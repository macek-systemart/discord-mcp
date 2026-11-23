using System.Globalization;

namespace DiscordMcp.Http;

/// <summary>A request that the read-only guards refuse. Always a programming error, never a Discord response.</summary>
public sealed class RequestGuardException(string message) : InvalidOperationException(message);

/// <summary>What the API guard lets through: the configured guild, and account endpoints only for the CLI verbs.</summary>
public sealed class ApiScope(ulong? guildId, bool allowAccountEndpoints)
{
    public ulong? GuildId { get; private set; } = guildId;

    /// <summary><c>/users/@me</c>, <c>/users/@me/guilds</c> and the probe-only routes. Never enabled in the MCP server.</summary>
    public bool AllowAccountEndpoints { get; } = allowAccountEndpoints;

    public void SetGuild(ulong guildId) => GuildId = guildId;
}

/// <summary>
/// Read-only allowlist for <c>https://discord.com/api/v10</c>. A request passes only if it is a GET without a body
/// whose path matches one route of the verified endpoint table segment by segment.
/// </summary>
public static class ApiGuard
{
    public const string Host = "discord.com";
    public const string BasePath = "/api/v10/";
    public const string BaseAddress = "https://discord.com/api/v10/";

    /// <summary>CLI verbs and the probe only. The probe (2026-11-23) confirmed the server needs none of these.</summary>
    private static readonly string[] AccountRoutes =
    [
        "users/@me",
        "users/@me/guilds",
        "guilds/{g}/roles", // guild object already carries roles
        "guilds/{g}/threads/active", // 403 code 20002 for user tokens
        "channels/{c}/threads/archived/public", // threads/search covers archived threads
        "channels/{c}/threads/archived/private",
        "channels/{c}/users/@me/threads/archived/private",
    ];

    /// <summary>Everything the MCP server may call (each verified by the probe with a user token).</summary>
    private static readonly string[] GuildRoutes =
    [
        "guilds/{g}",
        "guilds/{g}/channels",
        "guilds/{g}/messages/search",
        "users/@me/guilds/{g}/member",
        "channels/{c}",
        "channels/{c}/messages",
        "channels/{c}/messages/pins",
        "channels/{c}/threads/search",
    ];

    public static void Validate(HttpRequestMessage request, ApiScope scope)
    {
        var path = request.RequestUri?.IsAbsoluteUri == true ? request.RequestUri.AbsolutePath : "?";
        if (request.Method != HttpMethod.Get)
        {
            throw Refuse("only GET is allowed", request.Method, path);
        }

        if (request.Content is not null)
        {
            throw Refuse("request body is not allowed", request.Method, path);
        }

        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != 443
            || uri.UserInfo.Length > 0)
        {
            throw Refuse("only https://discord.com is allowed", request.Method, path);
        }

        if (HasTraversal(PathPart(uri.OriginalString)))
        {
            throw Refuse("path traversal", request.Method, path);
        }

        if (!IsAllowedPath(uri.AbsolutePath, scope))
        {
            throw Refuse("route is not on the allowlist", request.Method, path);
        }
    }

    internal static bool IsAllowedPath(string path, ApiScope scope)
    {
        if (!path.StartsWith(BasePath, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = path[BasePath.Length..].Split('/');
        var routes = scope.AllowAccountEndpoints ? AccountRoutes.Concat(GuildRoutes) : GuildRoutes;
        return routes.Any(route => Matches(route.Split('/'), segments, scope.GuildId));
    }

    /// <summary>
    /// The raw text before the query. Traversal only matters in the path; query values are escaped and may contain
    /// ".." or "/" (search text such as "čekej..." or a link).
    /// </summary>
    internal static string PathPart(string raw)
    {
        var end = raw.IndexOfAny(['?', '#']);
        return end < 0 ? raw : raw[..end];
    }

    internal static bool HasTraversal(string raw) =>
        raw.Contains("..", StringComparison.Ordinal)
        || raw.Contains('\\')
        || raw.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || raw.Contains("%5c", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSnowflake(string segment) =>
        segment.Length is > 0 and <= 20
        && segment[0] != '0'
        && segment.All(char.IsAsciiDigit)
        && ulong.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool Matches(string[] route, string[] segments, ulong? guildId)
    {
        if (route.Length != segments.Length)
        {
            return false;
        }

        for (var i = 0; i < route.Length; i++)
        {
            var ok = route[i] switch
            {
                "{g}" => guildId is { } g && segments[i] == g.ToString(CultureInfo.InvariantCulture),
                "{c}" => IsSnowflake(segments[i]),
                _ => string.Equals(route[i], segments[i], StringComparison.Ordinal),
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static RequestGuardException Refuse(string reason, HttpMethod method, string path) =>
        new($"Discord API request blocked ({reason}): {method} {path}");
}

/// <summary>
/// Guard for attachment downloads. The CDN client never carries the token: a request with an Authorization header
/// is refused, so a URL mixed up between the two clients cannot leak it.
/// </summary>
public static class CdnGuard
{
    public static readonly string[] Hosts = ["cdn.discordapp.com", "media.discordapp.net"];

    public static void Validate(HttpRequestMessage request)
    {
        // Only the path is ever reported: the query holds the CDN signature.
        var path = request.RequestUri?.IsAbsoluteUri == true ? request.RequestUri.AbsolutePath : "?";
        if (request.Method != HttpMethod.Get)
        {
            throw Refuse("only GET is allowed", request.Method, path);
        }

        if (request.Content is not null)
        {
            throw Refuse("request body is not allowed", request.Method, path);
        }

        if (request.Headers.Authorization is not null || request.Headers.Contains("Authorization"))
        {
            throw Refuse("CDN requests must not carry credentials", request.Method, path);
        }

        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri
            || uri.Scheme != Uri.UriSchemeHttps
            || !Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || uri.Port != 443
            || uri.UserInfo.Length > 0)
        {
            throw Refuse("only the Discord CDN hosts are allowed", request.Method, path);
        }

        if (HasTraversalSegment(ApiGuard.PathPart(uri.OriginalString)) || !uri.AbsolutePath.StartsWith("/attachments/", StringComparison.Ordinal))
        {
            throw Refuse("only attachment paths are allowed", request.Method, path);
        }
    }

    /// <summary>
    /// Attachment file names may legitimately contain "..", e.g. "zapis..v2.pdf": only whole "." / ".." segments,
    /// backslashes and encoded separators or dots count as traversal here.
    /// </summary>
    internal static bool HasTraversalSegment(string rawPath) =>
        rawPath.Contains('\\')
        || rawPath.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        || rawPath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
        || rawPath.Contains("%5c", StringComparison.OrdinalIgnoreCase)
        || rawPath.Split('/').Any(segment => segment is "." or "..");

    private static RequestGuardException Refuse(string reason, HttpMethod method, string path) =>
        new($"Discord CDN request blocked ({reason}): {method} {path}");
}
