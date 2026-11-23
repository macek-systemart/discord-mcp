using System.Globalization;
using System.Net;
using System.Text.Json;
using DiscordMcp.Http;
using DiscordMcp.Localization;
using DiscordMcp.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DiscordMcp.Discord;

/// <summary>
/// HTTP implementation of <see cref="IDiscordApi"/> over the guarded named clients. URLs are built from <c>ulong</c>s
/// and escaped query values only. Every Discord or network failure becomes a <see cref="DiscordApiException"/> with
/// fixed text in the active language; guard refusals (<see cref="RequestGuardException"/>) and caller cancellation propagate unchanged.
/// </summary>
public sealed class DiscordApi(
    IHttpClientFactory clients,
    IOptions<DiscordOptions> options,
    RequestGovernor governor,
    TimeProvider time,
    ILogger<DiscordApi> logger) : IDiscordApi
{
    private const int IndexNotReadyCode = 110000;

    /// <summary>A 429 retry starts this long after the governor's block ends, so a coarse clock cannot hit the block.</summary>
    private static readonly TimeSpan RetryMargin = TimeSpan.FromMilliseconds(50);

    private sealed record Reply(HttpStatusCode Status, byte[] Body, TimeSpan? RetryAfter);

    public async Task<DiscordGuild> GetGuildAsync(CancellationToken cancellationToken) =>
        await GetAsync<DiscordGuild>(Url(Invariant($"guilds/{Guild()}")), search: false, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<DiscordChannel>> GetChannelsAsync(CancellationToken cancellationToken) =>
        await GetAsync<List<DiscordChannel>>(Url(Invariant($"guilds/{Guild()}/channels")), search: false, cancellationToken).ConfigureAwait(false);

    public async Task<DiscordMember> GetMyMemberAsync(CancellationToken cancellationToken) =>
        await GetAsync<DiscordMember>(Url(Invariant($"users/@me/guilds/{Guild()}/member")), search: false, cancellationToken).ConfigureAwait(false);

    public async Task<DiscordChannel> GetChannelAsync(ulong channelId, CancellationToken cancellationToken)
    {
        Guild();
        return await GetAsync<DiscordChannel>(Url(Invariant($"channels/{channelId}")), search: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiscordMessage>> GetMessagesAsync(ulong channelId, MessageQuery query, CancellationToken cancellationToken)
    {
        Guild();
        Validate(query.Validate, Languages.Current.InvalidReadParameters);
        var url = Url(
            Invariant($"channels/{channelId}/messages"),
            ("limit", Number(query.Limit)),
            ("before", Id(query.Before)),
            ("after", Id(query.After)),
            ("around", Id(query.Around)));
        return await GetAsync<List<DiscordMessage>>(url, search: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PinsResponse> GetPinsAsync(ulong channelId, DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        Guild();
        if (limit is < 1 or > 50)
        {
            throw new DiscordApiException(DiscordErrorKind.BadRequest, Languages.Current.InvalidPinsParameters);
        }

        var url = Url(
            Invariant($"channels/{channelId}/messages/pins"),
            ("before", before?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)),
            ("limit", Number(limit)));
        return await GetAsync<PinsResponse>(url, search: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ThreadSearchResponse> SearchThreadsAsync(ulong channelId, ThreadQuery query, CancellationToken cancellationToken)
    {
        Guild();
        Validate(query.Validate, Languages.Current.InvalidThreadSearchParameters);
        var url = Url(
            Invariant($"channels/{channelId}/threads/search"),
            ("archived", Bool(query.Archived)),
            ("limit", Number(query.Limit)),
            ("offset", Number(query.Offset)));
        return await GetAsync<ThreadSearchResponse>(url, search: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MessageSearchResponse> SearchMessagesAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        var guild = Guild();
        Validate(query.Validate, Languages.Current.InvalidSearchParameters(string.Join(", ", SearchQuery.HasValues)));
        List<(string, string?)> parameters =
        [
            ("content", string.IsNullOrEmpty(query.Content) ? null : query.Content),
            ("author_id", Id(query.AuthorId)),
            ("channel_id", Id(query.ChannelId)),
            .. query.Has.Select(h => ("has", (string?)h)),
            ("min_id", Id(query.MinId)),
            ("max_id", Id(query.MaxId)),
            ("pinned", Bool(query.Pinned)),
            ("sort_by", query.SortBy),
            ("sort_order", query.SortOrder),
            ("offset", Number(query.Offset)),
            ("limit", Number(query.Limit)),
            ("include_nsfw", "true"),
        ];
        var url = Url(Invariant($"guilds/{guild}/messages/search"), [.. parameters]);
        return await GetAsync<MessageSearchResponse>(url, search: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DownloadedFile> DownloadAttachmentAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(HttpClientNames.Cdn);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Discord CDN answered {Status}.", (int)response.StatusCode);
                throw new DiscordApiException(DiscordErrorKind.NotFound, Languages.Current.AttachmentUnavailable);
            }

            // Neither the attachment's size nor Content-Length is trusted: the cap applies to the bytes actually read.
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    var megabytes = (maxBytes / (1024d * 1024)).ToString("0.#", CultureInfo.InvariantCulture);
                    throw new DiscordApiException(DiscordErrorKind.BadRequest, Languages.Current.DownloadTooLarge(megabytes));
                }

                buffer.Write(chunk, 0, read);
            }

            return new DownloadedFile(buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            logger.LogWarning("Discord CDN download failed ({Error}).", e.GetType().Name);
            throw new DiscordApiException(DiscordErrorKind.Upstream, Languages.Current.DownloadFailed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Discord CDN download timed out.");
            throw new DiscordApiException(DiscordErrorKind.Upstream, Languages.Current.DownloadTimeout);
        }
    }

    /// <summary>
    /// One logical GET: at most one retry after a short 429 and one after a search 202 (index not ready). Every send
    /// passes the governor, so retries count toward the tool budget and respect blocks.
    /// </summary>
    private async Task<T> GetAsync<T>(string url, bool search, CancellationToken cancellationToken)
        where T : class
    {
        var o = options.Value;
        var retriedRateLimit = false;
        var retriedIndex = false;
        while (true)
        {
            var reply = await SendAsync(url, cancellationToken).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var deadline = ToolScope.Current?.Deadline ?? now + TimeSpan.FromSeconds(o.MaxToolWaitSeconds);

            // Checked before deserializing: an index-not-ready answer must never look like an empty result.
            if (search && IsIndexing(reply))
            {
                var interval = TimeSpan.FromMilliseconds(o.MinRequestIntervalMs);
                var wait = reply.RetryAfter is { } hint && hint > interval ? hint : interval;
                if (retriedIndex || now + wait > deadline)
                {
                    throw new DiscordApiException(DiscordErrorKind.IndexNotReady, Languages.Current.IndexNotReady, now + wait);
                }

                retriedIndex = true;
                await Task.Delay(wait, time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (reply.Status == HttpStatusCode.TooManyRequests)
            {
                // The governor has already recorded the block (and maybe opened the breaker): wait for exactly that.
                var until = governor.BlockedUntil is { } blocked && blocked > now ? blocked : now;
                if (retriedRateLimit
                    || until - now > TimeSpan.FromSeconds(o.MaxAutoRetryAfterSeconds)
                    || until + RetryMargin > deadline)
                {
                    governor.ThrowIfStopped();
                    throw new DiscordApiException(DiscordErrorKind.RateLimited, Languages.Current.RateLimited429, until);
                }

                retriedRateLimit = true;
                await Task.Delay(until - now + RetryMargin, time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if ((int)reply.Status is < 200 or > 299)
            {
                if (reply.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
                {
                    // A captcha or account-verification demand has just latched the hard stop: say so now, and do not
                    // let the 403 land in the channel's negative cache as "no permission".
                    governor.ThrowIfStopped();
                }

                throw Failure(url, reply);
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(reply.Body, DiscordJson.Options);
            }
            catch (JsonException e)
            {
                // Path and position only (property names and indexes): never the message or the body, which hold content.
                logger.LogWarning(
                    "Discord API {Route}: response does not match the model at {JsonPath} (line {Line}, byte {Position}).",
                    Route(url), e.Path, e.LineNumber, e.BytePositionInLine);
                value = null;
            }

            if (value is null)
            {
                logger.LogWarning("Discord API {Route}: unexpected response shape.", Route(url));
                throw new DiscordApiException(DiscordErrorKind.Upstream, Languages.Current.UnexpectedResponse);
            }

            return value;
        }
    }

    private async Task<Reply> SendAsync(string url, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient(HttpClientNames.Api);
        try
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new Reply(response.StatusCode, body, RequestGovernor.ReadRetryAfter(response, body));
        }
        catch (HttpRequestException e)
        {
            logger.LogWarning("Discord API {Route}: network error ({Error}).", Route(url), e.HttpRequestError);
            throw new DiscordApiException(DiscordErrorKind.Upstream, Languages.Current.NetworkError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient's own timeout; a cancelled caller (tool deadline, client) gets its exception unchanged.
            logger.LogWarning("Discord API {Route}: no response in time.", Route(url));
            throw new DiscordApiException(DiscordErrorKind.Upstream, Languages.Current.NoResponseInTime);
        }
    }

    private DiscordApiException Failure(string url, Reply reply)
    {
        var t = Languages.Current;
        var status = (int)reply.Status;
        switch (reply.Status)
        {
            case HttpStatusCode.Unauthorized:
                return new(DiscordErrorKind.Unauthorized, t.Unauthorized401);
            case HttpStatusCode.Forbidden:
                return new(DiscordErrorKind.Forbidden, t.Forbidden403);
            case HttpStatusCode.NotFound:
                return new(DiscordErrorKind.NotFound, t.NotFound404);
            case HttpStatusCode.BadRequest:
                return new(DiscordErrorKind.BadRequest, t.BadRequest400(ErrorText(reply.Body)));
        }

        logger.LogWarning("Discord API {Route} answered {Status}.", Route(url), status);
        return new(DiscordErrorKind.Upstream, status >= 500 ? t.ServerTrouble(status) : t.UnexpectedStatus(status));
    }

    private static bool IsIndexing(Reply reply)
    {
        if (reply.Status == HttpStatusCode.Accepted)
        {
            return true;
        }

        if ((int)reply.Status is < 200 or > 299)
        {
            return false;
        }

        using var json = RequestGovernor.ParseJson(reply.Body);
        return json?.RootElement is { ValueKind: JsonValueKind.Object } root
            && root.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number
            && code.TryGetInt32(out var value)
            && value == IndexNotReadyCode;
    }

    /// <summary>Discord's <c>message</c> field without control characters, at most 200 characters.</summary>
    private static string? ErrorText(byte[] body)
    {
        using var json = RequestGovernor.ParseJson(body);
        if (json?.RootElement is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("message", out var message)
            || message.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = string.Concat(message.GetString()!.Where(c => !char.IsControl(c))).Trim();
        if (text.Length > 200)
        {
            text = text[..(char.IsHighSurrogate(text[199]) ? 199 : 200)];
        }

        return text.Length == 0 ? null : text;
    }

    private ulong Guild() =>
        options.Value.GuildId ?? throw new DiscordApiException(
            DiscordErrorKind.NotAllowed,
            options.Value.GuildIdError ?? Languages.Current.GuildIdNotConfigured(DiscordOptions.GuildIdVariable));

    private static void Validate(Action validate, string message)
    {
        try
        {
            validate();
        }
        catch (ArgumentException)
        {
            throw new DiscordApiException(DiscordErrorKind.BadRequest, message);
        }
    }

    private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Relative URL with escaped query values; null values are left out, repeated names stay repeated.</summary>
    private static string Url(string path, params (string Name, string? Value)[] query)
    {
        var parts = query
            .Where(p => p.Value is not null)
            .Select(p => p.Name + "=" + Uri.EscapeDataString(p.Value!));
        var joined = string.Join('&', parts);
        return joined.Length == 0 ? path : path + "?" + joined;
    }

    private static string Route(string url) => url.Split('?')[0];

    private static string? Id(ulong? id) => id?.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Bool(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => null,
    };
}
