using DiscordMcp.Discord;
using Microsoft.Extensions.Time.Testing;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DiscordMcp.Tests;

public class ApiCacheTests
{
    private const ulong Text = Fixture.TextChannel;
    private const ulong Other = Fixture.VoiceChannel;
    private static readonly CancellationToken None = CancellationToken.None;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
    private readonly CountingApi _inner = new();

    private CachingDiscordApi Cache(int messageTtlSeconds = 120, int guildTtlMinutes = 10) =>
        new(_inner, MsOptions.Create(new DiscordOptions { MessageCacheTtlSeconds = messageTtlSeconds, GuildCacheTtlMinutes = guildTtlMinutes }), _time);

    [Fact]
    public async Task A_message_page_is_served_from_the_cache_until_it_expires()
    {
        var cache = Cache();
        var query = new MessageQuery(Before: 1300000000000000010);

        var first = await cache.GetMessagesAsync(Text, query, None);
        var again = await cache.GetMessagesAsync(Text, query with { }, None);
        Assert.Same(first, again);
        Assert.Equal(1, _inner.Count("messages"));

        await cache.GetMessagesAsync(Text, query with { Limit = 10 }, None);
        await cache.GetMessagesAsync(Other, query, None);
        Assert.Equal(3, _inner.Count("messages"));

        _time.Advance(TimeSpan.FromSeconds(120));
        await cache.GetMessagesAsync(Text, query, None);
        Assert.Equal(4, _inner.Count("messages"));
    }

    [Fact]
    public async Task A_zero_ttl_disables_the_page_cache()
    {
        var cache = Cache(messageTtlSeconds: 0);

        await cache.GetMessagesAsync(Text, new MessageQuery(), None);
        await cache.GetMessagesAsync(Text, new MessageQuery(), None);

        Assert.Equal(2, _inner.Count("messages"));
    }

    [Fact]
    public async Task Search_pins_threads_and_guild_calls_are_not_cached()
    {
        var cache = Cache();
        for (var i = 0; i < 2; i++)
        {
            await cache.SearchMessagesAsync(new SearchQuery(), None);
            await cache.GetPinsAsync(Text, null, 50, None);
            await cache.SearchThreadsAsync(Text, new ThreadQuery(), None);
            await cache.GetChannelAsync(Text, None);
            await cache.GetGuildAsync(None);
        }

        Assert.All(new[] { "search", "pins", "threads", "channel", "guild" }, name => Assert.Equal(2, _inner.Count(name)));
    }

    [Theory]
    [InlineData(DiscordErrorKind.Forbidden)]
    [InlineData(DiscordErrorKind.NotFound)]
    public async Task A_refused_channel_is_remembered_for_every_channel_scoped_call(DiscordErrorKind kind)
    {
        var cache = Cache(guildTtlMinutes: 10);
        _inner.FailChannel(Text, kind);

        var first = await Assert.ThrowsAsync<DiscordApiException>(() => cache.GetMessagesAsync(Text, new MessageQuery(), None));
        Assert.Equal(kind, first.Kind);
        Assert.Equal(1, _inner.Total);

        Func<Task>[] calls =
        [
            () => cache.GetMessagesAsync(Text, new MessageQuery(), None),
            () => cache.GetMessagesAsync(Text, new MessageQuery(Around: 5), None),
            () => cache.GetPinsAsync(Text, null, 50, None),
            () => cache.SearchThreadsAsync(Text, new ThreadQuery(), None),
            () => cache.GetChannelAsync(Text, None),
        ];
        foreach (var call in calls)
        {
            var e = await Assert.ThrowsAsync<DiscordApiException>(call);
            Assert.Equal(kind, e.Kind);
            Assert.Equal(first.Message, e.Message);
        }

        Assert.Equal(1, _inner.Total);

        // Other channels are unaffected; the refusal expires with the guild cache TTL.
        await cache.GetMessagesAsync(Other, new MessageQuery(), None);
        _time.Advance(TimeSpan.FromMinutes(10));
        await Assert.ThrowsAsync<DiscordApiException>(() => cache.GetPinsAsync(Text, null, 50, None));
        Assert.Equal(3, _inner.Total);
    }

    [Theory]
    [InlineData(DiscordErrorKind.RateLimited)]
    [InlineData(DiscordErrorKind.Upstream)]
    [InlineData(DiscordErrorKind.BudgetExceeded)]
    [InlineData(DiscordErrorKind.Unauthorized)]
    public async Task Other_failures_are_not_remembered(DiscordErrorKind kind)
    {
        var cache = Cache();
        _inner.FailChannel(Text, kind);

        await Assert.ThrowsAsync<DiscordApiException>(() => cache.GetMessagesAsync(Text, new MessageQuery(), None));
        await Assert.ThrowsAsync<DiscordApiException>(() => cache.GetMessagesAsync(Text, new MessageQuery(), None));

        Assert.Equal(2, _inner.Total);
    }

