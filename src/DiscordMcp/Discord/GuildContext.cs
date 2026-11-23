using System.Collections.Concurrent;
using DiscordMcp.Auth;
using DiscordMcp.Localization;
using DiscordMcp.Tools;
using Microsoft.Extensions.Options;

namespace DiscordMcp.Discord;

/// <summary>The guild, its channels and my computed access, as loaded at <see cref="LoadedAt"/>.</summary>
public sealed class GuildSnapshot
{
    public GuildSnapshot(DiscordGuild guild, MemberContext me, IReadOnlyList<DiscordChannel> channels, DateTimeOffset loadedAt)
    {
        Guild = guild;
        Me = me;
        Channels = channels;
        LoadedAt = loadedAt;
        var byId = new Dictionary<ulong, DiscordChannel>();
        var access = new Dictionary<ulong, ChannelAccess>();
        foreach (var channel in channels)
        {
            byId[channel.Id] = channel;
            access[channel.Id] = Visibility.ForChannel(me, channel.PermissionOverwrites.Select(o => o.ToOverwrite()).ToList(), channel.Flags);
        }

        ChannelsById = byId;
        Access = access;
        RoleNames = guild.Roles.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First().Name);
    }

    public DiscordGuild Guild { get; }

    public MemberContext Me { get; }

    public IReadOnlyList<DiscordChannel> Channels { get; }

    public IReadOnlyDictionary<ulong, DiscordChannel> ChannelsById { get; }

    /// <summary>My access per guild channel (threads are resolved by <see cref="ChannelResolver"/>).</summary>
    public IReadOnlyDictionary<ulong, ChannelAccess> Access { get; }

    public IReadOnlyDictionary<ulong, string> RoleNames { get; }

    public DateTimeOffset LoadedAt { get; }

    public ChannelAccess AccessOf(ulong channelId) => Access.GetValueOrDefault(channelId);
}

/// <summary>A thread seen in an earlier response. <see cref="Type"/> is null when only a type-18 message announced it.</summary>
public sealed record KnownThread(ulong Id, ulong ParentId, int? Type, string? Name, bool IsMember, int? MessageCount);

/// <summary>
/// Guild, channels and my member object (3 API calls outside the per-tool budget), cached for GuildCacheTtlMinutes and
/// loaded by one caller at a time. Also remembers threads seen in responses, so they resolve without extra calls.
/// </summary>
public sealed class GuildContext(IDiscordApi api, TokenHolder tokens, IOptions<DiscordOptions> options, TimeProvider time)
{
    private const int MaxThreads = 10_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, KnownThread> _threads = new();
    private volatile GuildSnapshot? _snapshot;

    /// <summary>Returns the cached snapshot or loads a new one. Refuses with 0 requests without a guild id or token.</summary>
    public async Task<GuildSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        var guildId = o.GuildId ?? throw new DiscordApiException(DiscordErrorKind.NotAllowed, o.GuildIdError ?? Languages.Current.GuildIdUnknown);
        var userId = tokens.Stored?.UserId ?? throw new DiscordApiException(DiscordErrorKind.NoToken, Languages.Current.NoTokenRetry);

        if (Fresh() is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh() is { } loaded)
            {
                return loaded;
            }

            using (ToolScope.Current?.Exempt())
            {
                var guild = await api.GetGuildAsync(cancellationToken).ConfigureAwait(false);
                var channels = await api.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
                var member = await api.GetMyMemberAsync(cancellationToken).ConfigureAwait(false);
                var rolePermissions = new Dictionary<ulong, ulong>();
                foreach (var role in guild.Roles)
                {
                    rolePermissions[role.Id] = role.Permissions;
                }

                var me = new MemberContext(guildId, guild.OwnerId, userId, rolePermissions, member.Roles.ToHashSet());
                var snapshot = new GuildSnapshot(guild, me, channels, time.GetUtcNow());
                _snapshot = snapshot;
                return snapshot;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public KnownThread? FindThread(ulong threadId) => _threads.TryGetValue(threadId, out var thread) ? thread : null;

    /// <summary>Threads from <c>threads/search</c> or message search; membership comes from the sibling <c>members</c> list.</summary>
    public void RememberThreads(IEnumerable<DiscordChannel>? threads, IEnumerable<ThreadMember>? members)
    {
        var joined = members?.Select(m => m.Id).OfType<ulong>().ToHashSet() ?? [];
        foreach (var thread in threads ?? [])
        {
            Remember(thread, joined.Contains(thread.Id));
        }
    }

    /// <summary>Threads announced by messages: the <c>thread</c> field and type-18 "thread created" notices.</summary>
    public void RememberMessages(IEnumerable<DiscordMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Thread is { } thread)
            {
                Remember(thread, isMember: false);
            }

            if (message.Type == MessageTypes.ThreadCreated && message.MessageReference is { ChannelId: { } threadId } reference
                && (reference.GuildId is null || reference.GuildId == options.Value.GuildId))
            {
                // The type is unknown here; the parent is the channel the notice was posted in. A thread already known
                // from a channel object keeps its authoritative data.
                Add(new KnownThread(threadId, message.ChannelId, null, message.Content, false, null), overwrite: false);
            }
        }
    }

    /// <summary>A thread classified by <see cref="ChannelResolver"/> with <c>GET /channels/{id}</c>.</summary>
    public void Remember(DiscordChannel thread, bool isMember)
    {
        if (!thread.IsThread || thread.ParentId is not { } parent || (thread.GuildId is { } guild && guild != options.Value.GuildId))
        {
            return;
        }

        var known = new KnownThread(thread.Id, parent, thread.Type, thread.Name, isMember || thread.Member is not null, thread.MessageCount);
        Add(known, overwrite: true);
    }

    private void Add(KnownThread thread, bool overwrite)
    {
        if (_threads.Count >= MaxThreads)
        {
            _threads.Clear();
        }

        if (!overwrite)
        {
            _threads.TryAdd(thread.Id, thread);
            return;
        }

        _threads.AddOrUpdate(
            thread.Id,
            thread,
            (_, old) => thread with
            {
                IsMember = thread.IsMember || old.IsMember,
                MessageCount = thread.MessageCount ?? old.MessageCount,
                Name = thread.Name ?? old.Name,
            });
    }

    private GuildSnapshot? Fresh()
    {
        var snapshot = _snapshot;
        var ttl = TimeSpan.FromMinutes(options.Value.GuildCacheTtlMinutes);
        return snapshot is not null && time.GetUtcNow() - snapshot.LoadedAt < ttl ? snapshot : null;
    }
}
