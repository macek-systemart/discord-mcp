using System.Net;
using DiscordMcp.Discord;
using DiscordMcp.Http;

namespace DiscordMcp.Tests;

public class GovernorTests
{
    private const string Guild = "guilds/111111111111111111";

    [Fact]
    public async Task Requests_are_spaced_at_least_one_second_apart()
    {
        using var pipeline = new ApiPipeline(new FakeHandler());
        await pipeline.Governor.WaitTurnAsync(CancellationToken.None);

        var second = pipeline.Governor.WaitTurnAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        pipeline.Time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(second.IsCompleted);
        pipeline.Time.Advance(TimeSpan.FromMilliseconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Parallel_callers_queue_behind_one_gate()
    {
        using var pipeline = new ApiPipeline(new FakeHandler());
        var released = new List<DateTimeOffset>();
        var calls = Enumerable.Range(0, 3)
            .Select(_ => pipeline.Governor.WaitTurnAsync(CancellationToken.None)
                .ContinueWith(_ => { lock (released) { released.Add(pipeline.Time.GetUtcNow()); } }, TaskContinuationOptions.ExecuteSynchronously))
            .ToList();

        // Step fake time in small increments; continuations may register their timers asynchronously.
        for (var i = 0; i < 100 && !calls.All(c => c.IsCompleted); i++)
        {
            pipeline.Time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(5);
        }

        await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));
        var times = released.Order().ToList();
        Assert.Equal(3, times.Count);
        Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(1));
        Assert.True(times[2] - times[1] >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Cancelled_wait_sends_nothing()
    {
        var network = new FakeHandler();
        using var pipeline = new ApiPipeline(network);
        using var _ = await pipeline.Client.GetAsync(Guild);

        using var cts = new CancellationTokenSource();
        var pending = pipeline.Client.GetAsync(Guild, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Single(network.Requests);
    }

    [Fact]
    public async Task A_401_latches_a_persisted_hard_stop()
    {
        var network = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.Unauthorized, """{"message":"401: Unauthorized","code":0}"""));
        using var pipeline = new ApiPipeline(network);

        using (var response = await pipeline.Client.GetAsync(Guild))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        pipeline.Time.Advance(TimeSpan.FromHours(1));
        var e = await Assert.ThrowsAsync<DiscordApiException>(() => pipeline.Client.GetAsync(Guild));
        Assert.Equal(DiscordErrorKind.HardStop, e.Kind);
        Assert.Contains("set-token", e.Message, StringComparison.Ordinal);
        Assert.Single(network.Requests);

        // A restart (new state store and governor over the same file) keeps the latch.
        var restarted = pipeline.NewGovernor(new StateStore(pipeline.Paths));
        Assert.Equal(DiscordErrorKind.HardStop, Assert.Throws<DiscordApiException>(restarted.ThrowIfStopped).Kind);

        restarted.ClearHardStop();
        restarted.ThrowIfStopped();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"captcha_key":["captcha-required"],"captcha_sitekey":"x","captcha_service":"hcaptcha"}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"message":"You need to verify your account in order to perform this action.","code":40002}""")]
    public async Task Captcha_or_verification_latches_a_hard_stop(HttpStatusCode status, string body)
    {
        var network = new FakeHandler(_ => FakeHandler.Json(status, body));
        using var pipeline = new ApiPipeline(network);

        using (var response = await pipeline.Client.GetAsync(Guild))
        {
            // The body stays readable for the caller after the governor inspected it.
            Assert.Equal(body, await response.Content.ReadAsStringAsync());
        }

        pipeline.Time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(DiscordErrorKind.HardStop, (await Assert.ThrowsAsync<DiscordApiException>(() => pipeline.Client.GetAsync(Guild))).Kind);
    }

    [Fact]
    public async Task An_ordinary_403_does_not_latch()
    {
        var network = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.Forbidden, """{"message":"Missing Access","code":50001}"""));
        using var pipeline = new ApiPipeline(network);

        using var _ = await pipeline.Client.GetAsync(Guild);
        pipeline.Governor.ThrowIfStopped();
    }

    [Fact]
    public async Task A_429_blocks_every_request_until_retry_after()
    {
        var network = new FakeHandler(_ => FakeHandler.Json(HttpStatusCode.TooManyRequests, """{"message":"You are being rate limited.","retry_after":3.5,"global":false}"""));
        using var pipeline = new ApiPipeline(network);

        using (await pipeline.Client.GetAsync(Guild))
        {
        }

        pipeline.Time.Advance(TimeSpan.FromSeconds(3));
        var e = await Assert.ThrowsAsync<DiscordApiException>(() => pipeline.Client.GetAsync(Guild));
        Assert.Equal(DiscordErrorKind.RateLimited, e.Kind);
        Assert.Single(network.Requests);

        pipeline.Time.Advance(TimeSpan.FromSeconds(1));
        pipeline.Governor.ThrowIfStopped();
    }

    [Fact]
    public async Task A_429_without_any_hint_backs_off_for_a_minute()
    {
        var network = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        using var pipeline = new ApiPipeline(network);

        using (await pipeline.Client.GetAsync(Guild))
        {
        }

        pipeline.Time.Advance(TimeSpan.FromSeconds(59));
        Assert.Throws<DiscordApiException>(pipeline.Governor.ThrowIfStopped);
        pipeline.Time.Advance(TimeSpan.FromSeconds(2));
        pipeline.Governor.ThrowIfStopped();
    }
}
