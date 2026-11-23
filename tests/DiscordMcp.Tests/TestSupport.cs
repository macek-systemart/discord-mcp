using System.Net;
using System.Text;
using DiscordMcp.Auth;
using DiscordMcp.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DiscordMcp.Tests;

/// <summary>Records every request that reached the network layer and answers from a script.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHandler()
        : this(_ => Json(HttpStatusCode.OK, "{}"))
    {
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "discord-mcp-tests", Guid.NewGuid().ToString("N"));

    public AppPaths Paths => new(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class MemoryTokenStore(StoredToken? token = null) : ITokenStore
{
    public StoredToken? Token { get; private set; } = token;

    public StoredToken? Load() => Token;

    public void Save(StoredToken token) => Token = token;

    public bool Clear()
    {
        var had = Token is not null;
        Token = null;
        return had;
    }
}

/// <summary>The API pipeline as registered in production (guard → governor → auth), over a fake network.</summary>
internal sealed class ApiPipeline : IDisposable
{
    public const string Token = "CANARY-token-value-that-must-never-leak-0123456789";
    public const ulong GuildId = 111111111111111111;

    private readonly TempDir _dir = new();

    public ApiPipeline(FakeHandler network, FakeTimeProvider? time = null, bool allowAccount = false)
    {
        Network = network;
        Time = time ?? new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        State = new StateStore(_dir.Paths);
        Governor = NewGovernor(State);
        Scope = new ApiScope(GuildId, allowAccount);
        var chain = new ApiGuardHandler(Scope)
        {
            InnerHandler = new GovernorHandler(Governor, isCdn: false)
            {
                InnerHandler = new AuthHandler(new TokenHolder(new MemoryTokenStore(new StoredToken(Token, 42, "me"))))
                {
                    InnerHandler = network,
                },
            },
        };
        Client = new HttpClient(chain) { BaseAddress = new Uri(ApiGuard.BaseAddress) };
    }

    public FakeHandler Network { get; }

    public FakeTimeProvider Time { get; }

    public StateStore State { get; }

    public RequestGovernor Governor { get; }

    public ApiScope Scope { get; }

    public HttpClient Client { get; }

    public AppPaths Paths => _dir.Paths;

    public RequestGovernor NewGovernor(StateStore state) =>
        new(Time, Options.Create(new DiscordOptions()), state, NullLogger<RequestGovernor>.Instance);

    public void Dispose()
    {
        Client.Dispose();
        _dir.Dispose();
    }
}
