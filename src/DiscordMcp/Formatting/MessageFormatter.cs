using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiscordMcp.Discord;
using DiscordMcp.Localization;
using Microsoft.Extensions.Options;

namespace DiscordMcp.Formatting;

/// <summary>Which way a message page was requested; the output limit cuts the opposite side.</summary>
public enum PageAnchor
{
    Newest,
    Before,
    After,
    Around,
}

/// <summary>
/// Renders Discord data as compact text for Claude. Every result starts with <see cref="UntrustedHeader"/>; every
/// untrusted string passes through <see cref="Sanitizer"/>; lines written by this server start at column 0 with a
/// fixed token, untrusted text never does. Messages are listed oldest first; cursors only name rendered messages.
/// </summary>
public sealed partial class MessageFormatter(GuildContext context, IOptions<DiscordOptions> options)
{
    /// <summary>First line of every result, in the active language.</summary>
    public static string UntrustedHeader => Languages.Current.UntrustedHeader;

    public static string Legend => Languages.Current.Legend;

    private const string Detail = "  ";
    private const int FooterReserve = 300;
    private static readonly string[] CdnHosts = ["cdn.discordapp.com", "media.discordapp.net"];

    /// <summary>One message: a header line with the first content line, then indented continuation and detail lines.</summary>
    public string FormatMessage(GuildSnapshot guild, DiscordMessage message, bool detailed = false)
    {
        var t = Languages.Current;
        var starter = message.Type == MessageTypes.ThreadStarterMessage;
        var source = starter && message.ReferencedMessage is { } original ? original : message;
        var details = Details(guild, message, source, detailed);
        var label = Label(t, message);
        var text = IsSystem(message.Type) ? "" : Sanitizer.Indented(Mentions(guild, source.Content, source.Mentions), detailed ? 4000 : 2000);

        var sb = new StringBuilder();
        sb.Append('[').Append(DisplayTime.Format(message.Timestamp)).Append("] ").Append(Id(message.Id)).Append(' ');
        sb.Append(Author(guild, source.Author));
        if (source.EditedTimestamp is not null)
        {
            sb.Append(' ').Append(t.Edited);
        }

        if (label is not null)
        {
            sb.Append(' ').Append(label);
        }

        if (text.Length > 0)
        {
            sb.Append(": ").Append(text);
        }
        else if (label is null && details.Count == 0)
        {
            sb.Append(": ").Append(t.NoText);
        }

        foreach (var line in details)
        {
            sb.Append('\n').Append(Detail).Append(line);
        }

        return sb.ToString();
    }

    /// <summary>A page of channel messages, oldest first, cut to MaxOutputChars on the side away from the paging direction.</summary>
    public string RenderMessages(GuildSnapshot guild, string title, IReadOnlyList<DiscordMessage> fetched, PageAnchor anchor, int limit, ulong? around = null)
    {
        var t = Languages.Current;
        var head = $"{UntrustedHeader}\n{title}\n{Legend}\n";
        var messages = fetched.DistinctBy(m => m.Id).OrderBy(m => m.Id).ToList();
        if (messages.Count == 0)
        {
            return head + (anchor is PageAnchor.Newest ? t.NoMessagesInChannel : t.NoMessagesInDirection);
        }

        var blocks = messages.Select(m => FormatMessage(guild, m)).ToList();
        var (start, older, newer) = anchor switch
        {
            PageAnchor.After => (0, false, true),
            PageAnchor.Around => (CenterIndex(messages, around), true, true),
            _ => (messages.Count - 1, true, false),
        };
        var (first, last) = Window(blocks, Budget(head), start, older, newer);

        var olderEnd = anchor is PageAnchor.Newest or PageAnchor.Before && fetched.Count < limit && first == 0;
        var newerEnd = last == messages.Count - 1 && (anchor is PageAnchor.Newest || (anchor is PageAnchor.After && fetched.Count < limit));
        var shown = last - first + 1;

        var sb = new StringBuilder(head);
        for (var i = first; i <= last; i++)
        {
            sb.Append(blocks[i]).Append('\n');
        }

        sb.Append(t.ShownOf(shown, messages.Count));
        if (shown < messages.Count)
        {
            sb.Append(' ').Append(t.CutByOutputLimit);
        }

        sb.Append(" · ").Append(olderEnd ? t.OlderNone : t.OlderCursor(BeforeToken(Id(messages[first].Id))));
        sb.Append(" · ").Append(newerEnd ? t.NewerNone : t.NewerCursor(AfterToken(Id(messages[last].Id))));
        return sb.ToString();
    }

