using DiscordMcp.Discord;
using DiscordMcp.Tools;

namespace DiscordMcp.Tests;

public class GuildContextTests
{
    [Fact]
    public async Task Fixture_permissions_produce_the_documented_access()
    {
        using var h = new ToolsHarness();
        var guild = await h.GuildAsync();

        Assert.Equal(new ChannelAccess(true, true, false), guild.AccessOf(Fixture.TextChannel));
        Assert.Equal(new ChannelAccess(true, false, false), guild.AccessOf(Fixture.HistorylessChannel));
        Assert.False(guild.AccessOf(Fixture.ModeratorChannel).View);
        Assert.False(guild.AccessOf(Fixture.ObfuscatedChannel).View);
        Assert.Equal(Fixture.MyUserId, guild.Me.UserId);
        Assert.Equal("Moderátor", guild.RoleNames[500000000000000002]);
    }

    [Fact]
    public async Task Warm_up_costs_three_calls_once_per_ttl()
    {
        using var h = new ToolsHarness();
        await h.GuildAsync();
        await h.GuildAsync();
        Assert.Equal(["guild", "channels", "member"], h.Api.Calls);

        h.Time.Advance(TimeSpan.FromMinutes(h.Options.GuildCacheTtlMinutes));
        await h.GuildAsync();
        Assert.Equal(6, h.Api.Calls.Count);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_warm_up()
    {
        var gate = new TaskCompletionSource();
        var api = new ScriptedDiscordApi { GuildGate = gate.Task };
        using var h = new ToolsHarness(api);

        var first = h.GuildAsync();
        var second = h.GuildAsync();
        gate.SetResult();
        var guilds = await Task.WhenAll(first, second);

        Assert.Same(guilds[0], guilds[1]);
        Assert.Equal(["guild", "channels", "member"], api.Calls);
    }

    [Fact]
    public async Task A_failed_warm_up_is_not_cached()
    {
        var api = new ScriptedDiscordApi { Failure = new DiscordApiException(DiscordErrorKind.Upstream, "5xx") };
        using var h = new ToolsHarness(api);
        await Assert.ThrowsAsync<DiscordApiException>(h.GuildAsync);

        api.Failure = null;
        await h.GuildAsync();
        Assert.Equal(["guild", "guild", "channels", "member"], api.Calls);
    }

    [Fact]
    public async Task Missing_token_refuses_without_requests()
    {
        using var h = new ToolsHarness(withToken: false);
        var e = await Assert.ThrowsAsync<DiscordApiException>(h.GuildAsync);
        Assert.Equal(DiscordErrorKind.NoToken, e.Kind);
        Assert.Contains("set-token", e.Message, StringComparison.Ordinal);
        Assert.Empty(h.Api.Calls);
    }

    [Fact]
    public async Task Missing_guild_id_refuses_without_requests()
    {
        using var h = new ToolsHarness(configure: o => o.ApplyGuildId(null));
        var e = await Assert.ThrowsAsync<DiscordApiException>(h.GuildAsync);
        Assert.Equal(DiscordErrorKind.NotAllowed, e.Kind);
        Assert.Contains(DiscordOptions.GuildIdVariable, e.Message, StringComparison.Ordinal);
        Assert.Empty(h.Api.Calls);
    }

    [Fact]
    public async Task Warm_up_does_not_count_toward_the_tool_budget()
    {
        using var h = new ToolsHarness();
        using var scope = ToolScope.Begin("test", maxCalls: 1, h.Time.GetUtcNow().AddMinutes(1));
        await h.GuildAsync();
        Assert.Equal(0, scope.Calls);
        Assert.Equal(3, h.Api.Calls.Count);
    }

    [Fact]
    public void Threads_are_remembered_from_message_fields_and_thread_created_notices()
    {
        using var h = new ToolsHarness();
        h.Context.RememberMessages(Fixture.Load<List<DiscordMessage>>("messages.json"));

        var started = h.Context.FindThread(Fixture.StartedThread);
        Assert.NotNull(started);
        Assert.Equal(Fixture.TextChannel, started.ParentId);
        Assert.Equal(ChannelTypes.PublicThread, started.Type);
        Assert.True(started.IsMember);

        var announced = h.Context.FindThread(310000000000000001);
        Assert.NotNull(announced);
        Assert.Equal(Fixture.TextChannel, announced.ParentId);
        Assert.Null(announced.Type);
    }

    [Fact]
    public void Thread_search_membership_comes_from_the_members_list()
    {
        using var h = new ToolsHarness();
        var response = Fixture.Load<ThreadSearchResponse>("threads_search.json");
        h.Context.RememberThreads(response.Threads, response.Members);

        Assert.True(h.Context.FindThread(Fixture.StartedThread)!.IsMember);
        var secret = h.Context.FindThread(1290000000000000002)!;
        Assert.Equal(ChannelTypes.PrivateThread, secret.Type);
        Assert.False(secret.IsMember);
    }

    [Fact]
    public void A_thread_created_notice_never_overrides_a_known_thread()
    {
        using var h = new ToolsHarness();
        h.Context.Remember(ToolsData.Thread(310000000000000001, Fixture.ModeratorChannel, ChannelTypes.PrivateThread), isMember: false);
        h.Context.RememberMessages(Fixture.Load<List<DiscordMessage>>("messages.json"));

        var thread = h.Context.FindThread(310000000000000001)!;
        Assert.Equal(Fixture.ModeratorChannel, thread.ParentId);
        Assert.Equal(ChannelTypes.PrivateThread, thread.Type);
    }

    [Fact]
    public void Threads_of_another_guild_are_ignored()
    {
        using var h = new ToolsHarness();
        h.Context.Remember(ToolsData.Thread(1500000000000000001, Fixture.TextChannel, guildId: 990000000000000000), isMember: true);
        Assert.Null(h.Context.FindThread(1500000000000000001));
    }
}
