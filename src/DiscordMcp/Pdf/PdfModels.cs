using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscordMcp.Pdf;

/// <summary>What <c>get_attachment</c> asks the PDF child process for.</summary>
/// <param name="Page">1-based page to read alone, or null for the text of all pages.</param>
/// <param name="ImageBytes">JPEG size budget for one page picture; 0 means no picture.</param>
public sealed record PdfRequest(int? Page, int ImageBytes);

public enum PdfFailure
{
    None,
    Encrypted,
    Unreadable,
    PageOutOfRange,
}

public enum PdfImageProblem
{
    None,

    /// <summary>More pixels than <see cref="PdfExtractor.MaxImagePixels"/>, or no size fits the budget.</summary>
    TooLarge,

    /// <summary>An encoding neither PdfPig nor GDI+ decodes (JPEG 2000, JBIG2, chained filters).</summary>
    Unsupported,

    /// <summary>The image data is broken.</summary>
    Undecodable,
}

public sealed record PdfPageText(int Number, string Text, bool Garbled);

public sealed record PdfPageImage(int Page, byte[] Jpeg, int Width, int Height);

/// <summary>
/// The child's answer. Produced from an untrusted file by a process that may be compromised, so the parent validates
/// it (<c>AttachmentTools</c>) and sanitizes every text before use.
/// </summary>
public sealed record PdfExtract
{
    public PdfFailure Failure { get; init; }

    public int PageCount { get; init; }

    public IReadOnlyList<PdfPageText> Pages { get; init; } = [];

    /// <summary>The text stops early (page or character cap).</summary>
    public bool Truncated { get; init; }

    /// <summary>Pages read whose largest image covers a large part of the page: scans and photos.</summary>
    public IReadOnlyList<int> PicturePages { get; init; } = [];

    public PdfPageImage? Image { get; init; }

    public PdfImageProblem ImageProblem { get; init; }

    public int ImageProblemPage { get; init; }
}

internal static class PdfJson
{
    public static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
}
