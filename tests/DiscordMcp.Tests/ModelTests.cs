using DiscordMcp.Discord;

namespace DiscordMcp.Tests;

/// <summary>The frozen models read every fixture shape (string snowflakes, 64-bit permissions, nested search arrays).</summary>
public class ModelTests
{
    [Fact]
    public void Guild_reads_roles_owner_and_64_bit_permissions()
    {
        var guild = Fixture.Load<DiscordGuild>("guild.json");
        Assert.Equal(Fixture.GuildId, guild.Id);
        Assert.Equal(700000000000000009UL, guild.OwnerId);
        Assert.Equal(3, guild.Roles.Count);
        Assert.Equal(PermissionBits.ViewChannel | PermissionBits.ReadMessageHistory, guild.Roles.Single(r => r.Id == Fixture.GuildId).Permissions);
        Assert.Equal(PermissionBits.ManageThreads, guild.Roles.Single(r => r.Name == "Moderátor").Permissions);
    }

    [Fact]
    public void Channels_read_overwrites_flags_and_types()
    {
        var channels = Fixture.Load<List<DiscordChannel>>("channels.json");
        Assert.Equal(9, channels.Count);
        var moderators = channels.Single(c => c.Id == Fixture.ModeratorChannel);
        Assert.Equal(2, moderators.PermissionOverwrites.Count);
        Assert.Equal(PermissionBits.ViewChannel, moderators.PermissionOverwrites[0].Deny);
        Assert.Equal(Visibility.ChannelObfuscatedFlag, channels.Single(c => c.Id == Fixture.ObfuscatedChannel).Flags);
        Assert.Equal(20, channels.Single(c => c.Id == Fixture.UnknownTypeChannel).Type);
        Assert.Equal(1300000000000000011UL, channels.Single(c => c.Id == Fixture.TextChannel).LastMessageId);
        Assert.True(channels.Single(c => c.Id == 300000000000000008).Nsfw);
    }

    [Fact]
    public void Fixture_permissions_produce_the_documented_visibility()
    {
        var guild = Fixture.Load<DiscordGuild>("guild.json");
        var member = Fixture.Load<DiscordMember>("member.json");
        var me = new MemberContext(
            guild.Id, guild.OwnerId, Fixture.MyUserId,
            guild.Roles.ToDictionary(r => r.Id, r => r.Permissions),
            member.Roles.ToHashSet());
        var access = Fixture.Load<List<DiscordChannel>>("channels.json")
            .ToDictionary(c => c.Id, c => Visibility.ForChannel(me, c.PermissionOverwrites.Select(o => o.ToOverwrite()).ToList(), c.Flags));

        Assert.Equal(new ChannelAccess(true, true, false), access[Fixture.TextChannel]);
        Assert.Equal(new ChannelAccess(true, false, false), access[Fixture.HistorylessChannel]);
        Assert.False(access[Fixture.ModeratorChannel].View);
        Assert.False(access[Fixture.ObfuscatedChannel].View);
    }

