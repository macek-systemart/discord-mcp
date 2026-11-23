using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DiscordMcp.Discord;
using DiscordMcp.Formatting;
using DiscordMcp.Localization;
using DiscordMcp.Office;
using DiscordMcp.Pdf;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DiscordMcp.Tools;

/// <summary>
/// <c>get_attachment</c>: images inline (format from magic bytes), UTF-8 text inside a nonce fence in chunks, PDFs as
/// their text in the same fence plus the picture of a scanned page (parsed by <see cref="IPdfReader"/> in a child
/// process), Word/Excel/PowerPoint/OpenDocument files as their text and other ZIP archives as their file list (parsed
/// by <see cref="IOfficeReader"/> in a child process), everything else saved under <see cref="AppPaths.DownloadsDirectory"/>
/// with a whitelisted extension and the mark of the web. A document saved earlier is reused instead of downloading it again.
/// </summary>
[McpServerToolType]
public sealed class AttachmentTools(
    ToolRunner runner,
    GuildContext context,
    ChannelResolver resolver,
    IDiscordApi api,
    AppPaths paths,
    IOptions<DiscordOptions> options,
    TimeProvider time,
    IPdfReader pdfReader,
    IOfficeReader officeReader)
{
    /// <summary>Shown inline as text when the bytes are valid UTF-8.</summary>
    private static readonly HashSet<string> TextExtensions =
    [
        "txt", "md", "markdown", "csv", "tsv", "json", "jsonl", "log", "xml", "yaml", "yml", "toml", "ini", "cfg", "conf",
        "cs", "csproj", "js", "ts", "tsx", "jsx", "py", "rb", "go", "rs", "java", "kt", "c", "h", "cpp", "hpp", "sql",
        "html", "htm", "css", "scss", "sh", "ps1", "bat", "cmd", "srt", "vtt", "diff", "patch",
    ];

    /// <summary>Extensions kept when saving to disk; anything else (scripts and executables included) becomes <c>.bin</c>.</summary>
    private static readonly HashSet<string> SafeDiskExtensions =
    [
        "pdf", "png", "jpg", "jpeg", "gif", "webp", "heic", "heif", "avif", "bmp", "tif", "tiff",
        "mp3", "m4a", "wav", "ogg", "opus", "flac", "mp4", "mov", "webm", "mkv",
        "zip", "7z", "docx", "xlsx", "pptx", "odt", "ods", "odp",
        "txt", "md", "csv", "json", "log", "xml", "yaml", "yml",
    ];

    [McpServerTool(Name = "get_attachment", ReadOnly = true, Destructive = false, OpenWorld = true)]
    [Description("Fetches one attachment of a message (ids from read_messages or get_message). PNG, JPEG, GIF and WEBP images "
        + "come back as images (large ones downscaled), UTF-8 text files as text in chunks (continue with offset), PDFs as their "
        + "text plus the picture of a scanned page (other pages with page=N), Word, Excel, PowerPoint and OpenDocument files as "
        + "their text, other ZIP archives as their file list; anything else is saved to a local downloads folder for the user "
        + "and only its path is returned. " + ToolInput.UntrustedNotice)]
    public Task<CallToolResult> GetAttachmentAsync(
        [Description("Channel or thread id the message is in (for a forum post: the forum id).")] string channel_id,
        [Description("Message id.")] string message_id,
        [Description("Attachment id (the number after 📎).")] string attachment_id,
        [Description("Character offset for long text attachments, from the offset=N cursor printed at the end of the previous part.")] int? offset = null,
        [Description("PDF only: 1-based page to read alone; for a scanned page this also returns its picture.")] int? page = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_attachment", async ct =>
        {
            var channelId = ToolInput.Id(channel_id, "channel_id");
            var messageId = ToolInput.Id(message_id, "message_id");
            var attachmentId = ToolInput.Id(attachment_id, "attachment_id");
            if (offset < 0)
            {
                throw ToolInput.BadRequest(Languages.Current.OffsetNegative);
            }

            if (page < 1)
            {
                throw ToolInput.BadRequest(Languages.Current.PageFromOne);
            }

            var o = options.Value;
            var channel = await resolver.ResolveForMessageAsync(channelId, messageId, ct).ConfigureAwait(false);
            var message = await MessageTools.FindMessageAsync(api, context, channel.Id, messageId, ct).ConfigureAwait(false);
            var attachment = AttachmentLocator.Find(message, attachmentId)
                ?? throw new DiscordApiException(DiscordErrorKind.NotFound, Languages.Current.MessageHasNoAttachment(messageId, attachmentId));
            if (attachment.Size > o.MaxAttachmentBytes)
            {
                throw TooLarge(o.MaxAttachmentBytes);
            }

            if (ReadSaved(messageId, attachment) is { } saved)
            {
                return await PresentAsync(messageId, attachment, saved, offset ?? 0, page, ct).ConfigureAwait(false);
            }

            if (!Uri.TryCreate(attachment.Url, UriKind.Absolute, out var url))
            {
                throw new DiscordApiException(DiscordErrorKind.NotFound, Languages.Current.AttachmentGone);
            }

            if (ExpiresAt(url) is { } expires && expires <= time.GetUtcNow().AddMinutes(5))
            {
                // The cached copy carries an expiring signed URL: fetch the message again for a fresh one.
                var fresh = await api.GetMessagesAsync(channel.Id, new MessageQuery(Around: messageId, Limit: 5, BypassCache: true), ct).ConfigureAwait(false);
                var refreshed = fresh.FirstOrDefault(m => m.Id == messageId) is { } m ? AttachmentLocator.Find(m, attachmentId) : null;
                if (refreshed is null || !Uri.TryCreate(refreshed.Url, UriKind.Absolute, out url))
                {
                    throw new DiscordApiException(DiscordErrorKind.NotFound, Languages.Current.AttachmentGone);
                }

                attachment = refreshed;
            }

            var file = await api.DownloadAttachmentAsync(url, o.MaxAttachmentBytes, ct).ConfigureAwait(false);
            if (page is null && SniffImage(file.Bytes) is { } originalMime && file.Bytes.Length > o.MaxInlineImageBytes
                && await TryPreviewAsync(attachment, ct).ConfigureAwait(false) is { } preview)
            {
                return Preview(messageId, attachment, file.Bytes.Length, originalMime, preview.Bytes, preview.Mime);
            }

            return await PresentAsync(messageId, attachment, file.Bytes, offset ?? 0, page, ct).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<CallToolResult> PresentAsync(ulong messageId, DiscordAttachment attachment, byte[] bytes, int offset, int? page, CancellationToken cancellationToken)
    {
        if (IsPdf(bytes, Extension(Sanitizer.Line(attachment.Filename, int.MaxValue))))
        {
            return await PdfAsync(messageId, attachment, bytes, offset, page, cancellationToken).ConfigureAwait(false);
        }

        if (page is not null)
        {
            throw ToolInput.BadRequest(Languages.Current.PageOnlyForPdf);
        }

        return IsZip(bytes)
            ? await OfficeAsync(messageId, attachment, bytes, offset, cancellationToken).ConfigureAwait(false)
            : Present(messageId, attachment, bytes, offset);
    }

    /// <summary>Saved copies that are reused for paging: PDFs and the ZIP-based documents.</summary>
    private static readonly HashSet<string> DocumentExtensions = ["docx", "xlsx", "pptx", "odt", "ods", "odp", "zip"];

    internal static bool IsZip(ReadOnlySpan<byte> bytes) => bytes.StartsWith((ReadOnlySpan<byte>)[0x50, 0x4B, 0x03, 0x04]);

    private async Task<CallToolResult> OfficeAsync(ulong messageId, DiscordAttachment attachment, byte[] bytes, int offset, CancellationToken cancellationToken)
    {
        var t = Languages.Current;
        var name = Sanitizer.Line(attachment.Filename, int.MaxValue);
        var extension = Extension(name);
        var about = About(messageId, attachment, name, bytes.Length);
        var (path, _) = Save(bytes, $"{MessageFormatter.Id(messageId)}_{MessageFormatter.Id(attachment.Id)}.{DiskExtension(extension)}");
        var office = Checked(await officeReader.ReadAsync(path, cancellationToken).ConfigureAwait(false));
        if (office is null)
        {
            return Saved(t.OfficeUnreadable(about), bytes, messageId, attachment.Id, extension);
        }

        var text = Sanitizer.Text(office.Text);
        var sb = new StringBuilder(FencedText($"{about}, {Label(t, office.Kind)}", text, offset));
        if (office.Truncated)
        {
            sb.Append('\n').Append(t.OfficeTruncated);
        }

        if (text.Length == 0)
        {
            sb.Append('\n').Append(t.NoTextInFile);
        }

        sb.Append(office.Kind switch
        {
            OfficeKind.Excel => "\n" + t.ExcelNote,
            OfficeKind.OpenDocumentSpreadsheet => "\n" + t.OpenDocumentSpreadsheetNote,
            OfficeKind.Zip => "\n" + t.ZipNote,
            _ => "",
        });
        return Result(new TextContentBlock { Text = sb.ToString() });
    }

    /// <summary>The child's JSON is as untrusted as the file: an unknown kind or missing text means "not read".</summary>
    private static OfficeExtract? Checked(OfficeExtract? office) =>
        office is { Failed: false, Text: not null } && Enum.IsDefined(office.Kind) ? office : null;

    private static string Label(Texts t, OfficeKind kind) => kind switch
    {
        OfficeKind.Word => t.KindWord,
        OfficeKind.Excel => t.KindExcel,
        OfficeKind.PowerPoint => t.KindPowerPoint,
        OfficeKind.OpenDocumentText => t.KindOpenDocumentText,
        OfficeKind.OpenDocumentSpreadsheet => t.KindOpenDocumentSpreadsheet,
        OfficeKind.OpenDocumentPresentation => t.KindOpenDocumentPresentation,
        _ => t.KindZip,
    };

    /// <summary><c>Attachment id “name” (size) from message id</c>, the start of most results.</summary>
    private static string About(ulong messageId, DiscordAttachment attachment, string name, long size) =>
        Languages.Current.AttachmentAbout(
            MessageFormatter.Id(attachment.Id), Sanitizer.Truncate(name, 100), MessageFormatter.Size(size), MessageFormatter.Id(messageId));

    /// <summary>
    /// The PDF or document saved by an earlier call (same message, attachment and size): paging through it needs no second
    /// download. Other saved files are not reused, so a failed image preview is retried next time.
    /// </summary>
    private byte[]? ReadSaved(ulong messageId, DiscordAttachment attachment)
    {
        var extension = Extension(Sanitizer.Line(attachment.Filename, int.MaxValue));
        string[] candidates = extension is not null && DocumentExtensions.Contains(extension) ? ["pdf", extension] : ["pdf"];
        foreach (var candidate in candidates)
        {
            var path = Path.Combine(paths.DownloadsDirectory, $"{MessageFormatter.Id(messageId)}_{MessageFormatter.Id(attachment.Id)}.{candidate}");
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length == attachment.Size)
                {
                    return File.ReadAllBytes(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Unreadable copy: download again.
            }
        }

        return null;
    }

    /// <summary>
    /// A PDF by its header, whatever the name says. Readers accept the header anywhere in the first 1024 bytes; that
    /// counts only for a <c>.pdf</c> name, so a text file that merely mentions <c>%PDF-</c> stays text.
    /// </summary>
    internal static bool IsPdf(ReadOnlySpan<byte> bytes, string? extension)
    {
        var header = bytes[..Math.Min(bytes.Length, 1024)].IndexOf("%PDF-"u8);
        return header == 0 || (header > 0 && extension == "pdf");
    }

    /// <summary>JPEG budget for a PDF page picture: leaves room for a full text chunk in the same result.</summary>
    private int PdfImageBudget => Math.Max(options.Value.MaxInlineImageBytes / 2, options.Value.MaxInlineImageBytes - 150_000);

    private async Task<CallToolResult> PdfAsync(ulong messageId, DiscordAttachment attachment, byte[] bytes, int offset, int? page, CancellationToken cancellationToken)
    {
        var t = Languages.Current;
        var name = Sanitizer.Line(attachment.Filename, int.MaxValue);
        var about = About(messageId, attachment, name, bytes.Length);
        var (path, _) = Save(bytes, $"{MessageFormatter.Id(messageId)}_{MessageFormatter.Id(attachment.Id)}.pdf");
        var request = new PdfRequest(page, offset == 0 ? PdfImageBudget : 0);
        var pdf = Checked(await pdfReader.ReadAsync(path, request, cancellationToken).ConfigureAwait(false));
        switch (pdf?.Failure)
        {
            case null or PdfFailure.Unreadable:
                return Saved(t.PdfUnreadable(about), bytes, messageId, attachment.Id, "pdf");
            case PdfFailure.Encrypted:
                return Saved(t.PdfEncrypted(about), bytes, messageId, attachment.Id, "pdf");
            case PdfFailure.PageOutOfRange:
                throw ToolInput.BadRequest(t.PdfPageOutOfRange(pdf.PageCount));
        }

        var text = new StringBuilder();
        foreach (var p in pdf.Pages)
        {
            text.Append("--- ").Append(t.PdfPageLabel(p.Number, pdf.PageCount)).Append(" ---\n")
                .Append(p.Text.Length > 0 ? p.Text : t.NoText).Append('\n');
        }

        var sb = new StringBuilder(FencedText(t.PdfDescription(about, pdf.PageCount, page), Sanitizer.Text(text.ToString().TrimEnd()), offset));
        if (pdf.Truncated)
        {
            sb.Append('\n').Append(t.PdfTruncated(PdfExtractor.MaxPages, PdfExtractor.MaxTextChars));
        }

        if (pdf.Pages.Where(p => p.Garbled).Select(p => p.Number).ToList() is { Count: > 0 } garbled)
        {
            sb.Append('\n').Append(t.PdfGarbled(string.Join(", ", garbled)));
        }

        if (pdf.Image is { } image)
        {
            sb.Append('\n').Append(t.PdfImageBelow(image.Page, image.Width, image.Height, MessageFormatter.Size(image.Jpeg.Length)));
        }
        else if (pdf.ImageProblem != PdfImageProblem.None)
        {
            sb.Append('\n').Append(t.PdfImageUnavailable(pdf.ImageProblemPage, Reason(t, pdf.ImageProblem)));
        }

        var pictures = pdf.PicturePages.Where(p => p != pdf.Image?.Page && p != pdf.ImageProblemPage).ToList();
        if (page is null && pictures.Count > 0)
        {
            sb.Append('\n').Append(t.PdfPicturePages(string.Join(", ", pictures.Take(50)) + (pictures.Count > 50 ? " …" : "")));
        }

        if (page is null && pdf.PicturePages.Count == 0 && pdf.Pages.All(p => p.Text.Length == 0))
        {
            sb.Append('\n').Append(t.PdfNoContent);
        }

        var block = new TextContentBlock { Text = sb.ToString() };
        return pdf.Image is { } picture ? Result(block, ImageContentBlock.FromBytes(picture.Jpeg, "image/jpeg")) : Result(block);
    }

    /// <summary>The child's JSON is as untrusted as the PDF: drop anything out of range; texts are sanitized by the caller.</summary>
    private PdfExtract? Checked(PdfExtract? pdf)
    {
        if (pdf is null || pdf.PageCount < 0 || pdf.Pages is null || pdf.PicturePages is null
            || pdf.Pages.Any(p => p is null || p.Text is null || p.Number < 1 || p.Number > pdf.PageCount))
        {
            return null;
        }

        var problemPage = pdf.ImageProblemPage >= 1 && pdf.ImageProblemPage <= pdf.PageCount ? pdf.ImageProblemPage : 0;
        var image = pdf.Image is { } i && i.Page >= 1 && i.Page <= pdf.PageCount && i.Jpeg is not null
            && i.Jpeg.Length <= options.Value.MaxInlineImageBytes && SniffImage(i.Jpeg) == "image/jpeg"
            ? i
            : null;
        return pdf with
        {
            Image = image,
            PicturePages = [.. pdf.PicturePages.Where(p => p >= 1 && p <= pdf.PageCount).Distinct()],
            ImageProblemPage = problemPage,
            ImageProblem = problemPage > 0 && Enum.IsDefined(pdf.ImageProblem) ? pdf.ImageProblem : PdfImageProblem.None,
        };
    }

    private static string Reason(Texts t, PdfImageProblem problem) => problem switch
    {
        PdfImageProblem.TooLarge => t.ImageProblemTooLarge,
        PdfImageProblem.Unsupported => t.ImageProblemUnsupported,
        _ => t.ImageProblemCorrupt,
    };

    /// <summary>Longest side of a preview; larger images are downscaled by Discord's media proxy.</summary>
    internal const int PreviewMaxSide = 1568;

    /// <summary>
    /// Discord's media proxy (<c>media.discordapp.net</c>, the attachment's signed <c>proxy_url</c>) serves a resized
    /// WEBP copy when asked with <c>width</c>, <c>height</c> and <c>format</c>. Null when the attachment has no proxy URL
    /// or no dimensions.
    /// </summary>
    internal static Uri? PreviewUrl(DiscordAttachment attachment)
    {
        if (!Uri.TryCreate(attachment.ProxyUrl, UriKind.Absolute, out var proxy)
            || !string.Equals(proxy.Host, "media.discordapp.net", StringComparison.OrdinalIgnoreCase)
            || attachment.Width is not > 0 || attachment.Height is not > 0)
        {
            return null;
        }

        var (width, height) = (attachment.Width.Value, attachment.Height.Value);
        var scale = Math.Min(1.0, (double)PreviewMaxSide / Math.Max(width, height));
        var kept = proxy.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("width=", StringComparison.Ordinal) && !p.StartsWith("height=", StringComparison.Ordinal)
                && !p.StartsWith("format=", StringComparison.Ordinal) && !p.StartsWith("quality=", StringComparison.Ordinal));
        string[] resize =
        [
            "format=webp",
            FormattableString.Invariant($"width={Math.Max(1, (int)Math.Round(width * scale))}"),
            FormattableString.Invariant($"height={Math.Max(1, (int)Math.Round(height * scale))}"),
        ];
        return new UriBuilder(proxy) { Query = string.Join('&', kept.Concat(resize)) }.Uri;
    }

    /// <summary>One CDN request without the token; any failure or a still too large result means "no preview".</summary>
    private async Task<(byte[] Bytes, string Mime)?> TryPreviewAsync(DiscordAttachment attachment, CancellationToken cancellationToken)
    {
        if (PreviewUrl(attachment) is not { } url)
        {
            return null;
        }

        try
        {
            var file = await api.DownloadAttachmentAsync(url, options.Value.MaxInlineImageBytes, cancellationToken).ConfigureAwait(false);
            return SniffImage(file.Bytes) is { } mime ? (file.Bytes, mime) : null;
        }
        catch (DiscordApiException)
        {
            return null;
        }
    }

    private static CallToolResult Preview(ulong messageId, DiscordAttachment attachment, int originalBytes, string originalMime, byte[] preview, string mime)
    {
        var name = Sanitizer.Truncate(Sanitizer.Line(attachment.Filename, int.MaxValue), 100);
        var text = MessageFormatter.UntrustedHeader + "\n" + Languages.Current.ImagePreview(
            MessageFormatter.Id(attachment.Id),
            name,
            MessageFormatter.Id(messageId),
            originalMime,
            $"{attachment.Width}×{attachment.Height}",
            MessageFormatter.Size(originalBytes),
            MessageFormatter.Size(preview.Length),
            mime);
        return Result(new TextContentBlock { Text = text }, ImageContentBlock.FromBytes(preview, mime));
    }

    internal CallToolResult Present(ulong messageId, DiscordAttachment attachment, byte[] bytes, int offset)
    {
        var t = Languages.Current;
        var o = options.Value;
        var name = Sanitizer.Line(attachment.Filename, int.MaxValue);
        var about = About(messageId, attachment, name, bytes.Length);
        var extension = Extension(name);

        if (SniffImage(bytes) is { } mime)
        {
            if (bytes.Length <= o.MaxInlineImageBytes)
            {
                return Result(new TextContentBlock { Text = $"{MessageFormatter.UntrustedHeader}\n{t.ImageInline(about, mime)}" }, ImageContentBlock.FromBytes(bytes, mime));
            }

            return Saved(t.ImageTooBigForInline(about, MessageFormatter.Size(o.MaxInlineImageBytes)), bytes, messageId, attachment.Id, extension);
        }

        if (extension is not null && TextExtensions.Contains(extension) && TryDecodeUtf8(bytes) is { } decoded)
        {
            return Result(new TextContentBlock { Text = FencedText(about, Sanitizer.Text(decoded), offset) });
        }

        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]))
        {
            return Saved(t.LegacyOffice(about), bytes, messageId, attachment.Id, extension);
        }

        return Saved(t.NotImageOrText(about), bytes, messageId, attachment.Id, extension);
    }

    /// <summary>One chunk of untrusted text inside a fresh nonce fence, with the next offset or the end-of-text label.</summary>
    private string FencedText(string about, string text, int offset)
    {
        var t = Languages.Current;
        if (offset > 0 && offset >= text.Length)
        {
            throw ToolInput.BadRequest(t.OffsetBeyondText(text.Length));
        }

        var chunk = Math.Max(1000, options.Value.MaxOutputChars - 600);
        var end = Math.Min(text.Length, offset + chunk);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
        {
            end--;
        }

        var part = text[offset..end];
        string nonce;
        do
        {
            nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        }
        while (part.Contains(nonce, StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder()
            .Append(MessageFormatter.UntrustedHeader).Append('\n')
            .Append(t.FenceIntro(about, offset, end, text.Length)).Append('\n')
            .Append("<<<attachment-").Append(nonce).Append('\n')
            .Append(part).Append('\n')
            .Append("attachment-").Append(nonce).Append(">>>\n")
            .Append(end < text.Length ? t.NextPart(MessageFormatter.OffsetToken(end)) : t.EndOfText);
        return sb.ToString();
    }

    /// <param name="reason">The sentence saying why the content is not returned, without a final period.</param>
    private CallToolResult Saved(string reason, byte[] bytes, ulong messageId, ulong attachmentId, string? extension)
    {
        var (path, marked) = Save(bytes, $"{MessageFormatter.Id(messageId)}_{MessageFormatter.Id(attachmentId)}.{DiskExtension(extension)}");
        return Result(new TextContentBlock { Text = $"{MessageFormatter.UntrustedHeader}\n{Languages.Current.SavedFile(reason, path, marked)}" });
    }

    private static string DiskExtension(string? extension) =>
        extension is not null && SafeDiskExtensions.Contains(extension) ? extension : "bin";

    /// <summary>Writes the file unless the same size is already there and (re)writes its mark of the web.</summary>
    private (string Path, bool Marked) Save(byte[] bytes, string fileName)
    {
        var directory = Path.GetFullPath(paths.DownloadsDirectory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new DiscordApiException(DiscordErrorKind.BadRequest, Languages.Current.InvalidAttachmentName);
        }

        Directory.CreateDirectory(directory);
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
            }
        }
        catch (IOException) when (File.Exists(path))
        {
            // Being written by another server process right now: its copy is the same file.
        }

        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            return (path, true);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // Not NTFS (e.g. DISCORD_MCP_HOME on a FAT drive): the file stays without the mark.
            return (path, false);
        }
    }

    private static CallToolResult Result(params ContentBlock[] blocks) => new() { Content = [.. blocks] };

    private static DiscordApiException TooLarge(long limit) =>
        new(DiscordErrorKind.BadRequest, Languages.Current.AttachmentTooLargeSkipped(limit / (1024 * 1024)));

    /// <summary>Lower-case extension after the last dot, letters and digits only; null otherwise.</summary>
    internal static string? Extension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot < 0 || dot == fileName.Length - 1)
        {
            return null;
        }

        var extension = fileName[(dot + 1)..].ToLowerInvariant();
        return extension.Length <= 10 && extension.All(char.IsAsciiLetterOrDigit) ? extension : null;
    }

    internal static string? SniffImage(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (b.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8))
        {
            return "image/gif";
        }

        return b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..12].SequenceEqual("WEBP"u8) ? "image/webp" : null;
    }

    private static string? TryDecodeUtf8(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return text.StartsWith('\uFEFF') ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>The <c>ex</c> query parameter of a signed CDN URL: hex Unix seconds.</summary>
    internal static DateTimeOffset? ExpiresAt(Uri url)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&'))
        {
            if (pair.StartsWith("ex=", StringComparison.Ordinal)
                && long.TryParse(pair.AsSpan(3), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var seconds)
                && seconds is > 0 and < 253_402_300_799)
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
        }

        return null;
    }
}
