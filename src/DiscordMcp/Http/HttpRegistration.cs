using System.Net;
using DiscordMcp.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace DiscordMcp.Http;

public static class HttpClientNames
{
    /// <summary>discord.com API, carries the token.</summary>
    public const string Api = "discord-api";

    /// <summary>Attachment CDN, never carries the token.</summary>
    public const string Cdn = "discord-cdn";
}

public static class HttpRegistration
{
    /// <summary>Plain and truthful; no browser or official-client imitation.</summary>
    public const string UserAgent = "discord-mcp/1.0";

    /// <summary>
    /// Registers both named clients with their guards. The MCP server and the CLI verbs share this, so no code path
    /// talks to Discord without the guard and the governor. The caller registers <see cref="ApiScope"/>.
    /// Handler order (outer → inner): guard → governor → auth → socket handler.
    /// </summary>
    public static IServiceCollection AddDiscordHttp(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new AppPaths());
        services.AddSingleton<ITokenStore, DpapiTokenStore>();
        services.AddSingleton<TokenHolder>();
        services.AddSingleton<StateStore>();
        services.AddSingleton<RequestGovernor>();

        services.AddHttpClient(HttpClientNames.Api, client =>
            {
                client.BaseAddress = new Uri(ApiGuard.BaseAddress);
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler(sp => new ApiGuardHandler(sp.GetRequiredService<ApiScope>()))
            .AddHttpMessageHandler(sp => new GovernorHandler(sp.GetRequiredService<RequestGovernor>(), isCdn: false))
            .AddHttpMessageHandler(sp => new AuthHandler(sp.GetRequiredService<TokenHolder>()))
            .ConfigurePrimaryHttpMessageHandler(CreateSocketHandler)
            .RemoveAllLoggers(); // the factory's logger writes failed-request exceptions in full

        services.AddHttpClient(HttpClientNames.Cdn, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(60);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .AddHttpMessageHandler(() => new CdnGuardHandler())
            .AddHttpMessageHandler(sp => new GovernorHandler(sp.GetRequiredService<RequestGovernor>(), isCdn: true))
            .ConfigurePrimaryHttpMessageHandler(CreateSocketHandler)
            .RemoveAllLoggers();

        return services;
    }

    /// <summary>No redirects (they would carry the token elsewhere), no shared cookies.</summary>
    internal static SocketsHttpHandler CreateSocketHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
    };
}
