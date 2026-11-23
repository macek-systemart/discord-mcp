namespace DiscordMcp.Discord;

public enum DiscordErrorKind
{
    /// <summary>No token stored on this Windows install.</summary>
    NoToken,

    /// <summary>A latched stop (401, captcha, account verification) that only <c>set-token</c> clears.</summary>
    HardStop,

    Unauthorized,
    Forbidden,
    NotFound,
    RateLimited,
    BreakerOpen,
    BudgetExceeded,

    /// <summary>Refused locally: outside the configured guild, a DM, hidden or history-less channel, wrong channel type.</summary>
    NotAllowed,

    /// <summary>Search answered 202: the index is still being built.</summary>
    IndexNotReady,

    BadRequest,
    Upstream,
}

/// <summary>
/// The only exception type tools surface to Claude. Messages are localized text (see <c>Localization</c>) meant for
/// the user: they never contain the token, request headers or signed CDN URLs.
/// </summary>
public sealed class DiscordApiException(DiscordErrorKind kind, string message, DateTimeOffset? retryAt = null) : Exception(message)
{
    public DiscordErrorKind Kind { get; } = kind;

    /// <summary>When the operation may be tried again (rate limit, breaker, budget), if known.</summary>
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
