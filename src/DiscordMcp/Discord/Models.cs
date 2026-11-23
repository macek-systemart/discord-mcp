using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordMcp.Discord;

/// <summary>
/// Shared serializer settings. Discord sends snowflakes and permission bitfields as JSON strings, so numbers are read
/// from strings; every id and bitfield is <c>ulong</c> (MANAGE_THREADS is bit 34). Unknown fields are ignored.
/// </summary>
public static class DiscordJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        PropertyNameCaseInsensitive = false,
    };
}

// ---- Users, guild, roles, member -------------------------------------------------------------------------------

public sealed class DiscordUser
{
    public ulong Id { get; init; }
    public string Username { get; init; } = "";
    public string? GlobalName { get; init; }
    public bool Bot { get; init; }

    /// <summary>Display name as the Discord client shows it (nicknames are not available to user-token REST).</summary>
    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(GlobalName) ? Username : GlobalName;
}

public sealed class DiscordRole
{
    public ulong Id { get; init; }
    public string Name { get; init; } = "";
    public ulong Permissions { get; init; }
    public int Position { get; init; }
}

public sealed class DiscordGuild
{
    public ulong Id { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public ulong OwnerId { get; init; }
    public List<DiscordRole> Roles { get; init; } = [];
}

/// <summary><c>GET /users/@me/guilds/{g}/member</c>; <see cref="Roles"/> never contains @everyone.</summary>
public sealed class DiscordMember
{
    public List<ulong> Roles { get; init; } = [];
    public string? Nick { get; init; }
}

// ---- Channels and threads ---------------------------------------------------------------------------------------

public static class ChannelTypes
{
    public const int Text = 0;
    public const int Dm = 1;
    public const int Voice = 2;
    public const int GroupDm = 3;
    public const int Category = 4;
    public const int Announcement = 5;
    public const int AnnouncementThread = 10;
    public const int PublicThread = 11;
    public const int PrivateThread = 12;
    public const int Stage = 13;
    public const int Directory = 14;
    public const int Forum = 15;
    public const int Media = 16;

    /// <summary>Channel types whose messages can be read with <c>GET /channels/{id}/messages</c>.</summary>
    public static readonly IReadOnlySet<int> Readable = new HashSet<int> { Text, Voice, Announcement, AnnouncementThread, PublicThread, PrivateThread, Stage };

    public static readonly IReadOnlySet<int> Threads = new HashSet<int> { AnnouncementThread, PublicThread, PrivateThread };

    /// <summary>Forum and media channels hold posts (threads) only.</summary>
    public static readonly IReadOnlySet<int> ThreadOnly = new HashSet<int> { Forum, Media };
}

public sealed class DiscordOverwrite
{
    public ulong Id { get; init; }

    /// <summary>0 = role, 1 = member.</summary>
    public int Type { get; init; }

    public ulong Allow { get; init; }
    public ulong Deny { get; init; }

    public PermissionOverwrite ToOverwrite() => new(Id, Type == 1 ? OverwriteType.Member : OverwriteType.Role, Allow, Deny);
}

public sealed class ThreadMetadata
{
    public bool Archived { get; init; }
    public DateTimeOffset? ArchiveTimestamp { get; init; }
    public bool Locked { get; init; }
    public DateTimeOffset? CreateTimestamp { get; init; }
}

public sealed class ThreadMember
{
    /// <summary>Thread id.</summary>
    public ulong? Id { get; init; }

    public ulong? UserId { get; init; }
    public DateTimeOffset? JoinTimestamp { get; init; }
}

public sealed class ForumTag
{
    public ulong Id { get; init; }
    public string Name { get; init; } = "";
}

/// <summary>Guild channel or thread. Thread-only fields are null for ordinary channels.</summary>
public sealed class DiscordChannel
{
    public ulong Id { get; init; }
    public int Type { get; init; }
    public ulong? GuildId { get; init; }
    public string? Name { get; init; }
    public string? Topic { get; init; }
    public int? Position { get; init; }
    public ulong? ParentId { get; init; }
    public bool Nsfw { get; init; }
    public ulong? LastMessageId { get; init; }
    public ulong Flags { get; init; }
    public List<DiscordOverwrite> PermissionOverwrites { get; init; } = [];
    public List<ForumTag>? AvailableTags { get; init; }

