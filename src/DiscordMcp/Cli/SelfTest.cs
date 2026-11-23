using System.Globalization;
using System.Text.RegularExpressions;
using DiscordMcp.Discord;
using DiscordMcp.Localization;
using DiscordMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace DiscordMcp.Cli;

/// <summary>
/// <c>check &lt;guildId&gt; --selftest</c>: runs the real typed path (API → models → permissions → formatter → tools)
/// once per tool against the live guild, about eight paced requests. Prints only line/character counts and error kinds
/// with their fixed messages; a model mismatch is logged as a JSON path (property names and indexes only).
/// The ids it needs are scraped from the tools' own output with patterns that rely on structure (emoji, ids, file
/// names), never on the words of one language.
/// </summary>
internal sealed partial class SelfTest(IServiceProvider services, TextWriter output)
{
    private int _failures;
    private bool _stopped;

    private static Texts T => Languages.Current;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        output.WriteLine(T.SelfTestIntro);
        output.WriteLine();
        var server = ActivatorUtilities.CreateInstance<ServerTools>(services);
        var messages = ActivatorUtilities.CreateInstance<MessageTools>(services);

        await StepAsync("server_overview", () => server.ServerOverviewAsync(ct));
        if (_stopped)
        {
            return Finish();
        }

        var snapshot = await services.GetRequiredService<GuildContext>().GetAsync(ct);
        var text = snapshot.Channels
            .Where(c => c.Type is ChannelTypes.Text or ChannelTypes.Announcement && snapshot.AccessOf(c.Id).History)
            .OrderByDescending(c => c.LastMessageId ?? 0)
            .FirstOrDefault();
        if (text is null)
        {
            output.WriteLine("  [---]   " + T.SelfTestNoTextChannel);
            return Finish();
        }

        var id = text.Id.ToString(CultureInfo.InvariantCulture);
        var page = await StepAsync("read_messages", () => messages.ReadMessagesAsync(id, limit: 10, cancellationToken: ct));
        var messageId = page is null ? null : MessageId().Matches(page).Select(m => m.Groups[1].Value).LastOrDefault();
        if (messageId is not null)
        {
            await StepAsync("get_message", () => messages.GetMessageAsync(id, messageId, ct));
        }

        await StepAsync("list_pins", () => messages.ListPinsAsync(id, cancellationToken: ct));
        var threads = await StepAsync("list_threads", () => server.ListThreadsAsync(id, cancellationToken: ct));
        var search = await StepAsync("search_messages", () => messages.SearchMessagesAsync(channel_id: id, cancellationToken: ct));

        // Real attachments, found by guild-wide searches: any file, then an image (inline image path).
        var attachments = ActivatorUtilities.CreateInstance<AttachmentTools>(services);
        var files = await StepAsync("search_messages(has=file)", () => messages.SearchMessagesAsync(has: ["file"], cancellationToken: ct));
        await AttachmentStepAsync($"get_attachment({T.StepFile})", files, attachments, ct);
        var images = await StepAsync("search_messages(has=image)", () => messages.SearchMessagesAsync(has: ["image"], cancellationToken: ct));
        await AttachmentStepAsync($"get_attachment({T.StepImage})", images, attachments, ct);

        // A thread: classify it with GET /channels/{id} (the path used for thread ids from links), then read it.
        // Candidates come from output already fetched: thread lists, search groups, thread markers in messages.
        var threadId = FirstThreadId(threads, search, files, images, page);
        if (threadId is null)
        {
            output.WriteLine("  [---]   " + T.SelfTestNoThread);
        }
        else
        {
            await StepAsync("GET /channels/{" + T.StepThread + "}", async () =>
            {
                var channel = await services.GetRequiredService<IDiscordApi>().GetChannelAsync(ulong.Parse(threadId, CultureInfo.InvariantCulture), ct);
                return $"type={channel.Type} {T.GuildIdMatches}={channel.GuildId == snapshot.Guild.Id} parent_id={channel.ParentId is not null} metadata={channel.ThreadMetadata is not null}";
            }, printResult: true);
            await StepAsync($"read_messages({T.StepThread})", () => messages.ReadMessagesAsync(threadId, limit: 5, cancellationToken: ct));
        }

