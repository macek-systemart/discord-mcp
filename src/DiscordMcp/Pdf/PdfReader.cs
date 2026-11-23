using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DiscordMcp.Pdf;

public interface IPdfReader
{
    /// <summary>Null when the PDF could not be read at all (crash, timeout, oversized or malformed answer).</summary>
    Task<PdfExtract?> ReadAsync(string path, PdfRequest request, CancellationToken cancellationToken);
}

/// <summary>Parses in <c>DiscordMcp.exe pdf-extract</c> (see <see cref="ChildProcess"/>).</summary>
internal sealed class ChildProcessPdfReader(ILogger<ChildProcessPdfReader> logger) : IPdfReader
{
    public Task<PdfExtract?> ReadAsync(string path, PdfRequest request, CancellationToken cancellationToken) =>
        ChildProcess.RunJsonAsync<PdfExtract>(
            [PdfCommand.Verb, path, JsonSerializer.Serialize(request, PdfJson.Options)], PdfJson.Options, logger, cancellationToken);
}

/// <summary>The hidden <c>pdf-extract &lt;file&gt; &lt;request json&gt;</c> verb: prints a <see cref="PdfExtract"/> as JSON.</summary>
internal static class PdfCommand
{
    public const string Verb = "pdf-extract";

    public static int Run(string[] args)
    {
        using var watchdog = ChildProcess.StartWatchdog();
        if (args.Length != 2
            || JsonSerializer.Deserialize<PdfRequest>(args[1], PdfJson.Options) is not { } request
            || ChildProcess.ReadInput(args[0]) is not { } bytes)
        {
            return 2;
        }

        var result = PdfExtractor.Extract(bytes, request);
        using var stdout = Console.OpenStandardOutput();
        JsonSerializer.Serialize(stdout, result, PdfJson.Options);
        return 0;
    }
}
