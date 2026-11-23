using System.Globalization;
using DiscordMcp.Auth;
using DiscordMcp.Discord;
using DiscordMcp.Formatting;
using DiscordMcp.Office;
using DiscordMcp.Pdf;
using DiscordMcp.Tools;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;

namespace DiscordMcp.Tests;

/// <summary>
/// In-memory <see cref="IDiscordApi"/> over the fixtures. Counts every API call against the current
/// <see cref="ToolScope"/> like the real governor (downloads excepted) and records it in <see cref="Calls"/>.
/// </summary>
internal sealed class ScriptedDiscordApi : IDiscordApi
{
    public DiscordGuild Guild { get; set; } = Fixture.Load<DiscordGuild>("guild.json");

    public List<DiscordChannel> Channels { get; set; } = Fixture.Load<List<DiscordChannel>>("channels.json");

    public DiscordMember Member { get; set; } = Fixture.Load<DiscordMember>("member.json");

    /// <summary>Answers of <c>GET /channels/{id}</c>; anything else is a 404.</summary>
    public Dictionary<ulong, DiscordChannel> ChannelLookup { get; } = [];

    public Dictionary<ulong, List<DiscordMessage>> Messages { get; } = [];

    public PinsResponse Pins { get; set; } = Fixture.Load<PinsResponse>("pins.json");

    public ThreadSearchResponse Threads { get; set; } = Fixture.Load<ThreadSearchResponse>("threads_search.json");

    public Func<SearchQuery, MessageSearchResponse> Search { get; set; } = _ => Fixture.Load<MessageSearchResponse>("search.json");

    /// <summary>Attachment bytes by URL path.</summary>
    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>Thrown by every API call after it was counted.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Awaited by <see cref="GetGuildAsync"/>, to hold a warm-up in flight.</summary>
    public Task? GuildGate { get; set; }

    public List<string> Calls { get; } = [];

    public List<string> Downloads { get; } = [];

    public List<MessageQuery> MessageQueries { get; } = [];

    public List<SearchQuery> SearchQueries { get; } = [];

    public static ScriptedDiscordApi WithFixtureMessages()
    {
        var api = new ScriptedDiscordApi();
        api.Messages[Fixture.TextChannel] = Fixture.Load<List<DiscordMessage>>("messages.json");
        api.Messages[Fixture.StartedThread] = Fixture.Load<List<DiscordMessage>>("thread_messages.json");
        return api;
    }

    public async Task<DiscordGuild> GetGuildAsync(CancellationToken cancellationToken)
    {
        Count("guild");
        if (GuildGate is { } gate)
        {
            await gate.WaitAsync(cancellationToken);
        }

        return Guild;
    }

    public Task<IReadOnlyList<DiscordChannel>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        Count("channels");
        return Task.FromResult<IReadOnlyList<DiscordChannel>>(Channels);
    }

    public Task<DiscordMember> GetMyMemberAsync(CancellationToken cancellationToken)
    {
        Count("member");
        return Task.FromResult(Member);
    }

    public Task<DiscordChannel> GetChannelAsync(ulong channelId, CancellationToken cancellationToken)
    {
        Count($"channel:{channelId}");
        return ChannelLookup.TryGetValue(channelId, out var channel)
            ? Task.FromResult(channel)
            : throw new DiscordApiException(DiscordErrorKind.NotFound, "404");
    }

    public Task<IReadOnlyList<DiscordMessage>> GetMessagesAsync(ulong channelId, MessageQuery query, CancellationToken cancellationToken)
    {
        Count($"messages:{channelId}");
        query.Validate();
        MessageQueries.Add(query);
        var all = Messages.TryGetValue(channelId, out var list) ? list.OrderBy(m => m.Id).ToList() : [];
        IEnumerable<DiscordMessage> page;
        if (query.Before is { } before)
        {
            page = all.Where(m => m.Id < before).TakeLast(query.Limit);
        }
        else if (query.After is { } after)
        {
            page = all.Where(m => m.Id > after).Take(query.Limit);
        }
        else if (query.Around is { } around)
        {
            var index = all.FindIndex(m => m.Id >= around);
            page = all.Skip(Math.Max(0, (index < 0 ? all.Count : index) - query.Limit / 2)).Take(query.Limit);
        }
        else
        {
            page = all.TakeLast(query.Limit);
        }

        return Task.FromResult<IReadOnlyList<DiscordMessage>>(page.OrderByDescending(m => m.Id).ToList());
    }

    public Task<PinsResponse> GetPinsAsync(ulong channelId, DateTimeOffset? before, int limit, CancellationToken cancellationToken)
    {
        Count($"pins:{channelId}");
        return Task.FromResult(Pins);
    }

    public Task<ThreadSearchResponse> SearchThreadsAsync(ulong channelId, ThreadQuery query, CancellationToken cancellationToken)
    {
        Count($"threads:{channelId}");
        query.Validate();
        return Task.FromResult(Threads);
    }

    public Task<MessageSearchResponse> SearchMessagesAsync(SearchQuery query, CancellationToken cancellationToken)
    {
        Count("search");
        query.Validate();
        SearchQueries.Add(query);
        return Task.FromResult(Search(query));
    }

    public Task<DownloadedFile> DownloadAttachmentAsync(Uri url, long maxBytes, CancellationToken cancellationToken)
    {
        Downloads.Add(url.AbsolutePath);
        return Files.TryGetValue(url.AbsolutePath, out var bytes)
            ? Task.FromResult(new DownloadedFile(bytes, "application/octet-stream"))
            : throw new DiscordApiException(DiscordErrorKind.NotFound, "Příloha už není dostupná.");
    }

    private void Count(string call)
    {
        if (ToolScope.Current?.TryCount() == false)
        {
            throw new DiscordApiException(DiscordErrorKind.BudgetExceeded, "Tool budget exceeded.");
        }

        lock (Calls)
        {
            Calls.Add(call);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }
    }
}