    // Threads
    public ulong? OwnerId { get; init; }
    public int? MessageCount { get; init; }
    public int? TotalMessageSent { get; init; }
    public int? MemberCount { get; init; }
    public ThreadMetadata? ThreadMetadata { get; init; }

    /// <summary>Present when the current user has joined the thread.</summary>
    public ThreadMember? Member { get; init; }

    public List<ulong>? AppliedTags { get; init; }

    [JsonIgnore]
    public bool IsThread => ChannelTypes.Threads.Contains(Type);
}

// ---- Messages ---------------------------------------------------------------------------------------------------

public static class MessageTypes
{
    public const int Default = 0;
    public const int ChannelPinnedMessage = 6;
    public const int UserJoin = 7;
    public const int GuildBoost = 8;
    public const int GuildBoostTier1 = 9;
    public const int GuildBoostTier2 = 10;
    public const int GuildBoostTier3 = 11;
    public const int ChannelFollowAdd = 12;
    public const int ThreadCreated = 18;
    public const int Reply = 19;
    public const int ChatInputCommand = 20;
    public const int ThreadStarterMessage = 21;
    public const int ContextMenuCommand = 23;
    public const int AutoModerationAction = 24;
    public const int PollResult = 46;
}

public static class MessageFlags
{
    public const ulong Crossposted = 1UL << 0;
    public const ulong IsCrosspost = 1UL << 1;
    public const ulong HasThread = 1UL << 5;
    public const ulong HasSnapshot = 1UL << 14;
    public const ulong IsComponentsV2 = 1UL << 15;
}

public sealed class MessageReference
{
    /// <summary>0 = default (reply, starter, pin, crosspost…), 1 = forward.</summary>
    public int Type { get; init; }

    public ulong? MessageId { get; init; }
    public ulong? ChannelId { get; init; }
    public ulong? GuildId { get; init; }
}

public sealed class DiscordAttachment
{
    public ulong Id { get; init; }
    public string Filename { get; init; } = "";
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? ContentType { get; init; }
    public long Size { get; init; }

    /// <summary>Signed CDN URL (<c>ex</c> = hex expiry). Never shown to Claude or logged.</summary>
    public string Url { get; init; } = "";

