using DiscordMcp.Discord;
using static DiscordMcp.Discord.PermissionBits;

namespace DiscordMcp.Tests;

public class VisibilityTests
{
    private const ulong Guild = 100;
    private const ulong Me = 7;
    private const ulong Owner = 8;
    private const ulong RoleA = 201;
    private const ulong RoleB = 202;
    private const ulong RoleOther = 203;
    private const ulong Readable = ViewChannel | ReadMessageHistory;

    private static MemberContext Member(ulong everyone, params (ulong Id, ulong Perms)[] roles) => new(
        Guild,
        Owner,
        Me,
        new Dictionary<ulong, ulong>(roles.Select(r => KeyValuePair.Create(r.Id, r.Perms))) { [Guild] = everyone },
        roles.Select(r => r.Id).Where(id => id != RoleOther).ToHashSet());

    private static PermissionOverwrite Role(ulong id, ulong allow = 0, ulong deny = 0) => new(id, OverwriteType.Role, allow, deny);

    private static PermissionOverwrite User(ulong id, ulong allow = 0, ulong deny = 0) => new(id, OverwriteType.Member, allow, deny);

    [Fact]
    public void Everyone_permissions_are_the_base()
    {
        var me = Member(Readable);
        Assert.Equal(new ChannelAccess(true, true, false), Visibility.ForChannel(me, [], 0));
    }

    [Fact]
    public void Everyone_deny_with_a_role_allow_is_visible()
    {
        var me = Member(Readable, (RoleA, 0));
        Assert.True(Visibility.ForChannel(me, [Role(Guild, deny: ViewChannel), Role(RoleA, allow: ViewChannel)], 0).View);
    }

    [Fact]
    public void Role_overwrites_merge_deny_before_allow()
    {
        // One role denies, another allows: allow wins because all denies are applied first.
        var me = Member(Readable, (RoleA, 0), (RoleB, 0));
        Assert.True(Visibility.ForChannel(me, [Role(RoleA, deny: ViewChannel), Role(RoleB, allow: ViewChannel)], 0).View);
        Assert.True(Visibility.ForChannel(me, [Role(RoleB, allow: ViewChannel), Role(RoleA, deny: ViewChannel)], 0).View);
    }

    [Fact]
    public void Overwrites_of_roles_i_do_not_have_are_ignored()
    {
        var me = Member(Readable, (RoleA, 0), (RoleOther, 0));
        Assert.True(Visibility.ForChannel(me, [Role(RoleOther, deny: ViewChannel)], 0).View);
    }

    [Fact]
    public void Member_deny_beats_role_allow()
    {
        var me = Member(0, (RoleA, 0));
        Assert.False(Visibility.ForChannel(me, [Role(RoleA, allow: Readable), User(Me, deny: ViewChannel)], 0).View);
    }

    [Fact]
    public void Member_allow_beats_everyone_deny()
    {
        var me = Member(Readable);
        Assert.True(Visibility.ForChannel(me, [Role(Guild, deny: ViewChannel), User(Me, allow: ViewChannel)], 0).View);
    }

    [Fact]
    public void Overwrites_for_another_member_are_ignored()
    {
        var me = Member(Readable);
        Assert.True(Visibility.ForChannel(me, [User(999, deny: ViewChannel)], 0).View);
    }

    [Fact]
    public void Administrator_role_overrides_channel_denies()
    {
        var me = Member(0, (RoleA, Administrator));
        Assert.Equal(new ChannelAccess(true, true, true), Visibility.ForChannel(me, [Role(Guild, deny: ViewChannel), User(Me, deny: ViewChannel)], 0));
    }

    [Fact]
    public void Guild_owner_sees_everything()
    {
        var me = Member(0) with { OwnerId = Me };
        Assert.Equal(new ChannelAccess(true, true, true), Visibility.ForChannel(me, [Role(Guild, deny: ViewChannel)], 0));
    }

    [Fact]
    public void View_without_history_is_reported()
    {
        var me = Member(ViewChannel);
        Assert.Equal(new ChannelAccess(true, false, false), Visibility.ForChannel(me, [], 0));
    }

    [Fact]
    public void Without_view_nothing_else_counts()
    {
        var me = Member(ReadMessageHistory | ManageThreads);
        Assert.Equal(ChannelAccess.None, Visibility.ForChannel(me, [], 0));
    }

    [Fact]
    public void Child_with_explicit_allow_is_visible_even_if_its_category_is_hidden()
    {
        // Category overwrites are not merged into children: the child's own list decides.
        var me = Member(0, (RoleA, ReadMessageHistory));
        var category = Visibility.ForChannel(me, [], 0);
        var child = Visibility.ForChannel(me, [Role(RoleA, allow: ViewChannel)], 0);
        Assert.False(category.View);
        Assert.True(child.View);
    }

    [Fact]
    public void Obfuscated_channels_are_hidden_even_with_permissions()
    {
        var me = Member(0, (RoleA, Administrator));
        Assert.Equal(ChannelAccess.None, Visibility.ForChannel(me, [], Visibility.ChannelObfuscatedFlag));
    }

    [Fact]
    public void Manage_threads_bit_needs_64_bit_math()
    {
        var me = Member(Readable | ManageThreads);
        Assert.True(Visibility.ForChannel(me, [], 0).ManageThreads);
    }

    [Fact]
    public void Threads_follow_the_parent_and_private_ones_need_membership()
    {
        var parent = new ChannelAccess(true, true, false);
        Assert.Equal(parent, Visibility.ForThread(parent, isPrivate: false, isMember: false));
        Assert.Equal(ChannelAccess.None, Visibility.ForThread(parent, isPrivate: true, isMember: false));
        Assert.Equal(parent, Visibility.ForThread(parent, isPrivate: true, isMember: true));
        var moderator = parent with { ManageThreads = true };
        Assert.Equal(moderator, Visibility.ForThread(moderator, isPrivate: true, isMember: false));
        Assert.Equal(ChannelAccess.None, Visibility.ForThread(ChannelAccess.None, isPrivate: false, isMember: true));
    }
}