/// <summary>The domain services and tools over a <see cref="ScriptedDiscordApi"/>, without MCP.</summary>
internal sealed class ToolsHarness : IDisposable
{
    /// <summary>Before the <c>ex</c> expiry of the fixture CDN URLs (2026-05-10).</summary>
    public static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly TempDir _dir = new();

    public ToolsHarness(ScriptedDiscordApi? api = null, Action<DiscordOptions>? configure = null, bool withToken = true, IPdfReader? pdf = null, IOfficeReader? office = null)
    {
        Api = api ?? ScriptedDiscordApi.WithFixtureMessages();
        Options = new DiscordOptions();
        Options.ApplyGuildId(Fixture.GuildId.ToString(CultureInfo.InvariantCulture));
        configure?.Invoke(Options);
        var options = Microsoft.Extensions.Options.Options.Create(Options);
        Time = new FakeTimeProvider(Now);
        Tokens = new TokenHolder(new MemoryTokenStore(withToken ? new StoredToken("CANARY-tools-token", Fixture.MyUserId, "tester") : null));
        Context = new GuildContext(Api, Tokens, options, Time);
        Resolver = new ChannelResolver(Context, Api, options, Time);
        Formatter = new MessageFormatter(Context, options);
        var runner = new ToolRunner(options, Time);
        Server = new ServerTools(runner, Context, Resolver, Formatter, Api, options);
        Messages = new MessageTools(runner, Context, Resolver, Formatter, Api, options);
        Pdf = pdf ?? new InProcessPdfReader();
        Office = office ?? new InProcessOfficeReader();
        Attachments = new AttachmentTools(runner, Context, Resolver, Api, Paths, options, Time, Pdf, Office);
    }

    public ScriptedDiscordApi Api { get; }

    public DiscordOptions Options { get; }

    public FakeTimeProvider Time { get; }

    public TokenHolder Tokens { get; }

    public GuildContext Context { get; }

    public ChannelResolver Resolver { get; }

    public MessageFormatter Formatter { get; }

    public ServerTools Server { get; }

    public MessageTools Messages { get; }

    public AttachmentTools Attachments { get; }

    public IPdfReader Pdf { get; }

    public IOfficeReader Office { get; }

    public AppPaths Paths => _dir.Paths;

    public Task<GuildSnapshot> GuildAsync() => Context.GetAsync(CancellationToken.None);

    public void Dispose() => _dir.Dispose();
}

internal static class ToolsData
{
    public const ulong Author = 700000000000000002;

    /// <summary>Synthetic message whose id carries <paramref name="at"/> (so ids and times stay consistent).</summary>
    public static DiscordMessage Message(ulong channelId, DateTimeOffset at, string content, int sequence = 0) => new()
    {
        Id = Snowflake.FromTimestamp(at) + (ulong)sequence,
        ChannelId = channelId,
        Content = content,
        Timestamp = at,
        Author = new DiscordUser { Id = Author, Username = "petr", GlobalName = "Petr" },
    };

    public static DiscordChannel Thread(ulong id, ulong parentId, int type = ChannelTypes.PublicThread, ulong guildId = Fixture.GuildId, bool member = false) => new()
    {
        Id = id,
        Type = type,
        GuildId = guildId,
        ParentId = parentId,
        Name = $"vlákno {id}",
        Member = member ? new ThreadMember { Id = id, UserId = Fixture.MyUserId } : null,
    };

    public static string Text(CallToolResult result) => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>Ids of the messages rendered in a tool output (header lines only).</summary>
    public static List<ulong> RenderedIds(string output) =>
        output.Split('\n')
            .Where(line => line.StartsWith('['))
            .Select(line => ulong.Parse(line[(line.IndexOf("] ", StringComparison.Ordinal) + 2)..].Split(' ')[0], CultureInfo.InvariantCulture))
            .ToList();
}