    public string? ProxyUrl { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSecs { get; init; }
}

public sealed class EmbedAuthor
{
    public string? Name { get; init; }
    public string? Url { get; init; }
}

public sealed class EmbedField
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
}

public sealed class EmbedMedia
{
    public string? Url { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
}

public sealed class EmbedProvider
{
    public string? Name { get; init; }
}

public sealed class EmbedFooter
{
    public string? Text { get; init; }
}

public sealed class DiscordEmbed
{
    public string? Type { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public string? Url { get; init; }
    public EmbedAuthor? Author { get; init; }
    public EmbedProvider? Provider { get; init; }
    public EmbedFooter? Footer { get; init; }
    public List<EmbedField>? Fields { get; init; }
    public EmbedMedia? Image { get; init; }
    public EmbedMedia? Thumbnail { get; init; }
    public EmbedMedia? Video { get; init; }
}

public sealed class ReactionEmoji
{
    public ulong? Id { get; init; }
    public string? Name { get; init; }
}

public sealed class DiscordReaction
{
    public int Count { get; init; }
    public bool Me { get; init; }
    public ReactionEmoji Emoji { get; init; } = new();
}

public sealed class PollMedia
{
    public string? Text { get; init; }
    public ReactionEmoji? Emoji { get; init; }
}

public sealed class PollAnswer
{
    public int AnswerId { get; init; }
    public PollMedia PollMedia { get; init; } = new();
}

public sealed class PollAnswerCount
{
    public int Id { get; init; }
    public int Count { get; init; }
    public bool MeVoted { get; init; }
}

public sealed class PollResults
{
    public bool IsFinalized { get; init; }
    public List<PollAnswerCount> AnswerCounts { get; init; } = [];
}

public sealed class DiscordPoll
{
    public PollMedia Question { get; init; } = new();
    public List<PollAnswer> Answers { get; init; } = [];
    public DateTimeOffset? Expiry { get; init; }
    public bool AllowMultiselect { get; init; }
    public PollResults? Results { get; init; }
}

public sealed class StickerItem
{
    public ulong Id { get; init; }
    public string Name { get; init; } = "";
}

/// <summary>The immutable copy inside a forward. It has no author.</summary>
public sealed class SnapshotMessage
{
    public int Type { get; init; }
    public string Content { get; init; } = "";
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset? EditedTimestamp { get; init; }
    public ulong Flags { get; init; }
    public List<DiscordUser> Mentions { get; init; } = [];
    public List<ulong> MentionRoles { get; init; } = [];
    public List<DiscordAttachment> Attachments { get; init; } = [];
    public List<DiscordEmbed> Embeds { get; init; } = [];
    public List<StickerItem>? StickerItems { get; init; }
}

public sealed class MessageSnapshot
{
    public SnapshotMessage Message { get; init; } = new();
}

public sealed class DiscordMessage
{
    public ulong Id { get; init; }
    public ulong ChannelId { get; init; }
    public int Type { get; init; }
    public string Content { get; init; } = "";
    public DiscordUser Author { get; init; } = new();
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset? EditedTimestamp { get; init; }
    public ulong Flags { get; init; }
    public bool Pinned { get; init; }
    public ulong? WebhookId { get; init; }
    public bool MentionEveryone { get; init; }
    public List<DiscordUser> Mentions { get; init; } = [];
    public List<ulong> MentionRoles { get; init; } = [];
    public List<DiscordAttachment> Attachments { get; init; } = [];
    public List<DiscordEmbed> Embeds { get; init; } = [];
    public List<DiscordReaction>? Reactions { get; init; }
    public List<StickerItem>? StickerItems { get; init; }
    public MessageReference? MessageReference { get; init; }

    /// <summary>
    /// Only for types 19, 21, 23. System.Text.Json cannot tell "absent" from "null": a deleted reply is a type-19
    /// message whose <see cref="MessageReference"/> is set while this is null.
    /// </summary>
    public DiscordMessage? ReferencedMessage { get; init; }

    public List<MessageSnapshot>? MessageSnapshots { get; init; }

    /// <summary>The thread started from this message (its id equals the message id).</summary>
    public DiscordChannel? Thread { get; init; }

    public DiscordPoll? Poll { get; init; }
    public int? Position { get; init; }
}

// ---- Paged responses --------------------------------------------------------------------------------------------

/// <summary><c>GET /channels/{c}/messages/pins</c></summary>
public sealed class PinsResponse
{
    public List<PinnedItem> Items { get; init; } = [];
    public bool HasMore { get; init; }
}

public sealed class PinnedItem
{
    public DateTimeOffset PinnedAt { get; init; }
    public DiscordMessage Message { get; init; } = new();
}

/// <summary><c>GET /channels/{c}/threads/search</c></summary>
public sealed class ThreadSearchResponse
{
    public List<DiscordChannel> Threads { get; init; } = [];
    public List<ThreadMember>? Members { get; init; }
    public bool HasMore { get; init; }
    public int? TotalResults { get; init; }

    /// <summary>Only in forum and media channels: the post's first message.</summary>
    public List<DiscordMessage>? FirstMessages { get; init; }
}

/// <summary><c>GET /guilds/{g}/messages/search</c>. <see cref="Messages"/> is an array of arrays with one message each.</summary>
public sealed class MessageSearchResponse
{
    public int TotalResults { get; init; }
    public List<List<DiscordMessage>> Messages { get; init; } = [];
    public List<DiscordChannel>? Threads { get; init; }
    public List<ThreadMember>? Members { get; init; }
    public bool? DoingDeepHistoricalIndex { get; init; }
}
