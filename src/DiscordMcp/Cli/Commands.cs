using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DiscordMcp.Auth;
using DiscordMcp.Discord;
using DiscordMcp.Http;
using DiscordMcp.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DiscordMcp.Cli;

/// <summary>
/// Setup verbs. The user runs them in a terminal; agents never do. They use the same guarded, paced HTTP clients as
/// the MCP server, with account endpoints allowed. Output is limited to ids, usernames and guild names.
/// </summary>
internal static class Commands
{
    public static int PrintUsage(string? unknownVerb)
    {
        var t = Languages.Current;
        Console.OutputEncoding = Encoding.UTF8;
        if (unknownVerb is not null)
        {
            Console.Error.WriteLine(t.UnknownCommand(Json.Clean(unknownVerb)));
        }

        Console.Error.WriteLine(t.Usage);
        return unknownVerb is null ? 0 : 2;
    }

    public static async Task<int> RunAsync(string verb, string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var scope = new ApiScope(guildId: null, allowAccountEndpoints: true);
        await using var services = BuildServices(scope);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return verb switch
            {
                "set-token" => await SetTokenAsync(services, cts.Token),
                "clear-token" => ClearToken(services),
                "guilds" => await GuildsAsync(services, cts.Token),
                "check" => await CheckAsync(services, scope, args, cts.Token),
                _ => PrintUsage(verb),
            };
        }
        catch (DiscordApiException e)
        {
            Console.Error.WriteLine(e.Message);
            return 1;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine(Languages.Current.NetworkErrorCli);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(Languages.Current.Cancelled);
            return 1;
        }
    }

    private static ServiceProvider BuildServices(ApiScope scope)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b
            .SetMinimumLevel(LogLevel.Warning)
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));
        services.AddDiscordOptions();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddEnvironmentVariables().Build());

        // The guild given to `check` wins over DISCORD_GUILD_ID (registered after AddDiscordOptions, so it runs later).
        services.PostConfigure<DiscordOptions>(o =>
        {
            if (scope.GuildId is { } guild)
            {
                o.GuildId = guild;
                o.GuildIdError = null;
            }
        });
        services.AddSingleton(scope);
        services.AddDiscordHttp();
        services.AddDiscordApi();
        services.AddDiscordDomain();
        return services.BuildServiceProvider();
    }

    private static async Task<int> SetTokenAsync(ServiceProvider services, CancellationToken ct)
    {
        var t = Languages.Current;
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine(t.SetTokenNeedsConsole);
            return 2;
        }

        Console.Error.WriteLine(t.SetTokenHowToFirst);
        Console.Error.WriteLine(t.SetTokenHowToSecond);
        Console.Error.Write(t.SetTokenPrompt);
        var raw = ReadSecret();
        Console.Error.WriteLine(t.TokenRead(raw.Length));

        if (!TryNormalizeToken(raw, out var token, out var error))
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        var governor = services.GetRequiredService<RequestGovernor>();
        governor.ClearHardStop();
        services.GetRequiredService<TokenHolder>().UseCandidate(token);

        var api = services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Api);
        using var response = await api.GetAsync("users/@me", ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The 401 belongs to the candidate, not to a previously stored token: do not leave the latch behind.
            governor.ClearHardStop();
            Console.Error.WriteLine(t.TokenRejected);
            return 1;
        }

        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(t.DiscordReturnedNothingSaved((int)response.StatusCode));
            return 1;
        }

        using var me = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        if (me.RootElement.U64("id") is not { } userId)
        {
            Console.Error.WriteLine(t.UnexpectedResponseNothingSaved);
            return 1;
        }

        var username = Json.Clean(me.RootElement.Str("username"));
        services.GetRequiredService<ITokenStore>().Save(new StoredToken(token, userId, username));
        Console.WriteLine(t.TokenSaved(username, userId));
        return 0;
    }

    internal static bool TryNormalizeToken(string raw, out string token, out string? error)
    {
        token = raw.Trim().Trim('"', '\'', '`').Trim();
        error = null;
        var t = Languages.Current;
        if (token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase) || token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            error = t.TokenNotUserToken;
        }
        else if (token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = t.TokenHasWhitespace;
        }
        else if (token.Length is < 30 or > 200)
        {
            error = t.TokenBadLength;
        }

        return error is null;
    }

    private static string ReadSecret()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                sb.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return sb.ToString();
    }

    private static int ClearToken(ServiceProvider services)
    {
        var removed = services.GetRequiredService<ITokenStore>().Clear();
        Console.WriteLine(removed ? Languages.Current.TokenCleared : Languages.Current.NoTokenWasStored);
        return 0;
    }

    private static async Task<int> GuildsAsync(ServiceProvider services, CancellationToken ct)
    {
        var t = Languages.Current;
        RequireToken(services);
        var api = services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Api);
        using var response = await api.GetAsync("users/@me/guilds", ct);
        if (!response.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(t.DiscordReturned((int)response.StatusCode));
            return 1;
        }

        using var guilds = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct));
        foreach (var guild in guilds.RootElement.EnumerateArray())
        {
            Console.WriteLine($"{guild.U64("id")}  {Json.Clean(guild.Str("name"))}");
        }

        var exe = (Environment.ProcessPath ?? "DiscordMcp.exe").Replace("\\", "\\\\", StringComparison.Ordinal);
        Console.WriteLine();
        Console.WriteLine(t.ConfigSnippetIntro);
        Console.WriteLine($$"""
              "discord": {
                "command": "{{exe}}",
                "env": { "DISCORD_GUILD_ID": "{{t.GuildIdPlaceholder}}" }
              }
            """);
        return 0;
    }

    private static async Task<int> CheckAsync(ServiceProvider services, ApiScope scope, string[] args, CancellationToken ct)
    {
        var t = Languages.Current;
        var stored = RequireToken(services);
        var probe = args.Contains("--probe");
        var selftest = args.Contains("--selftest");
        var rawGuild = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? Environment.GetEnvironmentVariable(DiscordOptions.GuildIdVariable);
        var options = new DiscordOptions();
        options.ApplyGuildId(rawGuild);
        if (options.GuildId is not { } guildId)
        {
            Console.Error.WriteLine(t.CheckUsage(options.GuildIdError ?? ""));
            return 2;
        }

        scope.SetGuild(guildId);
        var api = services.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.Api);
        if (probe)
        {
            return await new Probe(api, guildId, stored.UserId, Console.Out).RunAsync(ct);
        }

        if (selftest)
        {
            return await new SelfTest(services, Console.Out).RunAsync(ct);
        }

        using (var me = await api.GetAsync("users/@me", ct))
        {
            if (!me.IsSuccessStatusCode)
            {
                Console.Error.WriteLine(t.TokenCheckFailed((int)me.StatusCode));
                return 1;
            }

            using var doc = JsonDocument.Parse(await me.Content.ReadAsByteArrayAsync(ct));
            Console.WriteLine(t.TokenOk(Json.Clean(doc.RootElement.Str("username")), doc.RootElement.U64("id") ?? 0));
        }

        using var guild = await api.GetAsync(string.Create(CultureInfo.InvariantCulture, $"guilds/{guildId}"), ct);
        if (!guild.IsSuccessStatusCode)
        {
            Console.Error.WriteLine(t.ServerCheckFailed(guildId, (int)guild.StatusCode));
            return 1;
        }

        using var g = JsonDocument.Parse(await guild.Content.ReadAsByteArrayAsync(ct));
        Console.WriteLine(t.ServerOk(Json.Clean(g.RootElement.Str("name")), guildId));
        return 0;
    }

    private static StoredToken RequireToken(ServiceProvider services) =>
        services.GetRequiredService<TokenHolder>().Stored
        ?? throw new DiscordApiException(DiscordErrorKind.NoToken, Languages.Current.NoStoredToken);
}
