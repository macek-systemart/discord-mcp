using System.Collections.Concurrent;
using System.Net;
using DiscordMcp.Auth;
using DiscordMcp.Discord;
using DiscordMcp.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace DiscordMcp.Tests;

/// <summary>
/// <see cref="DiscordApi"/> over the production handler chains (API: guard → governor → auth, CDN: guard → governor)
/// and two fake networks. Options are used as given (no Normalize), so tests can pick small budgets.
/// </summary>
internal sealed class ApiHarness : IDisposable
{
    public const string Token = ApiPipeline.Token;
    public const ulong GuildId = ApiPipeline.GuildId;
    public const string Guild = "/api/v10/guilds/111111111111111111";

    private readonly TempDir _dir = new();

    public ApiHarness(
        Func<HttpRequestMessage, HttpResponseMessage>? api = null,
        Func<HttpRequestMessage, HttpResponseMessage>? cdn = null,
        DiscordOptions? options = null)
    {
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero));
        Network = new FakeHandler(Timed(api ?? (_ => FakeHandler.Json(HttpStatusCode.OK, "{}"))));
        Cdn = new FakeHandler(Timed(cdn ?? (_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) })));
        Settings = options ?? new DiscordOptions { GuildId = GuildId };
        State = new StateStore(_dir.Paths);
        Governor = NewGovernor(State);
        var apiChain = new ApiGuardHandler(new ApiScope(GuildId, allowAccountEndpoints: false))
        {
            InnerHandler = new GovernorHandler(Governor, isCdn: false)
            {
                InnerHandler = new AuthHandler(new TokenHolder(new MemoryTokenStore(new StoredToken(Token, 42, "me"))))
                {
                    InnerHandler = Network,
                },
            },
        };
        var cdnChain = new CdnGuardHandler { InnerHandler = new GovernorHandler(Governor, isCdn: true) { InnerHandler = Cdn } };
        Api = new DiscordApi(new ApiClientFactory(apiChain, cdnChain), MsOptions.Create(Settings), Governor, Time, NullLogger<DiscordApi>.Instance);
    }

    public FakeTimeProvider Time { get; }

    public FakeHandler Network { get; }

    public FakeHandler Cdn { get; }

    /// <summary>Fake time at which each API request reached the network.</summary>
    public List<DateTimeOffset> SentAt { get; } = [];

    public DiscordOptions Settings { get; }

    public StateStore State { get; }

    public RequestGovernor Governor { get; }

    public DiscordApi Api { get; }

    public AppPaths Paths => _dir.Paths;

    public RequestGovernor NewGovernor(StateStore state) =>
        new(Time, MsOptions.Create(Settings), state, NullLogger<RequestGovernor>.Instance);

    /// <summary>Answers the n-th request with the n-th step (the last step repeats).</summary>
    public static Func<HttpRequestMessage, HttpResponseMessage> Steps(params Func<HttpResponseMessage>[] steps)
    {
        var next = 0;
        return _ => steps[Math.Min(Interlocked.Increment(ref next) - 1, steps.Length - 1)]();
    }

    public static HttpResponseMessage Ok(string json) => FakeHandler.Json(HttpStatusCode.OK, json);

    public static HttpResponseMessage RateLimited(double retryAfter, string? scope = null)
    {
        var response = FakeHandler.Json(
            HttpStatusCode.TooManyRequests,
            $$"""{"message":"You are being rate limited.","retry_after":{{retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"global":false}""");
        if (scope is not null)
        {
            response.Headers.Add("X-RateLimit-Scope", scope);
        }

        return response;
    }

    public static HttpResponseMessage Forbidden() => FakeHandler.Json(HttpStatusCode.Forbidden, """{"message":"Missing Access","code":50001}""");

    /// <summary>Advances fake time in small steps until the task completes (continuations may register timers later).</summary>
    public static async Task PumpAsync(FakeTimeProvider time, Task task)
    {
        for (var i = 0; i < 3000 && !task.IsCompleted; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(100));
            await Task.Delay(1);
        }

        await task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task PumpAsync(Task task) => PumpAsync(Time, task);

    public async Task<T> PumpAsync<T>(Task<T> task)
    {
        await PumpAsync(Time, task);
        return await task;
    }

    public void Dispose() => _dir.Dispose();

    private Func<HttpRequestMessage, HttpResponseMessage> Timed(Func<HttpRequestMessage, HttpResponseMessage> respond) => request =>
    {
        lock (SentAt)
        {
            SentAt.Add(Time.GetUtcNow());
        }

        return respond(request);
    };
}

/// <summary>Hands out clients over fixed handler chains, like the named clients of the real factory.</summary>
internal sealed class ApiClientFactory(HttpMessageHandler api, HttpMessageHandler cdn) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => name switch
    {
        HttpClientNames.Api => new HttpClient(api, disposeHandler: false) { BaseAddress = new Uri(ApiGuard.BaseAddress) },
        HttpClientNames.Cdn => new HttpClient(cdn, disposeHandler: false),
        _ => throw new ArgumentException("unknown client", nameof(name)),
    };
}

/// <summary>Captures every log line, scope and logged exception (full ToString) at every level.</summary>
internal sealed class ApiLogCapture : ILoggerProvider
{
    public ConcurrentQueue<(string Category, string Text)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(ApiLogCapture owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            owner.Entries.Enqueue((category, $"scope: {state}"));
            return null;
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, $"{logLevel}: {formatter(state, exception)} {exception}"));
    }
}

/// <summary>A response body that never ends, to prove a download is cut off instead of read to the end.</summary>
internal sealed class ApiEndlessStream : Stream
{
    public long Served { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Array.Fill(buffer, (byte)'x', offset, count);
        Served += count;
        return count;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
