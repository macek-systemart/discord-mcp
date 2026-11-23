using Microsoft.Extensions.Options;

namespace DiscordMcp.Discord;

/// <summary>
/// Decorator: short message-page cache (MessageCacheTtlSeconds) and a negative cache of 403/404 per channel
/// (GuildCacheTtlMinutes) so the same refusal is never requested twice — every 403 counts toward Discord's
/// invalid-request limit. Guild, channels and member are cached by GuildContext; search, pins and threads are not cached.
/// </summary>
public sealed class CachingDiscordApi(IDiscordApi inner, IOptions<DiscordOptions> options, TimeProvider time) : IDiscordApi
{
    /// <summary>Per cache; reaching it drops expired entries, and everything if none had expired.</summary>
    internal const int Capacity = 500;

    private readonly TtlCache<(ulong Channel, MessageQuery Query), IReadOnlyList<DiscordMessage>> _pages = new(time);
    private readonly TtlCache<ulong, DiscordApiException> _refused = new(time);

    public Task<DiscordGuild> GetGuildAsync(CancellationToken cancellationToken) => inner.GetGuildAsync(cancellationToken);

    public Task<IReadOnlyList<DiscordChannel>> GetChannelsAsync(CancellationToken cancellationToken) => inner.GetChannelsAsync(cancellationToken);

    public Task<DiscordMember> GetMyMemberAsync(CancellationToken cancellationToken) => inner.GetMyMemberAsync(cancellationToken);

    public Task<DiscordChannel> GetChannelAsync(ulong channelId, CancellationToken cancellationToken) =>
        ChannelScopedAsync(channelId, () => inner.GetChannelAsync(channelId, cancellationToken));

    public async Task<IReadOnlyList<DiscordMessage>> GetMessagesAsync(ulong channelId, MessageQuery query, CancellationToken cancellationToken)
    {
        ThrowIfRefused(channelId);
        var key = (channelId, query with { BypassCache = false });
        if (!query.BypassCache && _pages.TryGet(key, out var cached))
        {
            return cached;
        }

        var page = await ChannelScopedAsync(channelId, () => inner.GetMessagesAsync(channelId, key.Item2, cancellationToken)).ConfigureAwait(false);
        _pages.Set(key, page, TimeSpan.FromSeconds(options.Value.MessageCacheTtlSeconds));
        return page;
    }

    public Task<PinsResponse> GetPinsAsync(ulong channelId, DateTimeOffset? before, int limit, CancellationToken cancellationToken) =>
        ChannelScopedAsync(channelId, () => inner.GetPinsAsync(channelId, before, limit, cancellationToken));

    public Task<ThreadSearchResponse> SearchThreadsAsync(ulong channelId, ThreadQuery query, CancellationToken cancellationToken) =>
        ChannelScopedAsync(channelId, () => inner.SearchThreadsAsync(channelId, query, cancellationToken));

    public Task<MessageSearchResponse> SearchMessagesAsync(SearchQuery query, CancellationToken cancellationToken) =>
        inner.SearchMessagesAsync(query, cancellationToken);

    /// <summary>Not negative-cached: an expired attachment URL says nothing about its channel.</summary>
    public Task<DownloadedFile> DownloadAttachmentAsync(Uri url, long maxBytes, CancellationToken cancellationToken) =>
        inner.DownloadAttachmentAsync(url, maxBytes, cancellationToken);

    private async Task<T> ChannelScopedAsync<T>(ulong channelId, Func<Task<T>> call)
    {
        ThrowIfRefused(channelId);
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (DiscordApiException e) when (e.Kind is DiscordErrorKind.Forbidden or DiscordErrorKind.NotFound)
        {
            _refused.Set(channelId, e, TimeSpan.FromMinutes(options.Value.GuildCacheTtlMinutes));
            throw;
        }
    }

    private void ThrowIfRefused(ulong channelId)
    {
        if (_refused.TryGet(channelId, out var refusal))
        {
            throw new DiscordApiException(refusal.Kind, refusal.Message, refusal.RetryAt);
        }
    }

    private sealed class TtlCache<TKey, TValue>(TimeProvider time)
        where TKey : notnull
    {
        private readonly Dictionary<TKey, (TValue Value, DateTimeOffset Expires)> _items = [];
        private readonly Lock _lock = new();

        public bool TryGet(TKey key, out TValue value)
        {
            lock (_lock)
            {
                if (_items.TryGetValue(key, out var entry) && entry.Expires > time.GetUtcNow())
                {
                    value = entry.Value;
                    return true;
                }

                value = default!;
                return false;
            }
        }

        public void Set(TKey key, TValue value, TimeSpan ttl)
        {
            if (ttl <= TimeSpan.Zero)
            {
                return;
            }

            lock (_lock)
            {
                var now = time.GetUtcNow();
                if (_items.Count >= Capacity && !_items.ContainsKey(key))
                {
                    foreach (var expired in _items.Where(i => i.Value.Expires <= now).Select(i => i.Key).ToList())
                    {
                        _items.Remove(expired);
                    }

                    if (_items.Count >= Capacity)
                    {
                        _items.Clear();
                    }
                }

                _items[key] = (value, now + ttl);
            }
        }
    }
}
