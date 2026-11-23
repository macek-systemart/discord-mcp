using System.Net;
using DiscordMcp.Discord;
using DiscordMcp.Http;
using DiscordMcp.Localization;

namespace DiscordMcp.Tests;

/// <summary>state.json stores codes, never display text: the text is rendered by whoever reports the stop.</summary>
public class PersistedStopTests
{
    private const string LegacyCzechReason = "token odmítnut (401)";

    private static readonly Texts English = Languages.Find("en")!;

    private static void WriteState(ApiHarness h, string reason) =>
        File.WriteAllText(h.Paths.StateFile, $$"""{"HardStopReason":"{{reason}}","HardStopAt":"2026-11-23T10:00:00+00:00"}""");

    [Fact]
    public async Task A_legacy_czech_reason_in_state_json_still_blocks_and_renders_the_generic_reason()
    {
        using var h = new ApiHarness();
        WriteState(h, LegacyCzechReason);

        var czech = Assert.Throws<DiscordApiException>(() => h.Governor.ThrowIfStopped());
        Assert.Equal(DiscordErrorKind.HardStop, czech.Kind);
        Assert.Contains("zastaveno dřívější chybou", czech.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LegacyCzechReason, czech.Message, StringComparison.Ordinal);
        Assert.Contains("set-token", czech.Message, StringComparison.Ordinal);

        using (Languages.Use(English))
        {
            var english = Assert.Throws<DiscordApiException>(() => h.Governor.ThrowIfStopped());
            Assert.Contains("stopped by an earlier error", english.Message, StringComparison.Ordinal);
            Assert.Contains("set-token", english.Message, StringComparison.Ordinal);
        }

        // A request through the full pipeline is refused before it reaches the network.
        await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(CancellationToken.None));
        Assert.Empty(h.Network.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("something nobody wrote")]
    public void An_unknown_stored_reason_blocks_too(string reason)
    {
        using var h = new ApiHarness();
        WriteState(h, reason);

        var e = Assert.Throws<DiscordApiException>(() => h.Governor.ThrowIfStopped());

        Assert.Equal(DiscordErrorKind.HardStop, e.Kind);
    }

    [Fact]
    public async Task A_401_stores_a_code_and_the_text_follows_the_language_of_the_reporter()
    {
        using var h = new ApiHarness(api: _ => FakeHandler.Json(HttpStatusCode.Unauthorized, """{"message":"401: Unauthorized","code":0}"""));

        await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(CancellationToken.None));

        Assert.Equal(GovernorState.UnauthorizedStop, h.State.Read(s => s.HardStopReason));
        Assert.Contains("\"unauthorized\"", File.ReadAllText(h.Paths.StateFile), StringComparison.Ordinal);
        Assert.DoesNotContain("odmítnut", File.ReadAllText(h.Paths.StateFile), StringComparison.Ordinal);
        Assert.Contains("token odmítnut (401)", Assert.Throws<DiscordApiException>(() => h.Governor.ThrowIfStopped()).Message, StringComparison.Ordinal);
        using (Languages.Use(English))
        {
            Assert.Contains("token rejected (401)", Assert.Throws<DiscordApiException>(() => h.Governor.ThrowIfStopped()).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_captcha_demand_stores_a_code_too()
    {
        using var h = new ApiHarness(api: _ => FakeHandler.Json(HttpStatusCode.Forbidden, """{"captcha_key":["captcha-required"],"captcha_service":"hcaptcha"}"""));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(CancellationToken.None));

        Assert.Equal(DiscordErrorKind.HardStop, e.Kind);
        Assert.Equal(GovernorState.CaptchaStop, h.State.Read(s => s.HardStopReason));
        using (Languages.Use(English))
        {
            Assert.Contains("captcha or account verification", Assert.Throws<DiscordApiException>(() => h.Governor.ThrowIfStopped()).Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_block_reason_is_a_code_as_well_and_the_clock_follows_the_display_zone()
    {
        using var h = new ApiHarness(api: _ => ApiHarness.RateLimited(120));

        await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(CancellationToken.None));

        Assert.Equal(GovernorState.RateLimitBlock, h.State.Read(s => s.BlockReason));
        using (Languages.Use(English))
        using (DisplayTime.Use("Asia/Tokyo"))
        {
            var e = Assert.Throws<DiscordApiException>(() => h.Governor.ThrowIfStopped());
            Assert.Equal(DiscordErrorKind.RateLimited, e.Kind);
            Assert.Contains("Discord rate limit: try again after 21:02:00", e.Message, StringComparison.Ordinal);
        }
    }
}
