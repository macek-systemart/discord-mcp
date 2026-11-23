using DiscordMcp.Discord;
using DiscordMcp.Http;
using DiscordMcp.Tools;

namespace DiscordMcp.Tests;

public class GovernorBudgetTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static HttpResponseMessage Guild() => ApiHarness.Ok(Fixture.Text("guild.json"));

    private static DiscordOptions Limits(int per10Minutes = 60, int perDay = 300) => new()
    {
        GuildId = ApiHarness.GuildId,
        MaxApiCallsPer10Minutes = per10Minutes,
        MaxApiCallsPerDay = perDay,
    };

    [Fact]
    public async Task The_per_tool_budget_refuses_the_extra_call_without_sending()
    {
        using var h = new ApiHarness(_ => Guild());
        using var scope = ToolScope.Begin("read_messages", maxCalls: 2, h.Time.GetUtcNow() + TimeSpan.FromMinutes(1));

        await h.PumpAsync(h.Api.GetGuildAsync(None));
        await h.PumpAsync(h.Api.GetGuildAsync(None));
        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));

        Assert.Equal(DiscordErrorKind.BudgetExceeded, e.Kind);
        Assert.Contains("víc než 2 volání Discordu", e.Message, StringComparison.Ordinal);
        Assert.Equal(2, h.Network.Requests.Count);
    }

    [Fact]
    public async Task Retries_count_toward_the_per_tool_budget()
    {
        using var h = new ApiHarness(ApiHarness.Steps(() => ApiHarness.RateLimited(1), Guild));
        using var scope = ToolScope.Begin("server_overview", maxCalls: 1, h.Time.GetUtcNow() + TimeSpan.FromMinutes(1));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));

        Assert.Equal(DiscordErrorKind.BudgetExceeded, e.Kind);
        Assert.Single(h.Network.Requests);
    }

    [Fact]
    public async Task Exempt_requests_skip_the_tool_budget_but_not_the_rolling_budget()
    {
        using var h = new ApiHarness(_ => Guild(), options: Limits(per10Minutes: 3));
        using var scope = ToolScope.Begin("server_overview", maxCalls: 1, h.Time.GetUtcNow() + TimeSpan.FromMinutes(1));

        using (scope.Exempt())
        {
            await h.PumpAsync(h.Api.GetGuildAsync(None));
            await h.PumpAsync(h.Api.GetGuildAsync(None));
        }

        await h.PumpAsync(h.Api.GetGuildAsync(None));
        Assert.Equal(1, scope.Calls);

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));
        Assert.Equal(DiscordErrorKind.BudgetExceeded, e.Kind);
        Assert.Contains("za 10 minut", e.Message, StringComparison.Ordinal);
        Assert.Equal(3, h.Network.Requests.Count);
    }

    [Fact]
    public async Task The_10_minute_budget_refuses_until_the_oldest_call_leaves_the_window()
    {
        using var h = new ApiHarness(_ => Guild(), options: Limits(per10Minutes: 3));
        for (var i = 0; i < 3; i++)
        {
            await h.PumpAsync(h.Api.GetGuildAsync(None));
        }

        // Refused before any wait: the task completes without fake time moving.
        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5)));
        var retryAt = h.SentAt[0] + TimeSpan.FromMinutes(10);
        Assert.Equal(DiscordErrorKind.BudgetExceeded, e.Kind);
        Assert.Equal(retryAt, e.RetryAt);
        Assert.Contains("zeptej se uživatele", e.Message, StringComparison.Ordinal);
        Assert.Contains(DisplayTime.FormatClock(retryAt), e.Message, StringComparison.Ordinal);
        Assert.Equal(3, h.Network.Requests.Count);

        // A restart (new state store over the same state.json) still counts the calls.
        var restarted = h.NewGovernor(new StateStore(h.Paths));
        Assert.Equal(DiscordErrorKind.BudgetExceeded, (await Assert.ThrowsAsync<DiscordApiException>(() => restarted.WaitTurnAsync(None))).Kind);

        h.Time.Advance(retryAt - h.Time.GetUtcNow() - TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None));
        h.Time.Advance(TimeSpan.FromMilliseconds(1));
        await h.PumpAsync(h.Api.GetGuildAsync(None));
        Assert.Equal(4, h.Network.Requests.Count);
    }

    [Fact]
    public async Task The_daily_budget_survives_a_restart_and_names_the_reset_time()
    {
        using var h = new ApiHarness(_ => Guild(), options: Limits(perDay: 3));
        for (var i = 0; i < 3; i++)
        {
            await h.PumpAsync(h.Api.GetGuildAsync(None));
            h.Time.Advance(TimeSpan.FromHours(1));
        }

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5)));
        var retryAt = h.SentAt[0] + TimeSpan.FromHours(24);
        Assert.Equal(DiscordErrorKind.BudgetExceeded, e.Kind);
        Assert.Equal(retryAt, e.RetryAt);
        Assert.Contains("denní limit 3", e.Message, StringComparison.Ordinal);
        Assert.Contains("zeptej se uživatele", e.Message, StringComparison.Ordinal);
        Assert.Contains(DisplayTime.Format(retryAt), e.Message, StringComparison.Ordinal);

        // Persisted in state.json: a new store and governor refuse too, and nothing reaches the network.
        var restarted = h.NewGovernor(new StateStore(h.Paths));
        await Assert.ThrowsAsync<DiscordApiException>(() => restarted.WaitTurnAsync(None));
        Assert.Equal(3, h.Network.Requests.Count);

        h.Time.Advance(retryAt - h.Time.GetUtcNow());
        await h.PumpAsync(h.Api.GetGuildAsync(None));
        Assert.Equal(4, h.Network.Requests.Count);
    }

    [Fact]
    public async Task Calls_older_than_a_day_are_pruned_from_the_state_file()
    {
        using var h = new ApiHarness(_ => Guild());
        await h.PumpAsync(h.Api.GetGuildAsync(None));
        h.Time.Advance(TimeSpan.FromHours(25));
        await h.PumpAsync(h.Api.GetGuildAsync(None));

        var calls = new StateStore(h.Paths).Read(s => s.RecentCalls.ToList());
        Assert.Equal([h.SentAt[1]], calls);
    }

    [Fact]
    public async Task Five_403s_open_the_breaker_and_it_closes_after_the_open_period()
    {
        using var h = new ApiHarness(ApiHarness.Steps(
            ApiHarness.Forbidden, ApiHarness.Forbidden, ApiHarness.Forbidden, ApiHarness.Forbidden, ApiHarness.Forbidden, Guild));
        for (var i = 0; i < 5; i++)
        {
            var refused = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));

            // The fifth 403 opens the breaker, and that response already reports it.
            Assert.Equal(i < 4 ? DiscordErrorKind.Forbidden : DiscordErrorKind.BreakerOpen, refused.Kind);
        }

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5)));
        var closesAt = h.SentAt[4] + TimeSpan.FromMinutes(10);
        Assert.Equal(DiscordErrorKind.BreakerOpen, e.Kind);
        Assert.Equal(closesAt, e.RetryAt);
        Assert.Contains(DisplayTime.FormatClock(closesAt), e.Message, StringComparison.Ordinal);
        Assert.Equal(5, h.Network.Requests.Count);

        // Persisted: a restarted governor is still blocked.
        var restarted = h.NewGovernor(new StateStore(h.Paths));
        Assert.Equal(DiscordErrorKind.BreakerOpen, Assert.Throws<DiscordApiException>(restarted.ThrowIfStopped).Kind);

        h.Time.Advance(closesAt - h.Time.GetUtcNow());
        var guild = await h.PumpAsync(h.Api.GetGuildAsync(None));
        Assert.Equal("Testovací server", guild.Name);
        Assert.Equal(6, h.Network.Requests.Count);
    }

    [Fact]
    public async Task Forbidden_answers_older_than_the_window_do_not_open_the_breaker()
    {
        using var h = new ApiHarness(_ => ApiHarness.Forbidden());
        for (var i = 0; i < 8; i++)
        {
            await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));
            h.Time.Advance(TimeSpan.FromMinutes(3));
        }

        h.Governor.ThrowIfStopped();
        Assert.Equal(8, h.Network.Requests.Count);
    }

    [Fact]
    public async Task Shared_scope_429s_block_but_do_not_feed_the_breaker()
    {
        using var h = new ApiHarness(ApiHarness.Steps(
            ApiHarness.Forbidden, ApiHarness.Forbidden, ApiHarness.Forbidden, ApiHarness.Forbidden, () => ApiHarness.RateLimited(1, "shared"), Guild));
        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));
        }

        // The shared 429 is waited out and retried; it is not the fifth invalid request.
        var guild = await h.PumpAsync(h.Api.GetGuildAsync(None));
        Assert.Equal("Testovací server", guild.Name);
        Assert.True(h.SentAt[5] - h.SentAt[4] >= TimeSpan.FromSeconds(1));
        Assert.Equal(4, h.State.Read(s => s.RecentInvalid.Count));
        h.Governor.ThrowIfStopped();
    }

    [Fact]
    public async Task A_429_that_trips_the_breaker_reports_the_breaker()
    {
        using var h = new ApiHarness(ApiHarness.Steps(
            ApiHarness.Forbidden, ApiHarness.Forbidden, ApiHarness.Forbidden, ApiHarness.Forbidden, () => ApiHarness.RateLimited(3), Guild));
        for (var i = 0; i < 4; i++)
        {
            await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));
        }

        // The short retry_after would allow an automatic retry, but the breaker's longer block wins.
        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));
        Assert.Equal(DiscordErrorKind.BreakerOpen, e.Kind);
        Assert.Equal(h.SentAt[4] + TimeSpan.FromMinutes(10), e.RetryAt);
        Assert.Equal(5, h.Network.Requests.Count);
    }

    [Fact]
    public async Task A_wait_past_the_tool_deadline_fails_fast_without_sending()
    {
        using var h = new ApiHarness(_ => Guild());
        using var scope = ToolScope.Begin("read_messages", maxCalls: 5, h.Time.GetUtcNow() + TimeSpan.FromMilliseconds(500));
        await h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5));

        // The next pacing slot (1 s) lies after the deadline: no sleep, no request.
        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DiscordErrorKind.RateLimited, e.Kind);
        Assert.Single(h.Network.Requests);
    }

    [Fact]
    public async Task A_long_retry_after_fails_at_once_and_the_next_call_sends_nothing()
    {
        using var h = new ApiHarness(_ => ApiHarness.RateLimited(120));

        // Fake time never moves here, so any sleep would hang until WaitAsync gives up.
        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DiscordErrorKind.RateLimited, e.Kind);
        Assert.Equal(h.SentAt[0] + TimeSpan.FromSeconds(120), e.RetryAt);

        h.Time.Advance(TimeSpan.FromSeconds(30));
        var next = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(DiscordErrorKind.RateLimited, next.Kind);
        Assert.Single(h.Network.Requests);
    }

    [Fact]
    public async Task Cancelling_during_a_429_wait_sends_nothing_more()
    {
        using var h = new ApiHarness(ApiHarness.Steps(() => ApiHarness.RateLimited(3), Guild));
        using var cts = new CancellationTokenSource();

        var pending = h.Api.GetGuildAsync(cts.Token);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(pending.IsCompleted);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        h.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.Single(h.Network.Requests);
    }

    [Fact]
    public async Task A_refused_or_cancelled_slot_is_not_recorded()
    {
        using var h = new ApiHarness(_ => Guild(), options: Limits(per10Minutes: 2));
        await h.PumpAsync(h.Api.GetGuildAsync(None));

        using var cts = new CancellationTokenSource();
        var pending = h.Api.GetGuildAsync(cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, h.State.Read(s => s.RecentCalls.Count));
        await h.PumpAsync(h.Api.GetGuildAsync(None));
        Assert.Equal(2, h.Network.Requests.Count);
    }
}
