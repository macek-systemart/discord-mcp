using System.Net;
using DiscordMcp.Auth;
using DiscordMcp.Discord;
using DiscordMcp.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace DiscordMcp.Tests;

/// <summary>
/// The token and a signed CDN URL are planted wherever they could leak (the request, response bodies, timeouts) and
/// must never show up in an exception or in any log line. Runs over the production registration (named clients from
/// IHttpClientFactory with its own logging handlers), uncapped at Trace.
/// </summary>
/// <remarks>
/// A network failure carries a realistic socket message (host and port, never the token or the query) with a marker:
/// the factory's own ClientHandler logs that exception in full at Information level (the server caps the category to
/// Warning), so the marker check covers our exceptions and our own log categories only.
/// </remarks>
public class ApiCanaryTests
{
    private const string Token = ApiHarness.Token;
    private const string Secret = "SECRET";
    private const string SignedUrl = "https://cdn.discordapp.com/attachments/300000000000000001/800000000000000001/graf.png?ex=6a000000&is=69f00000&hm=SECRET";
    private const string Planted = $"{Token} {SignedUrl}";
    private const string RawDetail = "RAW-NETWORK-DETAIL";
    private const ulong ForeignGuild = 999999999999999999;
    private static readonly CancellationToken None = CancellationToken.None;

    public enum Scenario
    {
        ApiGuardRefusal,
        CdnGuardRefusal,
        Unauthorized,
        Forbidden,
        RateLimited,
        ServerError,
        Timeout,
        NetworkError,
        JsonError,
        CdnNotFound,
        CdnNetworkError,
        CdnTimeout,
    }

    [Theory]
    [InlineData(Scenario.ApiGuardRefusal)]
    [InlineData(Scenario.CdnGuardRefusal)]
    [InlineData(Scenario.Unauthorized)]
    [InlineData(Scenario.Forbidden)]
    [InlineData(Scenario.RateLimited)]
    [InlineData(Scenario.ServerError)]
    [InlineData(Scenario.Timeout)]
    [InlineData(Scenario.NetworkError)]
    [InlineData(Scenario.JsonError)]
    [InlineData(Scenario.CdnNotFound)]
    [InlineData(Scenario.CdnNetworkError)]
    [InlineData(Scenario.CdnTimeout)]
    public async Task The_token_and_signed_urls_never_leak(Scenario path)
    {
        using var c = new Container(path);
        var errors = new List<Exception>();

        async Task Attempt(Func<Task> call)
        {
            try
            {
                await ApiHarness.PumpAsync(c.Time, call());
            }
            catch (Exception e)
            {
                errors.Add(e);
            }

            c.Time.Advance(TimeSpan.FromSeconds(2));
        }

        if (path.ToString().StartsWith("Cdn", StringComparison.Ordinal))
        {
            var url = path == Scenario.CdnGuardRefusal ? "https://cdn.discordapp.com/avatars/1/2.png?ex=1&hm=SECRET" : SignedUrl;
            await Attempt(() => c.Api.DownloadAttachmentAsync(new Uri(url), 1024, None));
            await Attempt(() => c.Api.DownloadAttachmentAsync(new Uri(url), 1024, None));
        }
        else
        {
            // Enough calls to also reach the follow-up refusals: hard stop after 401, breaker after 403s, block after 429.
            for (var i = 0; i < 6; i++)
            {
                await Attempt(() => c.Api.GetGuildAsync(None));
            }
        }

        Assert.NotEmpty(errors);
        foreach (var e in errors.Select(e => e.ToString()))
        {
            Assert.False(Leaks(e), e);
            Assert.DoesNotContain(RawDetail, e, StringComparison.Ordinal);
        }

        // Positive controls: the capture works, and the token really was on the wire where a request was sent.
        // A guard refusal happens before any handler that logs (the factory's own loggers are removed).
        if (path is not (Scenario.ApiGuardRefusal or Scenario.CdnGuardRefusal))
        {
            Assert.NotEmpty(c.Logs.Entries);
        }

        foreach (var request in c.ApiNetwork.Requests)
        {
            Assert.Equal(Token, Assert.Single(request.Headers.GetValues("Authorization")));
        }

        foreach (var (category, text) in c.Logs.Entries)
        {
            Assert.False(Leaks(text), $"{category} {text}");
            if (category.StartsWith("DiscordMcp", StringComparison.Ordinal))
            {
                Assert.DoesNotContain(RawDetail, text, StringComparison.Ordinal);
            }
        }
    }