    /// <summary>
    /// Search hits: the longest prefix of Discord's order that fits, so <c>offset + shown</c> is exact. Time-sorted
    /// results are then listed oldest first; relevance keeps Discord's order.
    /// </summary>
    public string RenderSearch(GuildSnapshot guild, MessageSearchResponse response, int offset, int limit, bool chronological)
    {
        var t = Languages.Current;
        var head = $"{UntrustedHeader}\n{t.SearchHeader(response.TotalResults, chronological)}\n{Legend}\n";
        var hits = response.Messages.Where(g => g.Count > 0).ToList();
        if (hits.Count == 0)
        {
            return head + (offset > 0 ? t.NothingAtOffset(offset) : t.NothingFound);
        }

        var blocks = hits.Select(g => (Group: g, Text: string.Join("\n", g.Select(m => FormatMessage(guild, m))))).ToList();
        var shown = Prefix(blocks.Select(b => b.Text.Length + 80).ToList(), Budget(head));
        if (blocks[0].Text.Length + 80 > Budget(head))
        {
            blocks[0] = (blocks[0].Group, Sanitizer.Truncate(blocks[0].Text, Budget(head) - 80));
        }

        var listed = blocks.Take(shown);
        if (chronological)
        {
            listed = listed.OrderBy(b => b.Group[0].Id);
        }

        var sb = new StringBuilder(head);
        ulong? lastChannel = null;
        foreach (var (group, text) in listed)
        {
            if (group[0].ChannelId != lastChannel)
            {
                sb.Append("── ").Append(Where(guild, group[0].ChannelId)).Append('\n');
                lastChannel = group[0].ChannelId;
            }

            sb.Append(text).Append('\n');
        }

        // Discord may return fewer hits than the limit (results in channels the user cannot see are dropped), so a
        // short page is not the end: the offset counts index positions. When everything fetched was shown, move on by
        // the requested limit; when the output was cut, continue right after the last shown hit.
        var next = shown == hits.Count ? offset + limit : offset + shown;
        var end = next >= response.TotalResults || next > 9975;
        sb.Append(t.SearchShown(shown, offset + 1, Math.Min(next, response.TotalResults), response.TotalResults));
        sb.Append(" · ").Append(end ? t.EndOfResults : t.NextCursor(OffsetToken(next)));
        return sb.ToString();
    }