    [Fact]
    public async Task An_expired_attachment_does_not_mark_its_channel()
    {
        var cache = Cache();
        _inner.FailDownloads = true;

        await Assert.ThrowsAsync<DiscordApiException>(() => cache.DownloadAttachmentAsync(new Uri("https://cdn.discordapp.com/attachments/300000000000000001/1/a.png"), 10, None));
        await Assert.ThrowsAsync<DiscordApiException>(() => cache.DownloadAttachmentAsync(new Uri("https://cdn.discordapp.com/attachments/300000000000000001/1/a.png"), 10, None));
        await cache.GetMessagesAsync(Text, new MessageQuery(), None);

        Assert.Equal(2, _inner.Count("download"));
        Assert.Equal(1, _inner.Count("messages"));
    }

    [Fact]
    public async Task The_page_cache_is_bounded()
    {
        var cache = Cache();
        for (ulong i = 1; i <= CachingDiscordApi.Capacity; i++)
        {
            await cache.GetMessagesAsync(Text, new MessageQuery(Before: i), None);
        }

        await cache.GetMessagesAsync(Text, new MessageQuery(Before: 1), None);
        Assert.Equal(CachingDiscordApi.Capacity, _inner.Count("messages"));

        // One more distinct page while full (and nothing expired) drops the old pages.
        await cache.GetMessagesAsync(Text, new MessageQuery(Before: 100_000), None);
        await cache.GetMessagesAsync(Text, new MessageQuery(Before: 1), None);
        Assert.Equal(CachingDiscordApi.Capacity + 2, _inner.Count("messages"));
    }

    /// <summary>Counts calls per method; channel-scoped calls on a failing channel throw the configured kind.</summary>
    private sealed class CountingApi : IDiscordApi
    {
        private readonly Dictionary<string, int> _counts = [];
        private readonly Dictionary<ulong, DiscordErrorKind> _failing = [];

        public bool FailDownloads { get; set; }

        public int Total => _counts.Values.Sum();

        public int Count(string name) => _counts.GetValueOrDefault(name);

        public void FailChannel(ulong channelId, DiscordErrorKind kind) => _failing[channelId] = kind;

        public Task<DiscordGuild> GetGuildAsync(CancellationToken cancellationToken) => Done("guild", new DiscordGuild());

        public Task<IReadOnlyList<DiscordChannel>> GetChannelsAsync(CancellationToken cancellationToken) => Done<IReadOnlyList<DiscordChannel>>("channels", []);

        public Task<DiscordMember> GetMyMemberAsync(CancellationToken cancellationToken) => Done("member", new DiscordMember());

        public Task<DiscordChannel> GetChannelAsync(ulong channelId, CancellationToken cancellationToken) =>
            Done("channel", new DiscordChannel { Id = channelId }, channelId);

        public Task<IReadOnlyList<DiscordMessage>> GetMessagesAsync(ulong channelId, MessageQuery query, CancellationToken cancellationToken) =>
            Done<IReadOnlyList<DiscordMessage>>("messages", [new DiscordMessage { ChannelId = channelId }], channelId);

        public Task<PinsResponse> GetPinsAsync(ulong channelId, DateTimeOffset? before, int limit, CancellationToken cancellationToken) =>
            Done("pins", new PinsResponse(), channelId);

        public Task<ThreadSearchResponse> SearchThreadsAsync(ulong channelId, ThreadQuery query, CancellationToken cancellationToken) =>
            Done("threads", new ThreadSearchResponse(), channelId);

        public Task<MessageSearchResponse> SearchMessagesAsync(SearchQuery query, CancellationToken cancellationToken) =>
            Done("search", new MessageSearchResponse());

        public Task<DownloadedFile> DownloadAttachmentAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
        {
            _counts["download"] = Count("download") + 1;
            return FailDownloads
                ? Task.FromException<DownloadedFile>(new DiscordApiException(DiscordErrorKind.NotFound, "Příloha už není dostupná."))
                : Task.FromResult(new DownloadedFile([], null));
        }

        private Task<T> Done<T>(string name, T result, ulong? channelId = null)
        {
            _counts[name] = Count(name) + 1;
            return channelId is { } id && _failing.TryGetValue(id, out var kind)
                ? Task.FromException<T>(new DiscordApiException(kind, $"selhání {kind}"))
                : Task.FromResult(result);
        }
    }
}