    [Fact]
    public void Messages_read_every_documented_type()
    {
        var messages = Fixture.Load<List<DiscordMessage>>("messages.json");
        Assert.Equal(11, messages.Count);

        var mentions = messages.Single(m => m.Id == 1300000000000000010);
        Assert.Equal(2, mentions.Mentions.Count);
        Assert.Equal("graf.png", Assert.Single(mentions.Attachments).Filename);
        Assert.Equal(48213, mentions.Attachments[0].Size);
        Assert.Equal(2, mentions.Reactions!.Count);
        Assert.Equal("Článek", Assert.Single(mentions.Embeds).Title);
        Assert.NotNull(mentions.EditedTimestamp);

        var reply = messages.Single(m => m.Id == 1300000000000000009);
        Assert.Equal(MessageTypes.Reply, reply.Type);
        Assert.Equal(1300000000000000004UL, reply.ReferencedMessage!.Id);

        var deletedReply = messages.Single(m => m.Id == 1300000000000000008);
        Assert.NotNull(deletedReply.MessageReference);
        Assert.Null(deletedReply.ReferencedMessage);

        var threadCreated = messages.Single(m => m.Id == 1300000000000000007);
        Assert.Equal(MessageTypes.ThreadCreated, threadCreated.Type);
        Assert.Equal(310000000000000001UL, threadCreated.MessageReference!.ChannelId);

        var starter = messages.Single(m => m.Id == Fixture.StartedThread);
        Assert.Equal(MessageFlags.HasThread, starter.Flags);
        Assert.Equal(Fixture.StartedThread, starter.Thread!.Id);
        Assert.NotNull(starter.Thread.Member);

        var forward = messages.Single(m => m.Id == 1300000000000000005);
        Assert.Equal(1, forward.MessageReference!.Type);
        Assert.Equal("zapis.txt", Assert.Single(Assert.Single(forward.MessageSnapshots!).Message.Attachments).Filename);

        var poll = messages.Single(m => m.Id == 1300000000000000004).Poll!;
        Assert.Equal("Kdy se sejdeme?", poll.Question.Text);
        Assert.Equal(2, poll.Answers.Count);
        Assert.Equal(2, poll.Results!.AnswerCounts.Single(c => c.Id == 1).Count);

        Assert.Equal(MessageTypes.ChannelPinnedMessage, messages.Single(m => m.Id == 1300000000000000003).Type);
        Assert.Equal(MessageTypes.UserJoin, messages.Single(m => m.Id == 1300000000000000002).Type);

        var crosspost = messages.Single(m => m.Id == 1300000000000000001);
        Assert.Equal(MessageFlags.IsCrosspost, crosspost.Flags);
        Assert.Equal(990000000000000000UL, crosspost.MessageReference!.GuildId);
    }

    [Fact]
    public void Thread_starter_keeps_attachments_on_the_referenced_message()
    {
        var messages = Fixture.Load<List<DiscordMessage>>("thread_messages.json");
        var starter = messages.Single(m => m.Type == MessageTypes.ThreadStarterMessage);
        Assert.Empty(starter.Attachments);
        Assert.Equal("navrh.pdf", Assert.Single(starter.ReferencedMessage!.Attachments).Filename);
        Assert.Equal(1, messages.Single(m => m.Id == 1300000000000000101).Position);
    }

    [Fact]
    public void Search_reads_nested_messages_and_threads()
    {
        var search = Fixture.Load<MessageSearchResponse>("search.json");
        Assert.Equal(2, search.TotalResults);
        Assert.Equal(2, search.Messages.Count);
        Assert.All(search.Messages, inner => Assert.Single(inner));
        Assert.Equal(Fixture.StartedThread, Assert.Single(search.Threads!).Id);
    }

    [Fact]
    public void Thread_search_and_pins_read()
    {
        var threads = Fixture.Load<ThreadSearchResponse>("threads_search.json");
        Assert.Equal(3, threads.Threads.Count);
        Assert.True(threads.HasMore);
        Assert.Equal(7, threads.TotalResults);
        Assert.True(threads.Threads.Single(t => t.Id == 1290000000000000001).ThreadMetadata!.Archived);
        Assert.Equal(ChannelTypes.PrivateThread, threads.Threads.Single(t => t.Id == 1290000000000000002).Type);

        var pins = Fixture.Load<PinsResponse>("pins.json");
        Assert.False(pins.HasMore);
        Assert.True(Assert.Single(pins.Items).Message.Pinned);
    }

    [Fact]
    public void Display_name_prefers_global_name()
    {
        Assert.Equal("Petr", new DiscordUser { Username = "petr", GlobalName = "Petr" }.DisplayName);
        Assert.Equal("jana", new DiscordUser { Username = "jana", GlobalName = null }.DisplayName);
    }
}
