using DiscordMcp.Discord;
using DiscordMcp.Localization;
using Microsoft.Extensions.Options;

namespace DiscordMcp.Tools;

/// <summary>
/// The budget of one tool call, flowing through async code (AsyncLocal). The governor counts every API request
/// against <see cref="Current"/> and refuses once <see cref="MaxCalls"/> is reached; requests made inside
/// <see cref="Exempt"/> (the guild-context warm-up) count only toward the rolling budgets.
/// </summary>
public sealed class ToolScope : IDisposable
{
    private static readonly AsyncLocal<ToolScope?> CurrentScope = new();
    private readonly ToolScope? _previous;
    private int _calls;
    private int _exemptDepth;

    private ToolScope(string name, int maxCalls, DateTimeOffset deadline)
    {
        Name = name;
        MaxCalls = maxCalls;
        Deadline = deadline;
        _previous = CurrentScope.Value;
        CurrentScope.Value = this;
    }

    public static ToolScope? Current => CurrentScope.Value;

    public string Name { get; }

    public int MaxCalls { get; }

    /// <summary>The tool call must finish by then (MaxToolWait); waits that would pass it fail fast instead.</summary>
    public DateTimeOffset Deadline { get; }

    public int Calls => Volatile.Read(ref _calls);

    public bool IsExempt => Volatile.Read(ref _exemptDepth) > 0;

    public static ToolScope Begin(string name, int maxCalls, DateTimeOffset deadline) => new(name, maxCalls, deadline);

    /// <summary>Counts one API request (retries included). Returns false when the per-tool budget is exhausted.</summary>
    public bool TryCount()
    {
        if (IsExempt)
        {
            return true;
        }

        if (Interlocked.Increment(ref _calls) <= MaxCalls)
        {
            return true;
        }

        Interlocked.Decrement(ref _calls);
        return false;
    }

    /// <summary>Requests inside the returned scope do not count toward this tool's budget.</summary>
    public IDisposable Exempt()
    {
        Interlocked.Increment(ref _exemptDepth);
        return new Release(() => Interlocked.Decrement(ref _exemptDepth));
    }

    public void Dispose() => CurrentScope.Value = _previous;

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                release();
            }
        }
    }
}

/// <summary>Runs a tool body inside a <see cref="ToolScope"/> with the MaxToolWait deadline as a cancellation.</summary>
public sealed class ToolRunner(IOptions<DiscordOptions> options, TimeProvider time)
{
    public async Task<T> RunAsync<T>(string toolName, Func<CancellationToken, Task<T>> body, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var wait = TimeSpan.FromSeconds(o.MaxToolWaitSeconds);
        using var scope = ToolScope.Begin(toolName, o.MaxApiCallsPerTool, time.GetUtcNow() + wait);
        using var deadline = new CancellationTokenSource(wait, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await body(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DiscordApiException(
                DiscordErrorKind.RateLimited,
                Languages.Current.ToolTimeout(o.MaxToolWaitSeconds));
        }
    }
}