        // A forum post, when the server has a readable forum.
        var forum = snapshot.Channels.FirstOrDefault(c => ChannelTypes.ThreadOnly.Contains(c.Type) && snapshot.AccessOf(c.Id).History);
        if (forum is null)
        {
            output.WriteLine("  [---]   " + T.SelfTestNoForum);
        }
        else
        {
            var forumId = forum.Id.ToString(CultureInfo.InvariantCulture);
            var posts = await StepAsync($"list_threads({T.StepForum})", () => server.ListThreadsAsync(forumId, cancellationToken: ct));
            if (posts is not null && ThreadId().Match(posts) is { Success: true } post)
            {
                await StepAsync($"read_messages({T.StepForumPost})", () => messages.ReadMessagesAsync(post.Groups[1].Value, limit: 5, cancellationToken: ct));
            }
        }

        return Finish();
    }

    private async Task AttachmentStepAsync(string name, string? search, AttachmentTools attachments, CancellationToken ct)
    {
        if (search is null)
        {
            return;
        }

        if (FirstAttachment(search) is not var (channelId, messageId, attachmentId))
        {
            output.WriteLine("  [---]   " + T.SelfTestNoAttachment(name));
            return;
        }

        await StepAsync(name, async () =>
        {
            var result = await attachments.GetAttachmentAsync(channelId, messageId, attachmentId, cancellationToken: ct);
            if (result.IsError == true)
            {
                throw new DiscordApiException(DiscordErrorKind.Upstream, T.SelfTestAttachmentError);
            }

            return Summarize(result);
        }, printResult: true);
    }

    /// <summary>What a get_attachment result was, in counts only: an image, a saved file, a PDF or plain text.</summary>
    internal static string Summarize(CallToolResult result) => string.Join(", ", result.Content.Select(block => block switch
    {
        ImageContentBlock image => T.SelfTestImage(image.MimeType, image.Data.Length * 3 / 4 / 1024, image.Data.Length / 1024),

        // A saved file names its path ({message}_{attachment}.{ext}) before the fence; the text of a read file never comes first.
        TextContentBlock text when SavedPath().Match(BeforeFence(text.Text)) is { Success: true } saved =>
            saved.Groups[1].Value == "pdf" ? T.SelfTestSavedPdf : T.SelfTestSaved,
        TextContentBlock text when PdfSummary().Match(text.Text) is { Success: true } pdf =>
            T.SelfTestPdf(int.Parse(pdf.Groups[1].Value, CultureInfo.InvariantCulture), text.Text.Length),
        TextContentBlock text => T.SelfTestText(text.Text.Length),
        _ => block.GetType().Name,
    }));

    private static string BeforeFence(string text)
    {
        var fence = text.IndexOf("<<<attachment-", StringComparison.Ordinal);
        return fence < 0 ? text : text[..fence];
    }

    /// <summary>The first thread id found in tool output: a list_threads line, a search group or a thread marker in a message.</summary>
    internal static string? FirstThreadId(params string?[] outputs) => outputs
        .Where(s => s is not null)
        .SelectMany(s => ThreadCandidate().Matches(s!).Select(m => m.Groups.Values.Skip(1).First(g => g.Success).Value))
        .FirstOrDefault();

    /// <summary>The first (channel, message, attachment) triple in search output.</summary>
    internal static (string Channel, string Message, string Attachment)? FirstAttachment(string search)
    {
        string? channel = null, message = null;
        foreach (var line in search.Split('\n'))
        {
            if (line.StartsWith("── ", StringComparison.Ordinal) && GroupId().Match(line) is { Success: true } g)
            {
                channel = g.Groups[1].Value;
            }
            else if (MessageId().Match(line) is { Success: true } m)
            {
                message = m.Groups[1].Value;
            }
            else if (AttachmentId().Match(line) is { Success: true } a && channel is not null && message is not null)
            {
                return (channel, message, a.Groups[1].Value);
            }
        }

        return null;
    }

    private async Task<string?> StepAsync(string tool, Func<Task<string>> call, bool printResult = false)
    {
        if (_stopped)
        {
            return null;
        }

        try
        {
            var result = await call();
            var lines = result.Count(c => c == '\n') + 1;

            // printResult only for steps whose result is built here from counts and types, never from Discord text.
            output.WriteLine(printResult ? $"  [{T.SelfTestOkTag}]    {tool}: {result}" : $"  [{T.SelfTestOkTag}]    {tool}: {T.SelfTestCounts(lines, result.Length)}");
            return result;
        }
        catch (DiscordApiException e)
        {
            _failures++;
            output.WriteLine($"  [{T.SelfTestErrorTag}] {tool}: {e.Kind} — {e.Message}");
            _stopped = e.Kind is DiscordErrorKind.HardStop or DiscordErrorKind.Unauthorized or DiscordErrorKind.RateLimited
                or DiscordErrorKind.BreakerOpen or DiscordErrorKind.BudgetExceeded or DiscordErrorKind.NoToken;
            return null;
        }
    }

    private int Finish()
    {
        output.WriteLine();
        output.WriteLine(_failures == 0 ? T.SelfTestAllOk : T.SelfTestFailures(_failures));
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Message header: <c>[time] &lt;message id&gt; …</c>.</summary>
    [GeneratedRegex(@"^\[[^\]]+\] (\d{17,20}) ", RegexOptions.Multiline)]
    private static partial Regex MessageId();

    /// <summary>list_threads line: <c>🧵 &lt;thread id&gt; “name” (…)</c>.</summary>
    [GeneratedRegex(@"^\s*🧵 (\d{17,20}) ", RegexOptions.Multiline)]
    private static partial Regex ThreadId();

    /// <summary>
    /// A thread id in tool output: a list_threads line (<c>🧵 id …</c>, at the start of a line), a search group
    /// (<c>── 🧵 “name” (id) …</c>) or a message marker anywhere in a line (<c>🧵 thread id …</c>, and the type-18
    /// notice <c>🧵 started thread id …</c> after the author). The words in front of the id are whatever the active
    /// language puts there: letters, parentheses and spaces.
    /// </summary>
    [GeneratedRegex(@"^\s*🧵 (\d{17,20})|🧵 (?:[\p{L}()]+ )+(\d{17,20})|── 🧵 [^\n]*? \((\d{17,20})\)", RegexOptions.Multiline)]
    private static partial Regex ThreadCandidate();

    /// <summary>Search group line: <c>── #name (id)</c> or <c>── 🧵 „name“ (id) v #parent</c>.</summary>
    [GeneratedRegex(@"\((\d{17,20})\)")]
    private static partial Regex GroupId();

    /// <summary>Attachment line: <c>📎 &lt;attachment id&gt; filename (…)</c>.</summary>
    [GeneratedRegex(@"📎 (\d{17,20}) ")]
    private static partial Regex AttachmentId();

    /// <summary>Page count from the header of a PDF result (our own text, before the fence): <c>, PDF (3 …</c>.</summary>
    [GeneratedRegex(@", PDF \((\d+)")]
    private static partial Regex PdfSummary();

    /// <summary>The file a saved result names: <c>…\&lt;message id&gt;_&lt;attachment id&gt;.&lt;ext&gt;</c>.</summary>
    [GeneratedRegex(@"\d{17,20}_\d{17,20}\.([a-z0-9]+)")]
    private static partial Regex SavedPath();
}
