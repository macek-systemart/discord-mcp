namespace DiscordMcp.Discord;

public static class PermissionBits
{
    public const ulong Administrator = 1UL << 3;
    public const ulong ViewChannel = 1UL << 10;
    public const ulong ReadMessageHistory = 1UL << 16;
    public const ulong ManageThreads = 1UL << 34;
    public const ulong All = ulong.MaxValue;
}

public enum OverwriteType
{
    Role = 0,
    Member = 1,
}

public readonly record struct PermissionOverwrite(ulong Id, OverwriteType Type, ulong Allow, ulong Deny);

/// <summary>What the current user may do in one channel, as far as this read-only server cares.</summary>
public readonly record struct ChannelAccess(bool View, bool History, bool ManageThreads)
{
    public static ChannelAccess None => default;
}

/// <summary>The current user in one guild: everything needed to compute channel permissions locally.</summary>
/// <param name="RolePermissions">All guild roles (id → permission bits), including @everyone (id == guild id).</param>
/// <param name="RoleIds">The member's roles; never contains @everyone.</param>
public sealed record MemberContext(
    ulong GuildId,
    ulong OwnerId,
    ulong UserId,
    IReadOnlyDictionary<ulong, ulong> RolePermissions,
    IReadOnlySet<ulong> RoleIds);

/// <summary>
/// Discord's permission algorithm, computed locally so the server never probes channels it cannot see
/// (every 403 counts toward Discord's invalid-request limit).
/// </summary>
public static class Visibility
{
    /// <summary>Channel flag set on channels the user cannot view when Discord returns them obfuscated.</summary>
    public const ulong ChannelObfuscatedFlag = 1UL << 17;

    public static ulong BasePermissions(MemberContext me)
    {
        if (me.OwnerId == me.UserId)
        {
            return PermissionBits.All;
        }

        var permissions = me.RolePermissions.GetValueOrDefault(me.GuildId);
        foreach (var roleId in me.RoleIds)
        {
            permissions |= me.RolePermissions.GetValueOrDefault(roleId);
        }

        return (permissions & PermissionBits.Administrator) != 0 ? PermissionBits.All : permissions;
    }

    /// <summary>
    /// Applies the channel's own overwrites: @everyone (deny, then allow), all of the member's roles merged
    /// (deny, then allow), then the member overwrite. Category overwrites are never merged in: a child's
    /// <c>permission_overwrites</c> are authoritative.
    /// </summary>
    public static ulong ChannelPermissions(MemberContext me, IReadOnlyCollection<PermissionOverwrite> overwrites)
    {
        var permissions = BasePermissions(me);
        if (permissions == PermissionBits.All)
        {
            return permissions;
        }

        foreach (var o in overwrites)
        {
            if (o.Type == OverwriteType.Role && o.Id == me.GuildId)
            {
                permissions &= ~o.Deny;
                permissions |= o.Allow;
            }
        }

        ulong deny = 0, allow = 0;
        foreach (var o in overwrites)
        {
            if (o.Type == OverwriteType.Role && o.Id != me.GuildId && me.RoleIds.Contains(o.Id))
            {
                deny |= o.Deny;
                allow |= o.Allow;
            }
        }

        permissions &= ~deny;
        permissions |= allow;

        foreach (var o in overwrites)
        {
            if (o.Type == OverwriteType.Member && o.Id == me.UserId)
            {
                permissions &= ~o.Deny;
                permissions |= o.Allow;
            }
        }

        return permissions;
    }

    public static ChannelAccess Access(ulong permissions)
    {
        var view = (permissions & PermissionBits.ViewChannel) != 0;
        return new ChannelAccess(
            view,
            view && (permissions & PermissionBits.ReadMessageHistory) != 0,
            view && (permissions & PermissionBits.ManageThreads) != 0);
    }

    public static ChannelAccess ForChannel(MemberContext me, IReadOnlyCollection<PermissionOverwrite> overwrites, ulong channelFlags) =>
        (channelFlags & ChannelObfuscatedFlag) != 0 ? ChannelAccess.None : Access(ChannelPermissions(me, overwrites));

    /// <summary>Threads use the parent's permissions; a private thread also needs membership or MANAGE_THREADS.</summary>
    public static ChannelAccess ForThread(ChannelAccess parent, bool isPrivate, bool isMember) =>
        !parent.View || (isPrivate && !isMember && !parent.ManageThreads) ? ChannelAccess.None : parent;
}
