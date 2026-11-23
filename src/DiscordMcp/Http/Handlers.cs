using DiscordMcp.Auth;
using DiscordMcp.Discord;
using DiscordMcp.Localization;

namespace DiscordMcp.Http;

public sealed class ApiGuardHandler(ApiScope scope) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ApiGuard.Validate(request, scope);
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class CdnGuardHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CdnGuard.Validate(request);
        return base.SendAsync(request, cancellationToken);
    }
}

/// <summary>Stateless; pacing, stop latches and budgets live in the singleton <see cref="RequestGovernor"/>.</summary>
public sealed class GovernorHandler(RequestGovernor governor, bool isCdn) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await governor.WaitTurnAsync(isCdn, cancellationToken).ConfigureAwait(false);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await governor.ObserveAsync(response, isCdn, cancellationToken).ConfigureAwait(false);
        return response;
    }
}

/// <summary>Adds the user token (raw, no "Bot " prefix). Runs after the guard, so only allowlisted requests carry it.</summary>
public sealed class AuthHandler(TokenHolder tokens) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = tokens.Current ?? throw new DiscordApiException(
            DiscordErrorKind.NoToken,
            Languages.Current.NoToken);
        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation("Authorization", token);
        return base.SendAsync(request, cancellationToken);
    }
}