    /// <summary>Pinned messages, most recently pinned kept when cut; listed by pin time, oldest first.</summary>
    public string RenderPins(GuildSnapshot guild, string title, PinsResponse pins)
    {
        var t = Languages.Current;
        var head = $"{UntrustedHeader}\n{title}\n{Legend}\n";
        if (pins.Items.Count == 0)
        {
            return head + t.NoPins;
        }

        var blocks = pins.Items
            .Select(p => FormatMessage(guild, p.Message) + $"\n{Detail}{t.PinnedAt(DisplayTime.Format(p.PinnedAt))}")
            .ToList();
        var shown = Prefix(blocks.Select(b => b.Length + 1).ToList(), Budget(head));
        if (blocks[0].Length + 1 > Budget(head))
        {
            blocks[0] = Sanitizer.Truncate(blocks[0], Budget(head) - 1);
        }

        var sb = new StringBuilder(head);
        for (var i = shown - 1; i >= 0; i--)
        {
            sb.Append(blocks[i]).Append('\n');
        }

        sb.Append(t.ShownOf(shown, pins.Items.Count));
        var oldest = pins.Items[shown - 1].PinnedAt.ToUniversalTime();
        sb.Append(" · ").Append(pins.HasMore || shown < pins.Items.Count
            ? t.OlderPinsCursor(BeforeToken(oldest.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)))
            : t.NoMorePins);
        return sb.ToString();
    }

    /// <summary><c>🧵 id „name“ (flags)</c> for list_threads.</summary>
    public static string ThreadLine(DiscordChannel thread, bool isMember)
    {
        var t = Languages.Current;
        var flags = new List<string>();
        if (thread.Type == ChannelTypes.PrivateThread)
        {
            flags.Add(isMember ? t.ThreadPrivateMember : t.ThreadPrivate);
        }

        if (thread.ThreadMetadata is { Archived: true })
        {
            flags.Add(t.ThreadArchived);
        }

        if (thread.ThreadMetadata is { Locked: true })
        {
            flags.Add(t.ThreadLocked);
        }

        if (thread.MessageCount is { } count)
        {
            flags.Add(t.MessageCount(count));
        }

        var lastActivity = thread.LastMessageId is { } last ? Snowflake.ToTimestamp(last) : thread.ThreadMetadata?.ArchiveTimestamp;
        if (lastActivity is { } at)
        {
            flags.Add(t.LastActivity(DisplayTime.Format(at)));
        }

        return $"🧵 {Id(thread.Id)} {t.Quote(Sanitizer.Line(thread.Name, 100))}" + (flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "");
    }

    /// <summary>The name of a channel or known thread the user can see; null for anything else.</summary>
    public string? ChannelName(GuildSnapshot guild, ulong channelId)
    {
        if (guild.ChannelsById.TryGetValue(channelId, out var channel))
        {
            return guild.AccessOf(channelId).View ? Sanitizer.Line(channel.Name, 100) : null;
        }

        return context.FindThread(channelId) is { } thread && guild.ChannelsById.ContainsKey(thread.ParentId)
            && Visibility.ForThread(guild.AccessOf(thread.ParentId), thread.Type == ChannelTypes.PrivateThread, thread.IsMember).View
                ? Sanitizer.Line(thread.Name, 100)
                : null;
    }

    /// <summary><c>#name (id)</c>, or <c>🧵 „name“ (id) v #parent</c> for a thread.</summary>
    public string Where(GuildSnapshot guild, ulong channelId)
    {
        if (guild.ChannelsById.ContainsKey(channelId))
        {
            return $"#{ChannelName(guild, channelId) ?? "?"} ({Id(channelId)})";
        }

        if (context.FindThread(channelId) is { } thread && ChannelName(guild, channelId) is { } name)
        {
            return Languages.Current.ThreadInChannel(name, Id(channelId), ChannelName(guild, thread.ParentId) ?? "?");
        }

        return Languages.Current.ChannelFallback(Id(channelId));
    }

    internal static string Id(ulong id) => id.ToString(CultureInfo.InvariantCulture);

    // Cursor tokens are the same in every language: only the label in front of them is translated.
    internal static string OffsetToken(int offset) => $"offset={offset}";

    internal static string BeforeToken(string value) => $"before={value}";

    internal static string AfterToken(string value) => $"after={value}";

    internal static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024} kB",
        _ => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
    };

    internal static string AttachmentLine(DiscordAttachment a)
    {
        var parts = new List<string> { Size(a.Size) };
        if (!string.IsNullOrWhiteSpace(a.ContentType))
        {
            parts.Add(Sanitizer.Line(a.ContentType, 80));
        }

        if (a is { Width: { } w, Height: { } h })
        {
            parts.Add($"{w}×{h}");
        }

        if (a.DurationSecs is { } seconds)
        {
            parts.Add(seconds.ToString("0", CultureInfo.InvariantCulture) + " s");
        }

        return $"📎 {Id(a.Id)} {Sanitizer.Line(a.Filename, 100)} ({string.Join(", ", parts)})";
    }

    private int Budget(string head) => Math.Max(500, options.Value.MaxOutputChars - head.Length - FooterReserve);

    private static int CenterIndex(List<DiscordMessage> messages, ulong? around)
    {
        var index = around is { } id ? messages.FindIndex(m => m.Id >= id) : -1;
        return index < 0 ? messages.Count - 1 : index;
    }

    /// <summary>The contiguous range around <paramref name="start"/> that fits; the start block itself is always kept.</summary>
    private static (int First, int Last) Window(List<string> blocks, int budget, int start, bool growOlder, bool growNewer)
    {
        if (blocks[start].Length + 1 > budget)
        {
            blocks[start] = Sanitizer.Truncate(blocks[start], budget - 1);
        }

        int first = start, last = start, used = blocks[start].Length + 1;
        while (growOlder || growNewer)
        {
            if (growOlder)
            {
                growOlder = first > 0 && used + blocks[first - 1].Length + 1 <= budget;
                if (growOlder)
                {
                    used += blocks[--first].Length + 1;
                }
            }

            if (growNewer)
            {
                growNewer = last < blocks.Count - 1 && used + blocks[last + 1].Length + 1 <= budget;
                if (growNewer)
                {
                    used += blocks[++last].Length + 1;
                }
            }
        }

        return (first, last);
    }

    /// <summary>How many leading items fit the budget; at least one.</summary>
    private static int Prefix(List<int> lengths, int budget)
    {
        int count = 0, used = 0;
        while (count < lengths.Count && (count == 0 || used + lengths[count] <= budget))
        {
            used += lengths[count++];
        }

        return count;
    }

    private static bool IsSystem(int type) => type is MessageTypes.ChannelPinnedMessage or MessageTypes.UserJoin
        or (>= MessageTypes.GuildBoost and <= MessageTypes.GuildBoostTier3) or MessageTypes.ThreadCreated or MessageTypes.PollResult;

    private static string? Label(Texts t, DiscordMessage m) => m.Type switch
    {
        MessageTypes.Reply when m.ReferencedMessage is { } r => t.ReplyTo(Id(r.Id), Sanitizer.Name(r.Author.DisplayName, 80)),
        MessageTypes.Reply when m.MessageReference is not null => t.ReplyToDeleted,
        MessageTypes.ThreadStarterMessage => m.ReferencedMessage is null ? t.ThreadStarterDeleted : t.ThreadStarter,
        MessageTypes.ThreadCreated => t.ThreadCreated(Id(m.MessageReference?.ChannelId ?? 0), Sanitizer.Line(m.Content, 100)),
        MessageTypes.ChannelPinnedMessage => t.PinNotice(Id(m.MessageReference?.MessageId ?? 0)),
        MessageTypes.UserJoin => t.UserJoined,
        MessageTypes.GuildBoost => t.Boosted,
        >= MessageTypes.GuildBoostTier1 and <= MessageTypes.GuildBoostTier3 => t.BoostedTier(m.Type - MessageTypes.GuildBoost),
        MessageTypes.PollResult => t.PollResultLabel,
        MessageTypes.ChatInputCommand or MessageTypes.ContextMenuCommand => t.CommandReply,
        MessageTypes.AutoModerationAction => t.AutoModAction,
        _ when (m.Flags & MessageFlags.IsCrosspost) != 0 => t.Crosspost,
        MessageTypes.Default or MessageTypes.Reply => null,
        _ => t.SystemMessage(m.Type),
    };

    private List<string> Details(GuildSnapshot guild, DiscordMessage message, DiscordMessage source, bool detailed)
    {
        var t = Languages.Current;
        var lines = new List<string>();
        foreach (var snapshot in message.MessageSnapshots ?? [])
        {
            var s = snapshot.Message;
            var text = Sanitizer.Indented(Mentions(guild, s.Content, s.Mentions), detailed ? 4000 : 2000);
            lines.Add($"{t.Forwarded(DisplayTime.Format(s.Timestamp))}{(text.Length > 0 ? ": " + text : "")}");
            lines.AddRange(s.Embeds.SelectMany(e => Embed(e, s.Content, detailed)));
        }

        lines.AddRange(AttachmentLocator.All(message).Select(AttachmentLine));
        lines.AddRange((source.StickerItems ?? []).Select(s => t.Sticker(Sanitizer.Line(s.Name, 80))));
        lines.AddRange(source.Embeds.SelectMany(e => Embed(e, source.Content, detailed)));
        if (source.Poll is { } poll)
        {
            lines.Add(Poll(poll));
        }

        if (message.Thread is { } thread)
        {
            lines.Add(t.ThreadInline(Id(thread.Id), Sanitizer.Line(thread.Name, 100), thread.MessageCount));
        }

        if (source.Reactions is { Count: > 0 } reactions)
        {
            lines.Add(t.Reactions(string.Join(", ", reactions.Select(r => $"{Emoji(r.Emoji)} {r.Count}"))));
        }

        return lines;
    }

    private IEnumerable<string> Embed(DiscordEmbed e, string content, bool detailed)
    {
        var url = SafeUrl(e.Url);
        var parts = new[]
        {
            Sanitizer.Line(e.Title, 150),
            Sanitizer.Line(e.Description, detailed ? 2000 : 300),
            url is not null && !content.Contains(url, StringComparison.Ordinal) ? Sanitizer.Line(url, 300) : "",
            Sanitizer.Line(e.Provider?.Name, 80),
        }.Where(p => p.Length > 0).ToList();
        var fields = (e.Fields ?? []).Take(detailed ? 25 : 10).ToList();
        if (parts.Count == 0 && fields.Count == 0)
        {
            yield break;
        }

        yield return "🔗 " + (parts.Count > 0 ? string.Join(" · ", parts) : Languages.Current.EmbedFallback);
        foreach (var field in fields)
        {
            yield return $"  {Sanitizer.Line(field.Name, 100)}: {Sanitizer.Line(field.Value, detailed ? 1024 : 200)}";
        }
    }

    private static string Poll(DiscordPoll poll)
    {
        var t = Languages.Current;
        var counts = (poll.Results?.AnswerCounts ?? []).DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        var answers = poll.Answers.Select(a =>
        {
            var emoji = a.PollMedia.Emoji is { } em ? Emoji(em) + " " : "";
            var count = counts.TryGetValue(a.AnswerId, out var c) ? $": {c.Count}{(c.MeVoted ? " " + t.PollMyVote : "")}" : "";
            return $"{emoji}{Sanitizer.Line(a.PollMedia.Text, 100)}{count}";
        });
        var state = poll.Results is { IsFinalized: true } ? t.PollFinished
            : poll.Expiry is { } expiry ? t.PollEnds(DisplayTime.Format(expiry)) : t.PollNoEnd;
        return t.PollLine(Sanitizer.Line(poll.Question.Text, 200), string.Join(" | ", answers), state, poll.AllowMultiselect);
    }

    private static string Emoji(ReactionEmoji emoji) =>
        emoji.Id is null ? Sanitizer.Line(emoji.Name, 20) : $":{Sanitizer.Line(emoji.Name, 40)}:";

    private string Author(GuildSnapshot guild, DiscordUser user)
    {
        var sb = new StringBuilder();
        sb.Append(Sanitizer.Name(user.DisplayName, 80)).Append(" (@").Append(Sanitizer.Name(user.Username, 40)).Append(", ").Append(Id(user.Id)).Append(')');
        if (user.Bot)
        {
            sb.Append(' ').Append(Languages.Current.BotMarker);
        }

        if (user.Id == guild.Me.UserId)
        {
            sb.Append(' ').Append(Sanitizer.SelfMarker);
        }

        return sb.ToString();
    }

    private string Mentions(GuildSnapshot guild, string content, IReadOnlyList<DiscordUser> mentions) =>
        MentionPattern().Replace(content, match =>
        {
            if (!ulong.TryParse(match.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                return match.Value;
            }

            return match.Groups[1].Value switch
            {
                "@&" => guild.RoleNames.TryGetValue(id, out var role) ? "@" + Sanitizer.Line(role, 80).TrimStart('@') : match.Value,
                "#" => ChannelName(guild, id) is { } name ? "#" + name : match.Value,
                _ => mentions.FirstOrDefault(u => u.Id == id) is { } user ? "@" + Sanitizer.Line(user.DisplayName, 80) : match.Value,
            };
        });

    /// <summary>Embed links to the Discord CDN lose their query: it carries the signature.</summary>
    private static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && CdnHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            ? uri.GetLeftPart(UriPartial.Path)
            : url;
    }

    [GeneratedRegex(@"<(@!?|@&|#)(\d{1,20})>", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern();
}
