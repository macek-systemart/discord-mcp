using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DiscordMcp;

/// <summary>
/// Runs <c>DiscordMcp.exe &lt;verb&gt; …</c>, the parsers for untrusted files (PDF, Office, ZIP): a stack overflow, a hang
/// or runaway memory ends only the child, which is killed after <see cref="Timeout"/> and also ends itself shortly
/// after (<see cref="StartWatchdog"/>). The child never loads the token; its JSON answer is as untrusted as the file.
/// </summary>
internal static class ChildProcess
{
    /// <summary>Well inside the 20 s budget of a tool call, so a slow file falls back to "saved" instead of an error.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    private const int MaxOutputBytes = 8 * 1024 * 1024;

    /// <summary>Null when the child failed, timed out or answered with something that is not a <typeparamref name="T"/>.</summary>
    public static async Task<T?> RunJsonAsync<T>(IEnumerable<string> arguments, JsonSerializerOptions json, ILogger logger, CancellationToken cancellationToken)
        where T : class
    {
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "DiscordMcp.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["DOTNET_GCHeapHardLimit"] = "0x30000000"; // 768 MB managed heap

        using var process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            process.StandardInput.Close();
            // Drained, never logged: an exception message could quote the file.
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var stdout = await ReadCappedAsync(process.StandardOutput.BaseStream, timeout.Token).ConfigureAwait(false);
            if (stdout is null)
            {
                logger.LogWarning("Child process output is over {Limit} bytes.", MaxOutputBytes);
                return null;
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                logger.LogWarning("Child process failed with exit code {ExitCode}.", process.ExitCode);
                return null;
            }

            return JsonSerializer.Deserialize<T>(stdout, json);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Child process timed out after {Seconds} s.", Timeout.TotalSeconds);
            return null;
        }
        catch (JsonException)
        {
            logger.LogWarning("Child process returned malformed output.");
            return null;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>In the child: ends the process even when the parent has died or stopped waiting.</summary>
    public static Timer StartWatchdog() =>
        new(_ => Environment.Exit(3), null, Timeout + TimeSpan.FromSeconds(5), System.Threading.Timeout.InfiniteTimeSpan);

    /// <summary>In the child: the file named by the parent, or null when it is missing or over 100 MB.</summary>
    public static byte[]? ReadInput(string path)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length <= 100L * 1024 * 1024 ? File.ReadAllBytes(file.FullName) : null;
    }

    private static async Task<byte[]?> ReadCappedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxOutputBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
