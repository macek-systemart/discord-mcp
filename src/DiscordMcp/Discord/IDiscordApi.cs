namespace DiscordMcp.Discord;

/// <summary>
/// Exactly one of <see cref="Before"/>, <see cref="After"/>, <see cref="Around"/> or none (= newest messages).
/// Discord returns pages newest-first whatever the anchor; callers sort. <see cref="BypassCache"/> forces a fresh
/// fetch (e.g. for a new signed attachment URL); the fresh page replaces the cached one.
/// </summary>
public sealed record MessageQuery(ulong? Before = null, ulong? After = null, ulong? Around = null, int Limit = 50, bool BypassCache = false)
{
    public void Validate()
    {
        var anchors = (Before is null ? 0 : 1) + (After is null ? 0 : 1) + (Around is null ? 0 : 1);
        if (anchors > 1)
        {
            throw new ArgumentException("before, after and around are mutually exclusive.");
        }

        if (Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), "limit must be 1-100.");
        }
    }
}

/// <summary><c>GET /channels/{c}/threads/search</c>. <see cref="Archived"/> null = active and archived.</summary>
public sealed record ThreadQuery(bool? Archived = null, int Offset = 0, int Limit = 25)
{
    public void Validate()
    {
        if (Limit is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), "limit must be 1-25.");
        }

        if (Offset is < 0 or > 9975)
        {
            throw new ArgumentOutOfRangeException(nameof(Offset), "offset must be 0-9975.");
        }
    }
}

/// <summary>
/// <c>GET /guilds/{g}/messages/search</c> for the configured guild. Dates are already converted to snowflakes
/// (<see cref="MinId"/>/<see cref="MaxId"/>). <c>include_nsfw=true</c> is always sent: Discord itself filters NSFW
/// results for accounts that are not allowed to see them.
/// </summary>
public sealed record SearchQuery
{
    public string? Content { get; init; }
    public ulong? AuthorId { get; init; }
    public ulong? ChannelId { get; init; }

    /// <summary>Subset of image, sound, video, file, sticker, embed, link, poll, snapshot (optionally prefixed with '-').</summary>
    public IReadOnlyList<string> Has { get; init; } = [];

    public ulong? MinId { get; init; }
    public ulong? MaxId { get; init; }
    public bool? Pinned { get; init; }

    /// <summary><c>timestamp</c> or <c>relevance</c>.</summary>
    public string SortBy { get; init; } = "timestamp";

    /// <summary><c>desc</c> or <c>asc</c> (ignored for relevance).</summary>
    public string SortOrder { get; init; } = "desc";

    public int Offset { get; init; }
    public int Limit { get; init; } = 25;

    public static readonly IReadOnlySet<string> HasValues =
        new HashSet<string>(StringComparer.Ordinal) { "image", "sound", "video", "file", "sticker", "embed", "link", "poll", "snapshot" };

    public void Validate()
    {
        if (Limit is < 1 or > 25)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), "limit must be 1-25.");
        }

        if (Offset is < 0 or > 9975)
        {
            throw new ArgumentOutOfRangeException(nameof(Offset), "offset must be 0-9975.");
        }

        if (SortBy is not ("timestamp" or "relevance") || SortOrder is not ("asc" or "desc"))
        {
            throw new ArgumentException("sort_by must be timestamp|relevance and sort_order asc|desc.");
        }

        if (Content is { Length: > 1024 })
        {
            throw new ArgumentException("query is limited to 1024 characters.");
        }

        if (Has.Any(h => !HasValues.Contains(h.TrimStart('-'))))
        {
            throw new ArgumentException($"has must be one of: {string.Join(", ", HasValues)}.");
        }
    }
}

/// <summary>A bounded attachment download. <see cref="ContentType"/> is the server's claim; callers sniff the bytes.</summary>
public sealed record DownloadedFile(byte[] Bytes, string? ContentType);

/// <summary>
/// Typed, read-only access to the configured guild. Implementations map failures to <see cref="DiscordApiException"/>
/// (never to raw HTTP exceptions), retry search 202s at most once, and never send anything the guards refuse.
/// Every call counts toward the current <see cref="Tools.ToolScope"/> budget.
/// </summary>
public interface IDiscordApi
{
    /// <summary><c>GET /guilds/{g}</c>, includes roles and owner_id.</summary>
    Task<DiscordGuild> GetGuildAsync(CancellationToken cancellationToken);

    /// <summary><c>GET /guilds/{g}/channels</c>, without threads.</summary>
    Task<IReadOnlyList<DiscordChannel>> GetChannelsAsync(CancellationToken cancellationToken);

    /// <summary><c>GET /users/@me/guilds/{g}/member</c></summary>
    Task<DiscordMember> GetMyMemberAsync(CancellationToken cancellationToken);

    /// <summary><c>GET /channels/{c}</c>, used to classify ids not in the cache (threads from links).</summary>
    Task<DiscordChannel> GetChannelAsync(ulong channelId, CancellationToken cancellationToken);

    /// <summary><c>GET /channels/{c}/messages</c>, in Discord's order (newest first).</summary>
    Task<IReadOnlyList<DiscordMessage>> GetMessagesAsync(ulong channelId, MessageQuery query, CancellationToken cancellationToken);

    /// <summary><c>GET /channels/{c}/messages/pins</c></summary>
    Task<PinsResponse> GetPinsAsync(ulong channelId, DateTimeOffset? before, int limit, CancellationToken cancellationToken);

    /// <summary><c>GET /channels/{c}/threads/search</c></summary>
    Task<ThreadSearchResponse> SearchThreadsAsync(ulong channelId, ThreadQuery query, CancellationToken cancellationToken);

    /// <summary><c>GET /guilds/{g}/messages/search</c>; throws <see cref="DiscordErrorKind.IndexNotReady"/> after a second 202.</summary>
    Task<MessageSearchResponse> SearchMessagesAsync(SearchQuery query, CancellationToken cancellationToken);

    /// <summary>Downloads an attachment through the CDN client (no token), aborting past <paramref name="maxBytes"/>.</summary>
    Task<DownloadedFile> DownloadAttachmentAsync(Uri url, long maxBytes, CancellationToken cancellationToken);
}
