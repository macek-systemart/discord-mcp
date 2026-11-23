using System.Collections.Concurrent;
using DiscordMcp.Localization;
using Microsoft.Extensions.Options;

namespace DiscordMcp.Discord;

/// <summary>A channel or thread of the configured guild that the current user can see.</summary>
/// <param name="Type">Channel type; a thread known only from a type-18 notice is reported as a public thread.</param>
public sealed record ResolvedChannel(ulong Id, int Type, string Name, ChannelAccess Access, ulong? ParentId)
{
    public bool IsThread => ChannelTypes.Threads.Contains(Type);
}

/// <summary>
/// Fail-closed classification of channel ids coming from Claude. Cached channels and known threads cost nothing; any
/// other id costs exactly one <c>GET /channels/{id}</c> and is accepted only as a visible thread of the configured
/// guild. Refusals are remembered for GuildCacheTtlMinutes.
/// </summary>
public sealed class ChannelResolver(GuildContext context, IDiscordApi api, IOptions<DiscordOptions> options, TimeProvider time)
{
    private readonly ConcurrentDictionary<ulong, (string Message, DateTimeOffset Until)> _refused = new();

    /// <summary>Any visible channel or thread (categories and forums included).</summary>
    public async Task<ResolvedChannel> ResolveAsync(ulong channelId, CancellationToken cancellationToken)
    {
        var guild = await context.GetAsync(cancellationToken).ConfigureAwait(false);
        if (guild.ChannelsById.TryGetValue(channelId, out var channel))
        {
            var access = guild.AccessOf(channelId);
            return access.View
                ? new ResolvedChannel(channel.Id, channel.Type, channel.Name ?? "", access, channel.ParentId)
                : throw NotAllowed(Languages.Current.ChannelNotVisible(channelId));
        }

        if (context.FindThread(channelId) is { } known)
        {
            return AsVisibleThread(guild, known) ?? throw NotAllowed(HiddenThread(channelId));
        }

        if (_refused.TryGetValue(channelId, out var refusal) && refusal.Until > time.GetUtcNow())
        {
            throw NotAllowed(refusal.Message);
        }

        DiscordChannel fetched;
        try
        {
            fetched = await api.GetChannelAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (DiscordApiException e) when (e.Kind is DiscordErrorKind.Forbidden or DiscordErrorKind.NotFound)
        {
            throw Refuse(channelId, Outside(channelId));
        }

        if (fetched.GuildId != guild.Guild.Id || fetched.Id != channelId)
        {
            throw Refuse(channelId, Outside(channelId));
        }

        if (!fetched.IsThread || fetched.ParentId is not { } parentId)
        {
            throw Refuse(channelId, Languages.Current.NotInChannelList(channelId, options.Value.GuildCacheTtlMinutes));
        }

        var thread = new KnownThread(fetched.Id, parentId, fetched.Type, fetched.Name, fetched.Member is not null, fetched.MessageCount);
        var resolved = AsVisibleThread(guild, thread) ?? throw Refuse(channelId, HiddenThread(channelId));
        context.Remember(fetched, isMember: false);
        return resolved;
    }

    /// <summary>A channel whose messages can be read, with history access. Refusals cost no request.</summary>
    public async Task<ResolvedChannel> ResolveReadableAsync(ulong channelId, CancellationToken cancellationToken)
    {
        var channel = await ResolveAsync(channelId, cancellationToken).ConfigureAwait(false);
        EnsureReadable(channel);
        return channel;
    }

    /// <summary>
    /// The channel holding <paramref name="messageId"/>. For a forum or media channel the message id is the post, whose
    /// thread has the same id.
    /// </summary>
    public async Task<ResolvedChannel> ResolveForMessageAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken)
    {
        var channel = await ResolveAsync(channelId, cancellationToken).ConfigureAwait(false);
        if (ChannelTypes.ThreadOnly.Contains(channel.Type))
        {
            if (!channel.Access.History)
            {
                throw NoHistory(channel.Id);
            }

            channel = await ResolveAsync(messageId, cancellationToken).ConfigureAwait(false);
            if (channel.ParentId != channelId)
            {
                throw NotAllowed(Languages.Current.PostNotInForum(messageId, channelId));
            }
        }

        EnsureReadable(channel);
        return channel;
    }

    public static void EnsureReadable(ResolvedChannel channel)
    {
        if (ChannelTypes.ThreadOnly.Contains(channel.Type))
        {
            throw NotAllowed(Languages.Current.ChannelIsForum(channel.Id));
        }

        if (!ChannelTypes.Readable.Contains(channel.Type))
        {
            throw NotAllowed(Languages.Current.ChannelHasNoMessages(channel.Id, channel.Type));
        }

        if (!channel.Access.History)
        {
            throw NoHistory(channel.Id);
        }
    }

    internal static DiscordApiException NoHistory(ulong channelId) =>
        NotAllowed(Languages.Current.NoHistoryAccess(channelId));

    private static ResolvedChannel? AsVisibleThread(GuildSnapshot guild, KnownThread thread)
    {
        if (!guild.ChannelsById.ContainsKey(thread.ParentId))
        {
            return null;
        }

        var access = Visibility.ForThread(guild.AccessOf(thread.ParentId), thread.Type == ChannelTypes.PrivateThread, thread.IsMember);
        return access.View
            ? new ResolvedChannel(thread.Id, thread.Type ?? ChannelTypes.PublicThread, thread.Name ?? "", access, thread.ParentId)
            : null;
    }

    private DiscordApiException Refuse(ulong channelId, string message)
    {
        _refused[channelId] = (message, time.GetUtcNow() + TimeSpan.FromMinutes(options.Value.GuildCacheTtlMinutes));
        return NotAllowed(message);
    }

    private static string Outside(ulong channelId) => Languages.Current.OutsideServer(channelId);

    private static string HiddenThread(ulong channelId) => Languages.Current.ThreadHidden(channelId);

    private static DiscordApiException NotAllowed(string message) => new(DiscordErrorKind.NotAllowed, message);
}