    private static bool Leaks(string text) =>
        text.Contains(Token, StringComparison.Ordinal) || text.Contains(Secret, StringComparison.Ordinal);

    private static HttpResponseMessage Respond(Scenario path) => path switch
    {
        Scenario.Unauthorized => FakeHandler.Json(HttpStatusCode.Unauthorized, $$"""{"message":"401: Unauthorized {{Planted}}","code":0}"""),
        Scenario.Forbidden => FakeHandler.Json(HttpStatusCode.Forbidden, $$"""{"message":"Missing Access {{Planted}}","code":50001}"""),
        Scenario.RateLimited => FakeHandler.Json(HttpStatusCode.TooManyRequests, $$"""{"message":"{{Planted}}","retry_after":120,"global":false}"""),
        Scenario.ServerError => FakeHandler.Json(HttpStatusCode.ServiceUnavailable, $"upstream {Planted}"),
        Scenario.Timeout or Scenario.CdnTimeout => throw new TaskCanceledException($"timeout {Planted}", new TimeoutException(Planted)),
        Scenario.NetworkError or Scenario.CdnNetworkError =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, $"No such host is known. ({RawDetail}:443)"),
        Scenario.JsonError => FakeHandler.Json(HttpStatusCode.OK, $"<html>{Planted}</html>"),
        Scenario.CdnNotFound => FakeHandler.Json(HttpStatusCode.NotFound, $"gone {Planted}"),
        _ => FakeHandler.Json(HttpStatusCode.OK, Fixture.Text("guild.json")),
    };

    /// <summary>The production registration with fakes for the network, the clock, the token store and the state directory.</summary>
    private sealed class Container : IDisposable
    {
        private readonly TempDir _dir = new();
        private readonly ServiceProvider _provider;

        public Container(Scenario path)
        {
            ApiNetwork = new FakeHandler(_ => Respond(path));
            CdnNetwork = new FakeHandler(_ => Respond(path));

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [DiscordOptions.GuildIdVariable] = "111111111111111111" })
                .Build());
            services.AddDiscordOptions();
            services.AddSingleton(new ApiScope(path == Scenario.ApiGuardRefusal ? ForeignGuild : ApiHarness.GuildId, allowAccountEndpoints: false));
            services.AddDiscordHttp();
            services.AddDiscordApi();

            // AddDiscordHttp registers the real token store, state directory and clock: replace all of them.
            services.AddSingleton(_dir.Paths);
            services.AddSingleton<ITokenStore>(new MemoryTokenStore(new StoredToken(Token, 42, "me")));
            services.AddSingleton<TimeProvider>(Time);
            services.AddHttpClient(HttpClientNames.Api).ConfigurePrimaryHttpMessageHandler(() => ApiNetwork);
            services.AddHttpClient(HttpClientNames.Cdn).ConfigurePrimaryHttpMessageHandler(() => CdnNetwork);

            _provider = services.BuildServiceProvider();
            Assert.Equal(_dir.Path, _provider.GetRequiredService<AppPaths>().BaseDirectory);
            Assert.IsType<MemoryTokenStore>(_provider.GetRequiredService<ITokenStore>());
            Assert.Same(Time, _provider.GetRequiredService<TimeProvider>());
            Api = _provider.GetRequiredService<IDiscordApi>();
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));

        public ApiLogCapture Logs { get; } = new();

        public FakeHandler ApiNetwork { get; }

        public FakeHandler CdnNetwork { get; }

        public IDiscordApi Api { get; }

        public void Dispose()
        {
            _provider.Dispose();
            _dir.Dispose();
        }
    }
}
