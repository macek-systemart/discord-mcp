using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiscordMcp.Discord;
using DiscordMcp.Localization;

namespace DiscordMcp.Http;

/// <summary>Governor state that must survive a Claude Desktop restart.</summary>
public sealed class GovernorState
{
    public const string BreakerBlock = "breaker";
    public const string RateLimitBlock = "rate-limit";

    /// <summary>The <see cref="HardStopReason"/> code for a 401.</summary>
    public const string UnauthorizedStop = "unauthorized";

    /// <summary>The <see cref="HardStopReason"/> code for a captcha or account verification demand.</summary>
    public const string CaptchaStop = "captcha";

    /// <summary>
    /// Set by 401 / captcha / account verification. Cleared only by <c>set-token</c>. A stable code
    /// (<see cref="UnauthorizedStop"/>, <see cref="CaptchaStop"/>), never display text: the text is rendered in the
    /// language of the process that reports it. Any other value (an older version stored Czech text) still stops
    /// access and is reported with a generic reason.
    /// </summary>
    public string? HardStopReason { get; set; }

    public DateTimeOffset? HardStopAt { get; set; }

    /// <summary>No request may be sent before this instant (429 with a long retry_after, open breaker).</summary>
    public DateTimeOffset? BlockedUntil { get; set; }

    /// <summary>Why <see cref="BlockedUntil"/> is set: <see cref="BreakerBlock"/> or <see cref="RateLimitBlock"/> (null = rate limit).</summary>
    public string? BlockReason { get; set; }

    /// <summary>Send times of recent API calls, for the rolling 10-minute and daily budgets (pruned to 24 h).</summary>
    public List<DateTimeOffset> RecentCalls { get; set; } = [];

    /// <summary>Recent 403/429 responses, for the breaker (pruned to the breaker window).</summary>
    public List<DateTimeOffset> RecentInvalid { get; set; } = [];
}

/// <summary>
/// Thread-safe, file-backed <see cref="GovernorState"/> (<c>%LOCALAPPDATA%\discord-mcp\state.json</c>). The file is
/// re-read whenever another process changed it, and every update is a read-modify-write under a machine-wide mutex,
/// so the CLI (<c>set-token</c> clearing a hard stop, budget use of <c>check</c>) and one or more running MCP servers
/// share one state: one budget, one breaker, one hard stop.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    private readonly Mutex _fileMutex;
    private readonly Lock _lock = new();
    private GovernorState? _state;
    private DateTime _loadedStamp;

    public StateStore(AppPaths paths)
    {
        _paths = paths;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(paths.StateFile).ToUpperInvariant())))[..16];
        _fileMutex = new Mutex(initiallyOwned: false, $@"Local\discord-mcp-state-{key}");
    }

    public T Read<T>(Func<GovernorState, T> read)
    {
        lock (_lock)
        {
            return read(Current());
        }
    }

    public void Update(Action<GovernorState> change)
    {
        lock (_lock)
        {
            AcquireFileMutex();
            try
            {
                var state = Current();
                change(state);
                Save(state);
            }
            finally
            {
                _fileMutex.ReleaseMutex();
            }
        }
    }

    private void AcquireFileMutex()
    {
        try
        {
            if (!_fileMutex.WaitOne(TimeSpan.FromSeconds(5)))
            {
                throw new DiscordApiException(DiscordErrorKind.Upstream, Languages.Current.StateLockedByOtherProcess);
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous holder exited without releasing: the mutex is ours now and the file was replaced atomically.
        }
    }

    private GovernorState Current()
    {
        var stamp = Stamp();
        if (_state is null || stamp != _loadedStamp)
        {
            try
            {
                _state = Load();
                _loadedStamp = stamp;
            }
            catch (IOException) when (_state is not null)
            {
                // Briefly unreadable (antivirus, another writer): keep the last known state and retry next time.
            }
            catch (IOException)
            {
                // Fail closed: never start from an empty state, which would forget a hard stop or spent budget.
                throw new DiscordApiException(DiscordErrorKind.Upstream, Languages.Current.StateFileUnavailable);
            }
        }

        return _state;
    }

    private DateTime Stamp() => File.Exists(_paths.StateFile) ? File.GetLastWriteTimeUtc(_paths.StateFile) : DateTime.MinValue;

    private GovernorState Load()
    {
        if (!File.Exists(_paths.StateFile))
        {
            return new GovernorState();
        }

        var bytes = File.ReadAllBytes(_paths.StateFile);
        try
        {
            return JsonSerializer.Deserialize<GovernorState>(bytes, Json) ?? new GovernorState();
        }
        catch (JsonException)
        {
            // A corrupted file cannot be repaired by waiting: start empty, the next write replaces it.
            return new GovernorState();
        }
    }

    private void Save(GovernorState state)
    {
        try
        {
            Directory.CreateDirectory(_paths.BaseDirectory);
            var temp = _paths.StateFile + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, Json));
            File.Move(temp, _paths.StateFile, overwrite: true);
            _loadedStamp = Stamp();
        }
        catch (IOException)
        {
            // Another process holds the file for a moment: keep the in-memory state, the next update writes it.
        }
    }
}
