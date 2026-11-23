using System.ComponentModel;
using System.Text;
using DiscordMcp.Discord;
using DiscordMcp.Formatting;
using DiscordMcp.Localization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DiscordMcp.Tools;

/// <summary><c>server_overview</c> and <c>list_threads</c>.</summary>
[McpServerToolType]
public sealed class ServerTools(
    ToolRunner runner,
    GuildContext context,
    ChannelResolver resolver,
    MessageFormatter formatter,
    IDiscordApi api,
    IOptions<DiscordOptions> options)
{
    /// <summary>Channel types listed with a label; every other type goes under "other types".</summary>
    private static readonly HashSet<int> ListedTypes =
        [ChannelTypes.Text, ChannelTypes.Voice, ChannelTypes.Announcement, ChannelTypes.Stage, ChannelTypes.Forum, ChannelTypes.Media];

    private static string TypeLabel(Texts t, int type) => type switch
    {
        ChannelTypes.Text => t.TypeText,
        ChannelTypes.Voice => t.TypeVoice,
        ChannelTypes.Announcement => t.TypeAnnouncement,
        ChannelTypes.Stage => t.TypeStage,
        ChannelTypes.Forum => t.TypeForum,
        ChannelTypes.Media => t.TypeMedia,
        _ => "",
    };

    [McpServerTool(Name = "server_overview", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Overview of the configured Discord server: categories and the channels this account can see (type, id, topic, "
        + "NSFW flag, time of the last message, channels without history access). Hidden channels are only counted. "
        + "Start here to find channel ids. " + ToolInput.UntrustedNotice)]
    public Task<string> ServerOverviewAsync(CancellationToken cancellationToken) =>
        runner.RunAsync("server_overview", async ct => Overview(await context.GetAsync(ct).ConfigureAwait(false)), cancellationToken);

    [McpServerTool(Name = "list_threads", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Lists the threads of a text or announcement channel, or the posts of a forum or media channel, 25 per page. "
        + "Private threads you have not joined may be missing. Read a thread or post with read_messages using its id as channel_id. "
        + ToolInput.UntrustedNotice)]
    public Task<string> ListThreadsAsync(
        [Description("Channel id (from server_overview).")] string channel_id,
        [Description("true = only archived threads, false = only active ones, omit for both.")] bool? archived = null,
        [Description("Paging offset, from the offset=N cursor printed at the end of the previous page.")] int? offset = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("list_threads", async ct =>
        {
            var id = ToolInput.Id(channel_id, "channel_id");
            if (offset is < 0 or > 9975)
            {
                throw ToolInput.BadRequest(Languages.Current.OffsetRange);
            }

            var channel = await resolver.ResolveAsync(id, ct).ConfigureAwait(false);
            if (channel.Type is not (ChannelTypes.Text or ChannelTypes.Announcement or ChannelTypes.Forum or ChannelTypes.Media))
            {
                throw new DiscordApiException(DiscordErrorKind.NotAllowed, Languages.Current.ChannelHasNoThreads(id));
            }

            if (!channel.Access.History)
            {
                throw ChannelResolver.NoHistory(id);
            }

            var guild = await context.GetAsync(ct).ConfigureAwait(false);
            var response = await api.SearchThreadsAsync(id, new ThreadQuery(archived, offset ?? 0), ct).ConfigureAwait(false);
            context.RememberThreads(response.Threads, response.Members);
            return Threads(guild, id, response, offset ?? 0);
        }, cancellationToken);

    internal string Overview(GuildSnapshot guild)
    {
        var t = Languages.Current;
        var sb = new StringBuilder();
        sb.Append(MessageFormatter.UntrustedHeader).Append('\n');
        sb.Append(t.OverviewIntro(Sanitizer.Line(guild.Guild.Name, 100), MessageFormatter.Id(guild.Guild.Id))).Append('\n');

        var categories = guild.Channels.Where(c => c.Type == ChannelTypes.Category).OrderBy(c => c.Position ?? 0).ThenBy(c => c.Id).ToList();
        var categoryIds = categories.Select(c => c.Id).ToHashSet();
        var channels = guild.Channels.Where(c => c.Type != ChannelTypes.Category).ToList();
        var visible = channels.Where(c => guild.AccessOf(c.Id).View).ToList();
        var listed = visible.Where(c => ListedTypes.Contains(c.Type)).ToList();

        AppendGroup(sb, t, guild, t.NoCategory, listed.Where(c => c.ParentId is not { } p || !categoryIds.Contains(p)));
        foreach (var category in categories)
        {
            AppendGroup(sb, t, guild, "📁 " + Sanitizer.Line(category.Name, 100), listed.Where(c => c.ParentId == category.Id));
        }

        var other = visible.Where(c => !ListedTypes.Contains(c.Type)).OrderBy(c => c.Position ?? 0).ThenBy(c => c.Id).ToList();
        if (other.Count > 0)
        {
            sb.Append(t.OtherTypes).Append('\n');
            foreach (var c in other)
            {
                sb.Append("  #").Append(Sanitizer.Line(c.Name, 100)).Append(" (").Append(MessageFormatter.Id(c.Id)).Append(") ").Append(t.OtherTypeSuffix(c.Type)).Append('\n');
            }
        }

        var hidden = channels.Count - visible.Count;
        if (hidden > 0)
        {
            sb.Append(t.HiddenChannels(hidden)).Append('\n');
        }

        return Sanitizer.Truncate(sb.ToString().TrimEnd(), options.Value.MaxOutputChars);
    }

    private static void AppendGroup(StringBuilder sb, Texts t, GuildSnapshot guild, string heading, IEnumerable<DiscordChannel> channels)
    {
        var ordered = channels
            .OrderBy(c => c.Type is ChannelTypes.Voice or ChannelTypes.Stage ? 1 : 0)
            .ThenBy(c => c.Position ?? 0)
            .ThenBy(c => c.Id)
            .ToList();
        if (ordered.Count == 0)
        {
            return;
        }

        sb.Append(heading).Append('\n');
        foreach (var c in ordered)
        {
            sb.Append("  #").Append(Sanitizer.Line(c.Name, 100)).Append(" (").Append(MessageFormatter.Id(c.Id)).Append(") ").Append(TypeLabel(t, c.Type));
            if (c.Nsfw)
            {
                sb.Append(" · ").Append(t.Nsfw);
            }

            if (!guild.AccessOf(c.Id).History)
            {
                sb.Append(" · ").Append(t.NoHistoryFlag);
            }
            else if (c.LastMessageId is { } last)
            {
                sb.Append(" · ").Append(t.LastActivity(DisplayTime.Format(Snowflake.ToTimestamp(last))));
            }

            if (!string.IsNullOrWhiteSpace(c.Topic))
            {
                sb.Append(" · ").Append(t.Topic(Sanitizer.Line(c.Topic, 150)));
            }

            sb.Append('\n');
        }
    }

    private string Threads(GuildSnapshot guild, ulong channelId, ThreadSearchResponse response, int offset)
    {
        var t = Languages.Current;
        var joined = response.Members?.Select(m => m.Id).OfType<ulong>().ToHashSet() ?? [];
        var head = new StringBuilder()
            .Append(MessageFormatter.UntrustedHeader).Append('\n')
            .Append(t.ThreadsHeader(formatter.Where(guild, channelId), response.TotalResults)).Append('\n')
            .ToString();
        if (response.Threads.Count == 0)
        {
            return head + (offset > 0 ? t.NothingAtOffset(offset) : t.NoThreads);
        }

        var sb = new StringBuilder(head);
        var budget = options.Value.MaxOutputChars - head.Length - 100;
        var shown = 0;
        foreach (var thread in response.Threads)
        {
            var line = MessageFormatter.ThreadLine(thread, joined.Contains(thread.Id) || thread.Member is not null);
            if (shown > 0 && sb.Length - head.Length + line.Length + 1 > budget)
            {
                break;
            }

            sb.Append(line).Append('\n');
            shown++;
        }

        var more = response.HasMore || shown < response.Threads.Count;
        sb.Append(more ? t.NextCursor(MessageFormatter.OffsetToken(offset + shown)) : t.EndOfList);
        return sb.ToString();
    }
}
