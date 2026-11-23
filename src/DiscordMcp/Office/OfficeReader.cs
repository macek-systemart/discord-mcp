using System.Text.Json;
using System.Text.Json.Serialization;
using DiscordMcp.Localization;
using Microsoft.Extensions.Logging;

namespace DiscordMcp.Office;

public enum OfficeKind
{
    Zip,
    Word,
    Excel,
    PowerPoint,
    OpenDocumentText,
    OpenDocumentSpreadsheet,
    OpenDocumentPresentation,
}

/// <summary>
/// The child's answer for a ZIP-based file. Produced from an untrusted file by a process that may be compromised, so
/// the parent validates <see cref="Kind"/> and sanitizes <see cref="Text"/> before use.
/// </summary>
public sealed record OfficeExtract
{
    public bool Failed { get; init; }

    public OfficeKind Kind { get; init; }

    public string Text { get; init; } = "";

    /// <summary>The text stops early (character, entry or byte cap, or a broken part).</summary>
    public bool Truncated { get; init; }
}

public interface IOfficeReader
{
    /// <summary>Null when the file could not be read at all (crash, timeout, oversized or malformed answer).</summary>
    Task<OfficeExtract?> ReadAsync(string path, CancellationToken cancellationToken);
}

internal static class OfficeJson
{
    public static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// Parses in <c>DiscordMcp.exe office-extract</c> (see <see cref="ChildProcess"/>). The child writes section labels into
/// the text, so it is told the active language explicitly instead of resolving its own.
/// </summary>
internal sealed class ChildProcessOfficeReader(ILogger<ChildProcessOfficeReader> logger) : IOfficeReader
{
    /// <summary>The child's command line: verb, file and the language code.</summary>
    internal static string[] Arguments(string path, Texts language) => [OfficeCommand.Verb, path, language.Code];

    public Task<OfficeExtract?> ReadAsync(string path, CancellationToken cancellationToken) =>
        ChildProcess.RunJsonAsync<OfficeExtract>(Arguments(path, Languages.Current), OfficeJson.Options, logger, cancellationToken);
}

/// <summary>
/// The hidden <c>office-extract &lt;file&gt; [language]</c> verb: prints an <see cref="OfficeExtract"/> as JSON. The
/// language is a code from <see cref="Languages"/>; without or with an unknown one the child uses its own default.
/// </summary>
internal static class OfficeCommand
{
    public const string Verb = "office-extract";

    public static int Run(string[] args)
    {
        using var watchdog = ChildProcess.StartWatchdog();
        if (args.Length is not (1 or 2) || ChildProcess.ReadInput(args[0]) is not { } bytes)
        {
            return 2;
        }

        var language = (args.Length == 2 ? Languages.Find(args[1]) : null) ?? Languages.Current;
        var result = OfficeExtractor.Extract(bytes, language);
        using var stdout = Console.OpenStandardOutput();
        JsonSerializer.Serialize(stdout, result, OfficeJson.Options);
        return 0;
    }
}
