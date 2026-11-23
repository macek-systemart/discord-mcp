using System.Net;
using System.Text.Json;
using DiscordMcp.Discord;
using DiscordMcp.Localization;
using DiscordMcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordMcp.Http;

/// <summary>
/// Singleton holding every piece of request-shaping state. Message handlers created by IHttpClientFactory are
/// recycled every two minutes, so they stay stateless and delegate here.
/// Responsibilities: one shared pacing gate for API and CDN; refusal without sending while hard-stopped, blocked
/// (429, breaker) or over a budget (per tool call, rolling 10 minutes, rolling 24 hours); failing fast when pacing
/// would pass the tool call's deadline; latching 401 / captcha / verification responses into the persisted hard stop;
/// the 403/429 breaker. CDN downloads are paced but never count toward budgets or the breaker.
/// </summary>
public sealed class RequestGovernor(
    TimeProvider time,
    IOptions<DiscordOptions> options,
    StateStore state,
    ILogger<RequestGovernor> logger)
{
    private static readonly TimeSpan ShortWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DayWindow = TimeSpan.FromHours(24);
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;

    /// <summary>End of the last recorded 429 or breaker block (may lie in the past).</summary>
    public DateTimeOffset? BlockedUntil => state.Read(s => s.BlockedUntil);

    /// <summary>Waits for the next pacing slot of an API request.</summary>
    public Task WaitTurnAsync(CancellationToken cancellationToken) => WaitTurnAsync(isCdn: false, cancellationToken);

    /// <summary>
    /// Waits for the next pacing slot. Throws without sending (and without sleeping) while hard-stopped, blocked or
    /// over a budget, and when the pacing wait would end after <see cref="ToolScope.Deadline"/>. A granted API slot is
    /// counted toward the current tool call and recorded for the rolling budgets.
    /// </summary>
    public async Task WaitTurnAsync(bool isCdn, CancellationToken cancellationToken)
    {
        ThrowIfStopped();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var scope = ToolScope.Current;
            ThrowIfStopped();
            if (!isCdn)
            {
                ThrowIfOverBudget(scope);
            }

            var now = time.GetUtcNow();
            var interval = TimeSpan.FromMilliseconds(options.Value.MinRequestIntervalMs);

            // Pace against the latest send of any process sharing state.json (another server, the CLI), not just ours.
            var lastSend = isCdn
                ? _lastSend
                : state.Read(s => s.RecentCalls.Count > 0 ? s.RecentCalls.Max() : DateTimeOffset.MinValue) is var shared && shared > _lastSend
                    ? shared
                    : _lastSend;
            var wait = lastSend == DateTimeOffset.MinValue ? TimeSpan.Zero : lastSend + interval - now;
            if (wait > TimeSpan.Zero)
            {
                if (scope is not null && now + wait > scope.Deadline)
                {
                    throw new DiscordApiException(
                        DiscordErrorKind.RateLimited,
                        Languages.Current.RequestWouldMissDeadline,
                        now + wait);
                }

                await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);

                // A response that arrived during the wait may have stopped or blocked further traffic.
                ThrowIfStopped();
                now = time.GetUtcNow();
            }

            _lastSend = now;
            if (!isCdn)
            {
                state.Update(s =>
                {
                    s.RecentCalls.RemoveAll(t => t <= now - DayWindow);
                    s.RecentCalls.Add(now);
                });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ThrowIfStopped()
    {
        var t = Languages.Current;
        var (reason, blockedUntil, blockReason) = state.Read(s => (s.HardStopReason, s.BlockedUntil, s.BlockReason));
        if (reason is not null)
        {
            // The state stores a code; the text is rendered here, in the language of the process that reports it.
            var text = reason switch
            {
                GovernorState.UnauthorizedStop => t.HardStopUnauthorized,
                GovernorState.CaptchaStop => t.HardStopCaptcha,
                _ => t.HardStopUnknown,
            };
            throw new DiscordApiException(DiscordErrorKind.HardStop, t.HardStopped(text));
        }

        if (blockedUntil is { } until && until > time.GetUtcNow())
        {
            if (blockReason == GovernorState.BreakerBlock)
            {
                throw new DiscordApiException(DiscordErrorKind.BreakerOpen, t.BreakerBlocked(DisplayTime.FormatClock(until)), until);
            }

            throw new DiscordApiException(DiscordErrorKind.RateLimited, t.RateLimitedUntil(DisplayTime.FormatClock(until)), until);
        }
    }

    /// <summary>
    /// Inspects an API response: 401 / captcha / verification latch the hard stop, 429 blocks every request until its
    /// retry time, and 403 / 429 (except <c>X-RateLimit-Scope: shared</c>) feed the breaker. CDN responses are ignored:
    /// an expired attachment URL is not an invalid API request.
    /// </summary>
    public async Task ObserveAsync(HttpResponseMessage response, bool isCdn, CancellationToken cancellationToken)
    {
        if (isCdn)
        {
            return;
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized:
                Latch(GovernorState.UnauthorizedStop);
                break;

            case HttpStatusCode.TooManyRequests:
                var limited = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                var retryAfter = ReadRetryAfter(response, limited) ?? TimeSpan.FromSeconds(60); // no hint: back off, never retry blindly
                var until = time.GetUtcNow() + retryAfter;
                state.Update(s => Block(s, until, GovernorState.RateLimitBlock));
                logger.LogWarning("Discord answered 429; requests blocked for {Seconds:F1} s.", retryAfter.TotalSeconds);
                if (!IsSharedScope(response))
                {
                    RecordInvalid();
                }

                break;

            case HttpStatusCode.BadRequest or HttpStatusCode.Forbidden:
                var refused = await ReadBodyAsync(response, cancellationToken).ConfigureAwait(false);
                if (RequiresVerification(refused))
                {
                    Latch(GovernorState.CaptchaStop);
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    RecordInvalid();
                }

                break;
        }
    }

    /// <summary>Used by <c>set-token</c> only.</summary>
    public void ClearHardStop() => state.Update(s =>
    {
        s.HardStopReason = null;
        s.HardStopAt = null;
    });

    /// <summary>Discord's own <c>retry_after</c> (seconds, fractional) from the body, else the Retry-After header.</summary>
    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response, byte[] body)
    {
        using var json = ParseJson(body);
        if (json?.RootElement is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("retry_after", out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var seconds)
            && seconds >= 0)
        {
            return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfter.TotalSeconds));
        }

        return response.Headers.RetryAfter?.Delta is { } delta && delta >= TimeSpan.Zero
            ? (delta < MaxRetryAfter ? delta : MaxRetryAfter)
            : null;
    }

    /// <summary>A small JSON body, or null when it is empty, too large or not JSON.</summary>
    internal static JsonDocument? ParseJson(byte[] body)
    {
        try
        {
            return body.Length is > 0 and < 64 * 1024 ? JsonDocument.Parse(body) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void ThrowIfOverBudget(ToolScope? scope)
    {
        var t = Languages.Current;
        var o = options.Value;
        var now = time.GetUtcNow();
        var (day, recent) = state.Read(s => (
            RetryAt(s.RecentCalls, now, DayWindow, o.MaxApiCallsPerDay),
            RetryAt(s.RecentCalls, now, ShortWindow, o.MaxApiCallsPer10Minutes)));

        if (day is { } dayRetry)
        {
            throw new DiscordApiException(
                DiscordErrorKind.BudgetExceeded,
                t.DailyBudgetExhausted(o.MaxApiCallsPerDay, DisplayTime.Format(dayRetry)),
                dayRetry);
        }

        if (recent is { } recentRetry)
        {
            throw new DiscordApiException(
                DiscordErrorKind.BudgetExceeded,
                t.TenMinuteBudgetExhausted(o.MaxApiCallsPer10Minutes, DisplayTime.FormatClock(recentRetry)),
                recentRetry);
        }

        if (scope is not null && !scope.TryCount())
        {
            throw new DiscordApiException(DiscordErrorKind.BudgetExceeded, t.ToolBudgetExceeded(scope.MaxCalls));
        }
    }

    /// <summary>Null while fewer than <paramref name="limit"/> calls lie in the window; otherwise when enough have left it.</summary>
    private static DateTimeOffset? RetryAt(List<DateTimeOffset> calls, DateTimeOffset now, TimeSpan window, int limit)
    {
        var inWindow = calls.Where(t => t > now - window).Order().ToList();
        return inWindow.Count < limit ? null : inWindow[inWindow.Count - limit] + window;
    }

    private void RecordInvalid()
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var opened = false;
        state.Update(s =>
        {
            s.RecentInvalid.RemoveAll(t => t <= now - TimeSpan.FromMinutes(o.BreakerWindowMinutes));
            s.RecentInvalid.Add(now);
            if (s.RecentInvalid.Count >= o.BreakerThreshold)
            {
                Block(s, now + TimeSpan.FromMinutes(o.BreakerOpenMinutes), GovernorState.BreakerBlock);
                s.RecentInvalid.Clear();
                opened = true;
            }
        });

        if (opened)
        {
            logger.LogWarning(
                "Breaker open: {Count} rejected Discord requests within {Window} min; requests blocked for {Minutes} min.",
                o.BreakerThreshold,
                o.BreakerWindowMinutes,
                o.BreakerOpenMinutes);
        }
    }

    /// <summary>The later block wins, and its reason with it.</summary>
    private static void Block(GovernorState s, DateTimeOffset until, string reason)
    {
        if (s.BlockedUntil is { } existing && existing >= until)
        {
            return;
        }

        s.BlockedUntil = until;
        s.BlockReason = reason;
    }

    private void Latch(string reason)
    {
        state.Update(s =>
        {
            s.HardStopReason = reason;
            s.HardStopAt = time.GetUtcNow();
        });
        logger.LogWarning("Discord access hard-stopped: {Reason}. Run set-token to resume.", reason);
    }

    private static bool IsSharedScope(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-RateLimit-Scope", out var values)
        && values.Any(v => string.Equals(v.Trim(), "shared", StringComparison.OrdinalIgnoreCase));

    private static bool RequiresVerification(byte[] body)
    {
        using var json = ParseJson(body);
        if (json?.RootElement is not { ValueKind: JsonValueKind.Object } root)
        {
            return false;
        }

        return root.TryGetProperty("captcha_key", out _)
            || root.TryGetProperty("captcha_sitekey", out _)
            || (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var c) && c == 40002);
    }

    /// <summary>Buffers the body so the caller can still read it afterwards.</summary>
    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }
}
