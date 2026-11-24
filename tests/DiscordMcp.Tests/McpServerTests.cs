using System.Globalization;
using System.IO.Pipelines;
using System.Text.Json;
using DiscordMcp.Auth;
using DiscordMcp.Discord;
using DiscordMcp.Http;
using DiscordMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DiscordMcp.Tests;

/// <summary>The real tool and filter registration (<see cref="AppHost.WithDiscordTools"/>) in-process over pipes.</summary>
internal sealed class McpTestHost : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly TempDir _dir;

    private McpTestHost(IHost host, McpClient client, TempDir dir)
    {
        _host = host;
        Client = client;
        _dir = dir;
    }

    public McpClient Client { get; }

    public static async Task<McpTestHost> StartAsync(ScriptedDiscordApi api, bool withToken = true, Action<DiscordOptions>? configure = null)
    {
        var dir = new TempDir();
        var options = new DiscordOptions();
        options.ApplyGuildId(Fixture.GuildId.ToString(CultureInfo.InvariantCulture));
        configure?.Invoke(options);

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(ToolsHarness.Now));
        builder.Services.AddSingleton(new TokenHolder(new MemoryTokenStore(withToken ? new StoredToken("CANARY-mcp-token", Fixture.MyUserId, "tester") : null)));
        builder.Services.AddSingleton<IDiscordApi>(api);
        builder.Services.AddSingleton<ToolRunner>();
        builder.Services.AddSingleton(dir.Paths);
        builder.Services.AddDiscordDomain();
        builder.Services
            .AddMcpServer(o => o.ServerInstructions = AppHost.ServerInstructions)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithDiscordTools();

        var host = builder.Build();
        await host.StartAsync();
        var client = await McpClient.CreateAsync(new StreamClientTransport(clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream()));
        return new McpTestHost(host, client, dir);
    }

    public async Task<CallToolResult> CallAsync(string tool, params string[] arguments)
    {
        var args = arguments.Select(a => a.Split('=', 2)).ToDictionary(
            p => p[0],
            p => p[0] switch
            {
                "limit" or "offset" => int.Parse(p[1], CultureInfo.InvariantCulture),
                "archived" => bool.Parse(p[1]),
                _ => (object?)p[1],
            });
        return await Client.CallToolAsync(tool, args);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _dir.Dispose();
    }
}

