using System.ComponentModel;
using DiscordMcp.Discord;
using DiscordMcp.Formatting;
using DiscordMcp.Localization;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace DiscordMcp.Tools;

/// <summary><c>read_messages</c>, <c>get_message</c>, <c>search_messages</c> and <c>list_pins</c>.</summary>
[McpServerToolType]
public sealed class MessageTools(
    ToolRunner runner,
    GuildContext context,
    ChannelResolver resolver,
    MessageFormatter formatter,
    IDiscordApi api,
    IOptions<DiscordOptions> options)
{
    private const int SearchPageSize = 25;

    [McpServerTool(Name = "read_messages", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Reads messages of a channel or thread, listed oldest first. Without a cursor it returns the newest messages. "
        + "Page with the cursors printed on the last line (before=… for older, after=… for newer). Times are in the time zone shown in the results. "
        + ToolInput.UntrustedNotice)]
    public Task<string> ReadMessagesAsync(
        [Description("Channel or thread id (from server_overview, list_threads or a search result).")] string channel_id,
        [Description("Only messages older than this: " + ToolInput.CursorFormats + ".")] string? before = null,
        [Description("Only messages newer than this: " + ToolInput.CursorFormats + ".")] string? after = null,
        [Description("Messages around this point: " + ToolInput.CursorFormats + ".")] string? around = null,
        [Description("How many messages to fetch, 1-100.")] int? limit = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("read_messages", async ct =>
        {
            var id = ToolInput.Id(channel_id, "channel_id");
            var olderThan = ToolInput.Cursor(before, "before", upperBound: true);
            var newerThan = ToolInput.Cursor(after, "after");
            var center = ToolInput.Cursor(around, "around");
            if ((olderThan is null ? 0 : 1) + (newerThan is null ? 0 : 1) + (center is null ? 0 : 1) > 1)
            {
                throw ToolInput.BadRequest(Languages.Current.CursorsExclusive);
            }

            var pageSize = Math.Clamp(limit ?? 50, 1, 100);
            await resolver.ResolveReadableAsync(id, ct).ConfigureAwait(false);
            var guild = await context.GetAsync(ct).ConfigureAwait(false);
            var messages = await api.GetMessagesAsync(id, new MessageQuery(olderThan, newerThan, center, pageSize), ct).ConfigureAwait(false);
            context.RememberMessages(messages);
            var anchor = olderThan is not null ? PageAnchor.Before
                : newerThan is not null ? PageAnchor.After
                : center is not null ? PageAnchor.Around
                : PageAnchor.Newest;
            return formatter.RenderMessages(guild, Languages.Current.MessagesTitle(formatter.Where(guild, id)), messages, anchor, pageSize, center);
        }, cancellationToken);

    [McpServerTool(Name = "get_message", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("One message with all details: attachments (with ids for get_attachment), embeds, reactions, reply, forward, poll, thread. "
        + "For a forum post pass the forum channel id and the post (thread) id as message_id. " + ToolInput.UntrustedNotice)]
    public Task<string> GetMessageAsync(
        [Description("Channel or thread id the message is in.")] string channel_id,
        [Description("Message id.")] string message_id,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_message", async ct =>
        {
            var channelId = ToolInput.Id(channel_id, "channel_id");
            var messageId = ToolInput.Id(message_id, "message_id");
            var channel = await resolver.ResolveForMessageAsync(channelId, messageId, ct).ConfigureAwait(false);
            var guild = await context.GetAsync(ct).ConfigureAwait(false);
            var message = await FindMessageAsync(api, context, channel.Id, messageId, ct).ConfigureAwait(false);
            var text = $"{MessageFormatter.UntrustedHeader}\n{Languages.Current.MessageDetailTitle(formatter.Where(guild, channel.Id))}\n{MessageFormatter.Legend}\n"
                + formatter.FormatMessage(guild, message, detailed: true);
            return Sanitizer.Truncate(text, options.Value.MaxOutputChars);
        }, cancellationToken);

    [McpServerTool(Name = "search_messages", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Searches messages in the whole server with Discord search. Every filter is optional; without query it is a "
        + "server-wide activity feed (narrow it with after/before). 25 results per page, page with offset. " + ToolInput.UntrustedNotice)]
    public Task<string> SearchMessagesAsync(
        [Description("Text to search for.")] string? query = null,
        [Description("Only messages by this user id.")] string? author_id = null,
        [Description("Only messages in this channel or thread id.")] string? channel_id = null,
        [Description("Only messages that contain: image, sound, video, file, sticker, embed, link, poll, snapshot; prefix '-' to exclude.")] string[]? has = null,
        [Description("Only messages after this point: " + ToolInput.CursorFormats + ".")] string? after = null,
        [Description("Only messages before this point: " + ToolInput.CursorFormats + ".")] string? before = null,
        [Description("newest, oldest or relevance.")] string sort = "newest",
        [Description("Paging offset (0-9975), from the offset=N cursor printed at the end of the previous page.")] int? offset = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("search_messages", async ct =>
        {
            var t = Languages.Current;
            var (sortBy, sortOrder) = sort?.Trim().ToLowerInvariant() switch
            {
                null or "" or "newest" => ("timestamp", "desc"),
                "oldest" => ("timestamp", "asc"),
                "relevance" => ("relevance", "desc"),
                _ => throw ToolInput.BadRequest(t.SortInvalid),
            };
            if (offset is < 0 or > 9975)
            {
                throw ToolInput.BadRequest(t.OffsetRange);
            }

            var hasValues = (has ?? []).Select(h => h?.Trim().ToLowerInvariant() ?? "").Where(h => h.Length > 0).Distinct().ToList();
            if (hasValues.Any(h => !SearchQuery.HasValues.Contains(h.TrimStart('-'))))
            {
                throw ToolInput.BadRequest(t.HasInvalid(string.Join(", ", SearchQuery.HasValues)));
            }

            var content = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
            if (content is { Length: > 1024 })
            {
                throw ToolInput.BadRequest(t.QueryTooLong);
            }

            var request = new SearchQuery
            {
                Content = content,
                AuthorId = ToolInput.OptionalId(author_id, "author_id"),
                ChannelId = ToolInput.OptionalId(channel_id, "channel_id"),
                Has = hasValues,
                MinId = ToolInput.Cursor(after, "after"),
                MaxId = ToolInput.Cursor(before, "before", upperBound: true),
                SortBy = sortBy,
                SortOrder = sortOrder,
                Offset = offset ?? 0,
                Limit = SearchPageSize,
            };

            var guild = await context.GetAsync(ct).ConfigureAwait(false);
            if (request.ChannelId is { } channelId)
            {
                var channel = await resolver.ResolveAsync(channelId, ct).ConfigureAwait(false);
                if (!ChannelTypes.Readable.Contains(channel.Type) && !ChannelTypes.ThreadOnly.Contains(channel.Type))
                {
                    throw new DiscordApiException(DiscordErrorKind.NotAllowed, t.CannotSearchChannel(channelId));
                }

                if (!channel.Access.History)
                {
                    throw ChannelResolver.NoHistory(channelId);
                }
            }

            var response = await api.SearchMessagesAsync(request, ct).ConfigureAwait(false);
            context.RememberThreads(response.Threads, response.Members);
            context.RememberMessages(response.Messages.SelectMany(group => group));
            return formatter.RenderSearch(guild, response, offset ?? 0, SearchPageSize, chronological: sortBy == "timestamp");
        }, cancellationToken);

    [McpServerTool(Name = "list_pins", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Pinned messages of a channel or thread, up to 50 per page; page with the before= cursor on the last line. "
        + ToolInput.UntrustedNotice)]
    public Task<string> ListPinsAsync(
        [Description("Channel or thread id.")] string channel_id,
        [Description("Only pins pinned before this time: the cursor from the previous page or " + ToolInput.CursorFormats + ".")] string? before = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("list_pins", async ct =>
        {
            var id = ToolInput.Id(channel_id, "channel_id");
            DateTimeOffset? pinnedBefore = ToolInput.Cursor(before, "before", upperBound: true) is { } cursor ? Snowflake.ToTimestamp(cursor) : null;
            await resolver.ResolveReadableAsync(id, ct).ConfigureAwait(false);
            var guild = await context.GetAsync(ct).ConfigureAwait(false);
            var pins = await api.GetPinsAsync(id, pinnedBefore, 50, ct).ConfigureAwait(false);
            context.RememberMessages(pins.Items.Select(p => p.Message));
            return formatter.RenderPins(guild, Languages.Current.PinsTitle(formatter.Where(guild, id)), pins);
        }, cancellationToken);

    /// <summary><c>around</c> with limit 5 (single-message reads are not available to user tokens), then pick by id.</summary>
    internal static async Task<DiscordMessage> FindMessageAsync(IDiscordApi api, GuildContext context, ulong channelId, ulong messageId, CancellationToken cancellationToken)
    {
        var page = await api.GetMessagesAsync(channelId, new MessageQuery(Around: messageId, Limit: 5), cancellationToken).ConfigureAwait(false);
        context.RememberMessages(page);
        return page.FirstOrDefault(m => m.Id == messageId)
            ?? throw new DiscordApiException(DiscordErrorKind.NotFound, Languages.Current.MessageNotFound(messageId, channelId));
    }
}
