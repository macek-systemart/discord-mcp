using DiscordMcp.Discord;

namespace DiscordMcp.Tests;

public class ResolverTests
{
    private const ulong Dm = 1400000000000000001;
    private const ulong ForeignThread = 1400000000000000002;
    private const ulong UnknownThread = 1400000000000000003;
    private const ulong ForumPost = 1400000000000000004;

    private static async Task<(ToolsHarness H, List<string> Calls)> WarmAsync(Action<ScriptedDiscordApi>? setup = null)
    {
        var api = ScriptedDiscordApi.WithFixtureMessages();
        setup?.Invoke(api);
        var h = new ToolsHarness(api);
        await h.GuildAsync();
        api.Calls.Clear();
        return (h, api.Calls);
    }

    private static async Task<DiscordApiException> RefusedAsync(Task task)
    {
        var e = await Assert.ThrowsAsync<DiscordApiException>(() => task);
        Assert.Equal(DiscordErrorKind.NotAllowed, e.Kind);
        return e;
    }

    [Fact]
    public async Task Visible_cached_channel_costs_nothing()
    {
        var (h, calls) = await WarmAsync();
        using var _ = h;
        var channel = await h.Resolver.ResolveReadableAsync(Fixture.TextChannel, CancellationToken.None);
        Assert.Equal("obecny", channel.Name);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(Fixture.ModeratorChannel)]
    [InlineData(Fixture.ObfuscatedChannel)]
    public async Task Hidden_channels_are_refused_without_requests(ulong channelId)
    {
        var (h, calls) = await WarmAsync();
        using var _ = h;
        await RefusedAsync(h.Resolver.ResolveAsync(channelId, CancellationToken.None));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_dm_id_costs_exactly_one_classification_call()
    {
        var (h, calls) = await WarmAsync(api => api.ChannelLookup[Dm] = new DiscordChannel { Id = Dm, Type = ChannelTypes.Dm });
        using var _ = h;

        await RefusedAsync(h.Resolver.ResolveAsync(Dm, CancellationToken.None));
        await RefusedAsync(h.Resolver.ResolveReadableAsync(Dm, CancellationToken.None));

        Assert.Equal([$"channel:{Dm}"], calls);
    }

    [Fact]
    public async Task A_thread_of_another_guild_is_refused()
    {
        var (h, calls) = await WarmAsync(api => api.ChannelLookup[ForeignThread] = ToolsData.Thread(ForeignThread, Fixture.TextChannel, guildId: 990000000000000000));
        using var _ = h;
        await RefusedAsync(h.Resolver.ResolveAsync(ForeignThread, CancellationToken.None));
        Assert.Equal([$"channel:{ForeignThread}"], calls);
    }

    [Fact]
    public async Task An_unknown_id_is_refused_after_one_call_and_remembered()
    {
        var (h, calls) = await WarmAsync();
        using var _ = h;
        var e = await RefusedAsync(h.Resolver.ResolveAsync(UnknownThread, CancellationToken.None));
        Assert.Contains("nakonfigurovaného", e.Message, StringComparison.Ordinal);
        await RefusedAsync(h.Resolver.ResolveAsync(UnknownThread, CancellationToken.None));
        Assert.Equal([$"channel:{UnknownThread}"], calls);
    }

    [Fact]
    public async Task A_thread_from_search_resolves_without_extra_calls()
    {
        var (h, calls) = await WarmAsync();
        using var _ = h;
        var search = Fixture.Load<MessageSearchResponse>("search.json");
        h.Context.RememberThreads(search.Threads, search.Members);

        var thread = await h.Resolver.ResolveReadableAsync(Fixture.StartedThread, CancellationToken.None);

        Assert.True(thread.IsThread);
        Assert.Equal(Fixture.TextChannel, thread.ParentId);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_classified_thread_is_cached()
    {
        var (h, calls) = await WarmAsync(api => api.ChannelLookup[UnknownThread] = ToolsData.Thread(UnknownThread, Fixture.TextChannel));
        using var _ = h;
        await h.Resolver.ResolveReadableAsync(UnknownThread, CancellationToken.None);
        await h.Resolver.ResolveReadableAsync(UnknownThread, CancellationToken.None);
        Assert.Equal([$"channel:{UnknownThread}"], calls);
    }

    [Fact]
    public async Task A_thread_under_a_hidden_parent_is_refused()
    {
        var (h, calls) = await WarmAsync(api => api.ChannelLookup[UnknownThread] = ToolsData.Thread(UnknownThread, Fixture.ModeratorChannel));
        using var _ = h;

        await RefusedAsync(h.Resolver.ResolveAsync(UnknownThread, CancellationToken.None));
        Assert.Equal([$"channel:{UnknownThread}"], calls);

        // Known from a response: refused from the cache, no request.
        h.Context.Remember(ToolsData.Thread(ForeignThread, Fixture.ModeratorChannel), isMember: true);
        await RefusedAsync(h.Resolver.ResolveAsync(ForeignThread, CancellationToken.None));
        Assert.Single(calls);
    }

    [Fact]
    public async Task A_private_thread_needs_membership()
    {
        var (h, calls) = await WarmAsync(api =>
        {
            api.ChannelLookup[UnknownThread] = ToolsData.Thread(UnknownThread, Fixture.TextChannel, ChannelTypes.PrivateThread);
            api.ChannelLookup[ForeignThread] = ToolsData.Thread(ForeignThread, Fixture.TextChannel, ChannelTypes.PrivateThread, member: true);
        });
        using var _ = h;

        await RefusedAsync(h.Resolver.ResolveAsync(UnknownThread, CancellationToken.None));
        var joined = await h.Resolver.ResolveReadableAsync(ForeignThread, CancellationToken.None);
        Assert.Equal(ChannelTypes.PrivateThread, joined.Type);

        // A private thread listed by threads/search without membership is refused from the cache.
        var response = Fixture.Load<ThreadSearchResponse>("threads_search.json");
        h.Context.RememberThreads(response.Threads, response.Members);
        await RefusedAsync(h.Resolver.ResolveAsync(1290000000000000002, CancellationToken.None));
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task A_history_less_channel_is_refused_with_zero_requests()
    {
        var (h, calls) = await WarmAsync();
        using var _ = h;

        var e = await RefusedAsync(h.Resolver.ResolveReadableAsync(Fixture.HistorylessChannel, CancellationToken.None));
        Assert.Contains("bez přístupu k historii", e.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<DiscordApiException>(() => h.Messages.ReadMessagesAsync(Fixture.HistorylessChannel.ToString()));
        await Assert.ThrowsAsync<DiscordApiException>(() => h.Messages.ListPinsAsync(Fixture.HistorylessChannel.ToString()));
        await Assert.ThrowsAsync<DiscordApiException>(() => h.Messages.GetMessageAsync(Fixture.HistorylessChannel.ToString(), "1300000000000000010"));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(Fixture.ForumChannel, "list_threads")]
    [InlineData(300000000000000000UL, "kategorie")]
    [InlineData(Fixture.UnknownTypeChannel, "typ 20")]
    public async Task Channels_without_messages_are_refused_locally(ulong channelId, string hint)
    {
        var (h, calls) = await WarmAsync();
        using var _ = h;
        var e = await RefusedAsync(h.Resolver.ResolveReadableAsync(channelId, CancellationToken.None));
        Assert.Contains(hint, e.Message, StringComparison.Ordinal);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task A_forum_message_id_is_the_post_thread()
    {
        var (h, calls) = await WarmAsync(api => api.ChannelLookup[ForumPost] = ToolsData.Thread(ForumPost, Fixture.ForumChannel));
        using var _ = h;

        var post = await h.Resolver.ResolveForMessageAsync(Fixture.ForumChannel, ForumPost, CancellationToken.None);

        Assert.Equal(ForumPost, post.Id);
        Assert.Equal(Fixture.ForumChannel, post.ParentId);
        Assert.Equal([$"channel:{ForumPost}"], calls);
    }

    [Fact]
    public async Task A_post_of_another_forum_is_refused()
    {
        var (h, _) = await WarmAsync(api => api.ChannelLookup[ForumPost] = ToolsData.Thread(ForumPost, Fixture.TextChannel));
        using var __ = h;
        await RefusedAsync(h.Resolver.ResolveForMessageAsync(Fixture.ForumChannel, ForumPost, CancellationToken.None));
    }

    [Fact]
    public async Task A_thread_announced_by_a_notice_resolves_without_calls()
    {
        var (h, calls) = await WarmAsync();
        using var _ = h;
        h.Context.RememberMessages(Fixture.Load<List<DiscordMessage>>("messages.json"));

        var thread = await h.Resolver.ResolveReadableAsync(310000000000000001, CancellationToken.None);

        Assert.Equal(Fixture.TextChannel, thread.ParentId);
        Assert.Empty(calls);
    }
}