public class McpServerTests
{
    private const string T = "300000000000000001";
    private const string ForumPost = "1400000000000000004";
    private static readonly string[] WarmUp = ["guild", "channels", "member"];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13];

    private static ScriptedDiscordApi Api()
    {
        var api = ScriptedDiscordApi.WithFixtureMessages();
        api.Files["/attachments/300000000000000001/800000000000000001/graf.png"] = Png;

        var post = ulong.Parse(ForumPost, CultureInfo.InvariantCulture);
        api.ChannelLookup[post] = ToolsData.Thread(post, Fixture.ForumChannel);
        api.Messages[post] =
        [
            new DiscordMessage
            {
                Id = post,
                ChannelId = post,
                Timestamp = Snowflake.ToTimestamp(post),
                Author = new DiscordUser { Id = ToolsData.Author, Username = "petr" },
                Content = "Dotaz ve fóru",
                Attachments = [new DiscordAttachment { Id = 800000000000000077, Filename = "a.png", Size = Png.Length, Url = $"https://cdn.discordapp.com/attachments/{post}/800000000000000077/a.png?ex=6a000000&is=1&hm=SECRET&" }],
            },
        ];
        api.Files[$"/attachments/{post}/800000000000000077/a.png"] = Png;
        return api;
    }

    private static string Text(CallToolResult result) => ToolsData.Text(result);

    [Fact]
    public void The_server_reports_the_build_version_without_the_commit_suffix()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", AppHost.Version);
        Assert.Equal(typeof(AppHost).Assembly.GetName().Version!.ToString(3), AppHost.Version.Split('-')[0]);
    }

    [Fact]
    public async Task Tools_list_names_schemas_and_read_only_annotations()
    {
        await using var host = await McpTestHost.StartAsync(Api());

        var tools = await host.Client.ListToolsAsync();

        Assert.Equal(
            ["get_attachment", "get_message", "list_pins", "list_threads", "read_messages", "search_messages", "server_overview"],
            tools.Select(t => t.Name).Order());
        foreach (var tool in tools.Select(t => t.ProtocolTool))
        {
            Assert.True(tool.Annotations?.ReadOnlyHint);
            Assert.False(tool.Annotations?.DestructiveHint);
            Assert.True(tool.Annotations?.OpenWorldHint);
            Assert.EndsWith(ToolInput.UntrustedNotice, tool.Description, StringComparison.Ordinal);
            foreach (var property in tool.InputSchema.GetProperty("properties").EnumerateObject())
            {
                Assert.False(string.IsNullOrWhiteSpace(property.Value.GetProperty("description").GetString()), $"{tool.Name}.{property.Name}");
            }
        }

        AssertSchema(tools, "server_overview", [], []);
        AssertSchema(tools, "list_threads", ["channel_id", "archived", "offset"], ["channel_id"]);
        AssertSchema(tools, "read_messages", ["channel_id", "before", "after", "around", "limit"], ["channel_id"]);
        AssertSchema(tools, "get_message", ["channel_id", "message_id"], ["channel_id", "message_id"]);
        AssertSchema(tools, "search_messages", ["query", "author_id", "channel_id", "has", "after", "before", "sort", "offset"], []);
        AssertSchema(tools, "list_pins", ["channel_id", "before"], ["channel_id"]);
        AssertSchema(tools, "get_attachment", ["channel_id", "message_id", "attachment_id", "offset", "page"], ["channel_id", "message_id", "attachment_id"]);
    }

    private static void AssertSchema(IList<McpClientTool> tools, string name, string[] properties, string[] required)
    {
        var schema = tools.Single(t => t.Name == name).ProtocolTool.InputSchema;
        Assert.Equal(properties, schema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        var actualRequired = schema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()!).ToArray() : [];
        Assert.Equal(required, actualRequired);
    }

    public static TheoryData<string, string[], string[]> ToolCalls => new()
    {
        { "server_overview", [], [] },
        { "list_threads", ["channel_id=" + T], [$"threads:{T}"] },
        { "read_messages", ["channel_id=" + T, "limit=20"], [$"messages:{T}"] },
        { "get_message", ["channel_id=" + T, "message_id=1300000000000000010"], [$"messages:{T}"] },
        { "search_messages", ["query=Hledaný"], ["search"] },
        { "list_pins", ["channel_id=" + T], [$"pins:{T}"] },
        { "get_attachment", ["channel_id=" + T, "message_id=1300000000000000010", "attachment_id=800000000000000001"], [$"messages:{T}"] },
    };

    [Theory]
    [MemberData(nameof(ToolCalls))]
    public async Task Every_tool_answers_with_exact_call_counts_cold_and_warm(string tool, string[] arguments, string[] warmCalls)
    {
        var api = Api();
        await using var host = await McpTestHost.StartAsync(api);

        var cold = await host.CallAsync(tool, arguments);
        Assert.True(cold.IsError is null or false, Text(cold));
        Assert.StartsWith("⚠ Nedůvěryhodný obsah z Discordu", Text(cold), StringComparison.Ordinal);
        Assert.Equal([.. WarmUp, .. warmCalls], api.Calls);

        api.Calls.Clear();
        var warm = await host.CallAsync(tool, arguments);
        Assert.True(warm.IsError is null or false, Text(warm));
        Assert.Equal(warmCalls, api.Calls);
        Assert.DoesNotContain("SECRET", Text(warm), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_forum_post_costs_one_classification_call_once()
    {
        var api = Api();
        await using var host = await McpTestHost.StartAsync(api);
        string[] args = ["channel_id=" + Fixture.ForumChannel.ToString(CultureInfo.InvariantCulture), "message_id=" + ForumPost];

        var cold = await host.CallAsync("get_message", args);
        Assert.Contains("Dotaz ve fóru", Text(cold), StringComparison.Ordinal);
        Assert.Equal([.. WarmUp, $"channel:{ForumPost}", $"messages:{ForumPost}"], api.Calls);

        api.Calls.Clear();
        await host.CallAsync("get_message", args);
        Assert.Equal([$"messages:{ForumPost}"], api.Calls);
    }

    [Fact]
    public async Task A_cold_warm_up_never_exhausts_a_small_tool_budget()
    {
        var api = Api();
        await using var host = await McpTestHost.StartAsync(api, configure: o => o.MaxApiCallsPerTool = 2);

        var result = await host.CallAsync(
            "get_attachment",
            "channel_id=" + Fixture.ForumChannel.ToString(CultureInfo.InvariantCulture),
            "message_id=" + ForumPost,
            "attachment_id=800000000000000077");

        Assert.True(result.IsError is null or false, Text(result));
        Assert.Single(result.Content.OfType<ImageContentBlock>());
        Assert.Equal(5, api.Calls.Count);
    }

    [Fact]
    public async Task An_unauthorized_token_is_an_error_result_mentioning_set_token()
    {
        var api = Api();
        api.Failure = new DiscordApiException(DiscordErrorKind.Unauthorized, "Discord token odmítl (401): spusť DiscordMcp.exe set-token.");
        await using var host = await McpTestHost.StartAsync(api);

        var result = await host.CallAsync("server_overview");

        Assert.True(result.IsError);
        Assert.Contains("set-token", Text(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_token_is_actionable_and_the_server_keeps_working()
    {
        var api = Api();
        await using var host = await McpTestHost.StartAsync(api, withToken: false);

        var first = await host.CallAsync("read_messages", "channel_id=" + T);
        Assert.True(first.IsError);
        Assert.Contains("DiscordMcp.exe set-token", Text(first), StringComparison.Ordinal);

        Assert.Equal(7, (await host.Client.ListToolsAsync()).Count);
        var second = await host.CallAsync("server_overview");
        Assert.True(second.IsError);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Guard_refusals_and_unexpected_errors_become_fixed_texts()
    {
        var api = Api();
        await using var host = await McpTestHost.StartAsync(api);

        api.Failure = new RequestGuardException("Discord API request blocked (route is not on the allowlist): GET /api/v10/users/@me/channels");
        var guard = await host.CallAsync("server_overview");
        Assert.True(guard.IsError);
        Assert.Equal(ToolErrorFilter.GuardMessage, Text(guard));

        api.Failure = new InvalidOperationException("boom https://cdn.discordapp.com/attachments/1/2/x?ex=1&hm=SECRET CANARY-mcp-token");
        var unexpected = await host.CallAsync("server_overview");
        Assert.True(unexpected.IsError);
        Assert.Equal(ToolErrorFilter.GenericMessage, Text(unexpected));
    }

    [Theory]
    [InlineData("read_messages", "channel_id=abc")]
    [InlineData("read_messages", "channel_id=-5")]
    [InlineData("read_messages", "channel_id=" + T, "before=včera")]
    [InlineData("read_messages", "channel_id=" + T, "before=1300000000000000010", "after=1300000000000000001")]
    [InlineData("get_message", "channel_id=" + T, "message_id=0")]
    [InlineData("search_messages", "sort=random")]
    [InlineData("search_messages", "author_id=@petr")]
    public async Task Invalid_arguments_are_refused_before_any_request(string tool, params string[] arguments)
    {
        var api = Api();
        await using var host = await McpTestHost.StartAsync(api);

        var result = await host.CallAsync(tool, arguments);

        Assert.True(result.IsError);
        Assert.StartsWith("Parametr", Text(result), StringComparison.Ordinal);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Search_has_values_are_validated_and_passed_through()
    {
        var api = Api();
        await using var host = await McpTestHost.StartAsync(api);

        var bad = await host.Client.CallToolAsync("search_messages", new Dictionary<string, object?> { ["has"] = new[] { "image", "exe" } });
        Assert.True(bad.IsError);
        Assert.Empty(api.Calls);

        var good = await host.Client.CallToolAsync("search_messages", new Dictionary<string, object?> { ["has"] = new[] { "image", "-link" }, ["after"] = "2026-11-20" });
        Assert.True(good.IsError is null or false, Text(good));
        var query = Assert.Single(api.SearchQueries);
        Assert.Equal(["image", "-link"], query.Has);
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 23, 0, 0, TimeSpan.Zero), Snowflake.ToTimestamp(query.MinId!.Value));
    }

    [Fact]
    public async Task Overview_lists_visible_channels_and_counts_hidden_ones()
    {
        await using var host = await McpTestHost.StartAsync(Api());

        var text = Text(await host.CallAsync("server_overview"));

        var last = DisplayTime.Format(Snowflake.ToTimestamp(1300000000000000011));
        Assert.Contains("Server „Testovací server“ (111111111111111111)", text, StringComparison.Ordinal);
        Assert.Contains($"📁 Obecné\n  #obecny (300000000000000001) text · poslední aktivita {last} · téma: Hlavní kanál\n", text, StringComparison.Ordinal);
        Assert.Contains("  #oznameni (300000000000000004) oznámení · bez historie\n", text, StringComparison.Ordinal);
        Assert.Contains("  #nsfw (300000000000000008) text · NSFW", text, StringComparison.Ordinal);
        Assert.Contains("#forum (300000000000000003) fórum", text, StringComparison.Ordinal);
        Assert.Contains("Jiné typy (nečitelné)\n  #neznamy-typ (300000000000000007) typ 20\n", text, StringComparison.Ordinal);
        Assert.EndsWith("Skryté kanály: 2 (bez oprávnění, nevypisují se)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("moderatori", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_threads_pages_by_offset_and_marks_private_threads()
    {
        var api = Api();
        await using var host = await McpTestHost.StartAsync(api);

        var text = Text(await host.CallAsync("list_threads", "channel_id=" + T, "offset=0"));

        var last = DisplayTime.Format(Snowflake.ToTimestamp(1300000000000000101));
        Assert.Contains($"🧵 1300000000000000006 „Diskuse k vláknu“ (zpráv: 12, poslední aktivita {last})\n", text, StringComparison.Ordinal);
        Assert.Contains("🧵 1290000000000000001 „Staré archivované“ (archivované, zamčené, zpráv: 40", text, StringComparison.Ordinal);
        Assert.Contains("🧵 1290000000000000002 „Soukromé“ (soukromé, zpráv: 3, poslední aktivita 2026-11-01 11:00 +01:00)\n", text, StringComparison.Ordinal);
        Assert.Contains("celkem 7", text, StringComparison.Ordinal);
        Assert.EndsWith("další: offset=3", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_pins_prints_a_before_cursor_only_when_more_exist()
    {
        var api = Api();
        var pins = Fixture.Load<PinsResponse>("pins.json");
        api.Pins = new PinsResponse { Items = pins.Items, HasMore = true };
        await using var host = await McpTestHost.StartAsync(api);

        var text = Text(await host.CallAsync("list_pins", "channel_id=" + T));

        Assert.Contains("Pravidla serveru\n  📌 připnuto 2026-11-20 13:58 +01:00\n", text, StringComparison.Ordinal);
        Assert.EndsWith("starší připnuté: before=2026-11-20T12:58:00.000Z", text, StringComparison.Ordinal);
        Assert.True(Snowflake.TryParseCursor("2026-11-20T12:58:00.000Z", out _));
    }
}
