using System.Net;
using System.Net.Http.Headers;
using DiscordMcp.Discord;
using DiscordMcp.Http;
using DiscordMcp.Tools;

namespace DiscordMcp.Tests;

public class ApiTests
{
    private const ulong Text = Fixture.TextChannel;
    private const string SignedUrl = "https://cdn.discordapp.com/attachments/300000000000000001/800000000000000001/graf.png?ex=6a000000&is=69f00000&hm=deadbeef&";
    private static readonly CancellationToken None = CancellationToken.None;

    private static HttpResponseMessage Accepted() => FakeHandler.Json(HttpStatusCode.Accepted, Fixture.Text("search_202.json"));

    private static string Sent(ApiHarness h, int index = 0) => h.Network.Requests[index].RequestUri!.PathAndQuery;

    // ---- URL and query building ---------------------------------------------------------------------------------

    [Fact]
    public async Task Guild_endpoints_use_the_configured_guild_and_deserialize()
    {
        using var h = new ApiHarness(r => ApiHarness.Ok(Fixture.Text(r.RequestUri!.AbsolutePath switch
        {
            ApiHarness.Guild => "guild.json",
            ApiHarness.Guild + "/channels" => "channels.json",
            _ => "member.json",
        })));

        var guild = await h.PumpAsync(h.Api.GetGuildAsync(None));
        var channels = await h.PumpAsync(h.Api.GetChannelsAsync(None));
        var member = await h.PumpAsync(h.Api.GetMyMemberAsync(None));

        Assert.Equal("Testovací server", guild.Name);
        Assert.Contains(channels, c => c.Id == Fixture.TextChannel);
        Assert.Equal([500000000000000001UL], member.Roles);
        Assert.Equal(
            ["/api/v10/guilds/111111111111111111", "/api/v10/guilds/111111111111111111/channels", "/api/v10/users/@me/guilds/111111111111111111/member"],
            h.Network.Requests.Select(r => r.RequestUri!.PathAndQuery));
    }

    [Fact]
    public async Task Message_pages_send_the_limit_and_at_most_one_anchor()
    {
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("messages.json")));

        var page = await h.PumpAsync(h.Api.GetMessagesAsync(Text, new MessageQuery(), None));
        await h.PumpAsync(h.Api.GetMessagesAsync(Text, new MessageQuery(Before: 1300000000000000010, Limit: 100), None));
        await h.PumpAsync(h.Api.GetMessagesAsync(Text, new MessageQuery(After: 1300000000000000010, Limit: 1), None));
        await h.PumpAsync(h.Api.GetMessagesAsync(Text, new MessageQuery(Around: 1300000000000000010, Limit: 5), None));

        Assert.NotEmpty(page);
        Assert.Equal(
            [
                "/api/v10/channels/300000000000000001/messages?limit=50",
                "/api/v10/channels/300000000000000001/messages?limit=100&before=1300000000000000010",
                "/api/v10/channels/300000000000000001/messages?limit=1&after=1300000000000000010",
                "/api/v10/channels/300000000000000001/messages?limit=5&around=1300000000000000010",
            ],
            h.Network.Requests.Select(r => r.RequestUri!.PathAndQuery));
    }

    [Fact]
    public async Task Pins_send_an_iso_before_cursor()
    {
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("pins.json")));

        var pins = await h.PumpAsync(h.Api.GetPinsAsync(Text, new DateTimeOffset(2026, 11, 20, 13, 58, 0, TimeSpan.FromHours(1)), 50, None));
        await h.PumpAsync(h.Api.GetPinsAsync(Text, before: null, 10, None));

        Assert.Single(pins.Items);
        Assert.Equal("/api/v10/channels/300000000000000001/messages/pins?before=2026-11-20T12%3A58%3A00.0000000%2B00%3A00&limit=50", Sent(h, 0));
        Assert.Equal("/api/v10/channels/300000000000000001/messages/pins?limit=10", Sent(h, 1));
    }

    [Fact]
    public async Task Thread_search_sends_archived_limit_and_offset()
    {
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("threads_search.json")));

        var threads = await h.PumpAsync(h.Api.SearchThreadsAsync(Fixture.ForumChannel, new ThreadQuery(Archived: true, Offset: 25), None));
        await h.PumpAsync(h.Api.SearchThreadsAsync(Text, new ThreadQuery(), None));

        Assert.NotEmpty(threads.Threads);
        Assert.Equal("/api/v10/channels/300000000000000003/threads/search?archived=true&limit=25&offset=25", Sent(h, 0));
        Assert.Equal("/api/v10/channels/300000000000000001/threads/search?limit=25&offset=0", Sent(h, 1));
    }

    [Fact]
    public async Task Search_escapes_czech_text_and_repeats_has()
    {
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("search.json")));
        var query = new SearchQuery
        {
            Content = "ahoj světe & co?=",
            AuthorId = 700000000000000002,
            ChannelId = Text,
            Has = ["image", "-link"],
            MinId = 1300000000000000001,
            MaxId = 1300000000000000300,
            Pinned = false,
            SortBy = "relevance",
            SortOrder = "asc",
            Offset = 25,
            Limit = 10,
        };

        var result = await h.PumpAsync(h.Api.SearchMessagesAsync(query, None));

        Assert.Equal(2, result.TotalResults);
        Assert.Equal(
            "/api/v10/guilds/111111111111111111/messages/search?content=ahoj%20sv%C4%9Bte%20%26%20co%3F%3D"
            + "&author_id=700000000000000002&channel_id=300000000000000001&has=image&has=-link"
            + "&min_id=1300000000000000001&max_id=1300000000000000300&pinned=false&sort_by=relevance&sort_order=asc"
            + "&offset=25&limit=10&include_nsfw=true",
            Sent(h));
    }

    [Fact]
    public async Task Search_without_filters_is_the_guild_activity_feed()
    {
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("search.json")));

        await h.PumpAsync(h.Api.SearchMessagesAsync(new SearchQuery(), None));

        Assert.Equal("/api/v10/guilds/111111111111111111/messages/search?sort_by=timestamp&sort_order=desc&offset=0&limit=25&include_nsfw=true", Sent(h));
    }

    [Fact]
    public async Task Search_text_may_contain_a_link()
    {
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("search.json")));

        await h.PumpAsync(h.Api.SearchMessagesAsync(new SearchQuery { Content = "https://example.com/a/b" }, None));

        Assert.StartsWith("/api/v10/guilds/111111111111111111/messages/search?content=https%3A%2F%2Fexample.com%2Fa%2Fb&", Sent(h), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("čekej...", "content=%C4%8Dekej...&")]
    [InlineData(@"C:\cesta", "content=C%3A%5Ccesta&")]
    [InlineData("../users/@me/channels", "content=..%2Fusers%2F%40me%2Fchannels&")]
    public async Task Dots_and_slashes_in_search_text_stay_in_the_query(string content, string expected)
    {
        // The guard checks traversal in the path only; escaped query values cannot change the route.
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("search.json")));

        await h.PumpAsync(h.Api.SearchMessagesAsync(new SearchQuery { Content = content }, None));

        var sent = Assert.Single(h.Network.Requests).RequestUri!;
        Assert.Equal("/api/v10/guilds/111111111111111111/messages/search", sent.AbsolutePath);
        Assert.Contains(expected, sent.PathAndQuery, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_queries_are_refused_before_sending_with_fixed_czech_text()
    {
        using var h = new ApiHarness();
        Func<Task>[] calls =
        [
            () => h.Api.GetMessagesAsync(Text, new MessageQuery(Before: 1, After: 2), None),
            () => h.Api.GetMessagesAsync(Text, new MessageQuery(Limit: 0), None),
            () => h.Api.SearchThreadsAsync(Text, new ThreadQuery(Offset: -1), None),
            () => h.Api.SearchMessagesAsync(new SearchQuery { Limit = 26 }, None),
            () => h.Api.SearchMessagesAsync(new SearchQuery { Has = ["exe"] }, None),
            () => h.Api.GetPinsAsync(Text, null, 51, None),
        ];

        foreach (var call in calls)
        {
            var e = await Assert.ThrowsAsync<DiscordApiException>(call);
            Assert.Equal(DiscordErrorKind.BadRequest, e.Kind);
            Assert.StartsWith("Neplatné parametry", e.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Parameter", e.Message, StringComparison.Ordinal);
        }

        Assert.Empty(h.Network.Requests);
    }

    [Fact]
    public async Task Without_a_guild_every_call_is_refused_without_sending()
    {
        using var h = new ApiHarness(options: new DiscordOptions { GuildId = null, GuildIdError = "Chybí DISCORD_GUILD_ID." });
        Func<Task>[] calls =
        [
            () => h.Api.GetGuildAsync(None),
            () => h.Api.GetChannelsAsync(None),
            () => h.Api.GetMyMemberAsync(None),
            () => h.Api.GetChannelAsync(Text, None),
            () => h.Api.GetMessagesAsync(Text, new MessageQuery(), None),
            () => h.Api.GetPinsAsync(Text, null, 50, None),
            () => h.Api.SearchThreadsAsync(Text, new ThreadQuery(), None),
            () => h.Api.SearchMessagesAsync(new SearchQuery(), None),
        ];

        foreach (var call in calls)
        {
            var e = await Assert.ThrowsAsync<DiscordApiException>(call);
            Assert.Equal(DiscordErrorKind.NotAllowed, e.Kind);
            Assert.Equal("Chybí DISCORD_GUILD_ID.", e.Message);
        }

        Assert.Empty(h.Network.Requests);
    }

    // ---- Status mapping ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DiscordErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, DiscordErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.NotFound, DiscordErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, DiscordErrorKind.BadRequest)]
    [InlineData(HttpStatusCode.InternalServerError, DiscordErrorKind.Upstream)]
    [InlineData(HttpStatusCode.BadGateway, DiscordErrorKind.Upstream)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DiscordErrorKind.Upstream)]
    [InlineData(HttpStatusCode.Conflict, DiscordErrorKind.Upstream)]
    [InlineData(HttpStatusCode.Found, DiscordErrorKind.Upstream)]
    public async Task Statuses_map_to_error_kinds(HttpStatusCode status, DiscordErrorKind kind)
    {
        using var h = new ApiHarness(_ => FakeHandler.Json(status, """{"message":"x","code":0}"""));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetChannelAsync(Text, None));

        Assert.Equal(kind, e.Kind);
        Assert.Single(h.Network.Requests);
    }

    [Fact]
    public async Task A_401_points_to_set_token()
    {
        using var h = new ApiHarness(_ => FakeHandler.Json(HttpStatusCode.Unauthorized, """{"message":"401: Unauthorized","code":0}"""));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None));

        Assert.Equal(DiscordErrorKind.Unauthorized, e.Kind);
        Assert.Contains("set-token", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_400_shows_discords_message_without_control_characters_and_capped()
    {
        var message = "Invalid\u0000 Form\u001b[31m Body\r\n" + new string('x', 400);
        using var h = new ApiHarness(_ => FakeHandler.Json(HttpStatusCode.BadRequest, System.Text.Json.JsonSerializer.Serialize(new { message, code = 50035 })));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.SearchMessagesAsync(new SearchQuery(), None));

        Assert.Equal(DiscordErrorKind.BadRequest, e.Kind);
        Assert.StartsWith("Discord dotaz odmítl (400): Invalid Form[31m Body", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(e.Message, char.IsControl);
        Assert.True(e.Message.Length <= "Discord dotaz odmítl (400): ".Length + 200);
    }

    [Fact]
    public async Task Network_errors_timeouts_and_bad_json_are_upstream_errors()
    {
        Func<HttpRequestMessage, HttpResponseMessage>[] failures =
        [
            _ => throw new HttpRequestException("RAW-DETAIL network"),
            _ => throw new TaskCanceledException("RAW-DETAIL timeout", new TimeoutException("RAW-DETAIL")),
            _ => ApiHarness.Ok("RAW-DETAIL not json"),
            _ => ApiHarness.Ok("null"),
            _ => ApiHarness.Ok("[1, 2]"),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        ];

        foreach (var failure in failures)
        {
            using var h = new ApiHarness(failure);
            var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None));
            Assert.Equal(DiscordErrorKind.Upstream, e.Kind);
            Assert.DoesNotContain("RAW-DETAIL", e.Message, StringComparison.Ordinal);
            Assert.Null(e.InnerException);
        }
    }

    [Fact]
    public async Task Caller_cancellation_propagates_unchanged()
    {
        using var h = new ApiHarness();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Api.GetGuildAsync(cts.Token));
        Assert.Empty(h.Network.Requests);
    }

    [Fact]
    public async Task Guard_refusals_propagate_unchanged()
    {
        using var h = new ApiHarness();

        await Assert.ThrowsAsync<RequestGuardException>(() => h.Api.DownloadAttachmentAsync(new Uri("https://cdn.discordapp.com/avatars/1/2.png"), 1024, None));
        Assert.Empty(h.Cdn.Requests);
    }

    // ---- 429 ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_short_429_is_waited_out_and_retried_once()
    {
        using var h = new ApiHarness(ApiHarness.Steps(() => ApiHarness.RateLimited(1.5), () => ApiHarness.Ok(Fixture.Text("guild.json"))));

        var guild = await h.PumpAsync(h.Api.GetGuildAsync(None));

        Assert.Equal("Testovací server", guild.Name);
        Assert.Equal(2, h.Network.Requests.Count);
        Assert.True(h.SentAt[1] - h.SentAt[0] >= TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public async Task A_second_429_is_not_retried()
    {
        using var h = new ApiHarness(ApiHarness.Steps(() => ApiHarness.RateLimited(1), () => ApiHarness.RateLimited(1), () => ApiHarness.Ok("{}")));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.GetGuildAsync(None)));

        Assert.Equal(DiscordErrorKind.RateLimited, e.Kind);
        Assert.NotNull(e.RetryAt);
        Assert.Equal(2, h.Network.Requests.Count);
    }

    [Fact]
    public async Task A_429_retry_that_would_pass_the_tool_deadline_fails_fast()
    {
        using var h = new ApiHarness(ApiHarness.Steps(() => ApiHarness.RateLimited(3), () => ApiHarness.Ok("{}")));
        using var scope = ToolScope.Begin("read_messages", 5, h.Time.GetUtcNow() + TimeSpan.FromSeconds(2));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(DiscordErrorKind.RateLimited, e.Kind);
        Assert.Single(h.Network.Requests);
    }

    // ---- Search 202 --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_202_without_a_retry_after_header_is_retried_after_the_body_hint()
    {
        using var h = new ApiHarness(ApiHarness.Steps(Accepted, () => ApiHarness.Ok(Fixture.Text("search.json"))));

        var result = await h.PumpAsync(h.Api.SearchMessagesAsync(new SearchQuery { Content = "výraz" }, None));

        Assert.Equal(2, result.TotalResults);
        Assert.Equal(2, h.Network.Requests.Count);
        Assert.True(h.SentAt[1] - h.SentAt[0] >= TimeSpan.FromSeconds(2)); // body retry_after: 2
    }

    [Fact]
    public async Task Two_202s_mean_the_index_is_not_ready_never_an_empty_result()
    {
        using var h = new ApiHarness(_ => Accepted());

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.SearchMessagesAsync(new SearchQuery(), None)));

        Assert.Equal(DiscordErrorKind.IndexNotReady, e.Kind);
        Assert.Contains("indexuje", e.Message, StringComparison.Ordinal);
        Assert.Equal(2, h.Network.Requests.Count);
        h.Governor.ThrowIfStopped(); // not a breaker event
        Assert.Empty(h.State.Read(s => s.RecentInvalid.ToList()));
    }

    [Fact]
    public async Task A_202_without_any_hint_is_retried_after_the_pacing_interval()
    {
        using var h = new ApiHarness(ApiHarness.Steps(() => new HttpResponseMessage(HttpStatusCode.Accepted), () => ApiHarness.Ok(Fixture.Text("threads_search.json"))));

        var threads = await h.PumpAsync(h.Api.SearchThreadsAsync(Fixture.ForumChannel, new ThreadQuery(), None));

        Assert.NotEmpty(threads.Threads);
        Assert.True(h.SentAt[1] - h.SentAt[0] >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_200_carrying_code_110000_is_treated_as_index_not_ready()
    {
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("search_202.json")));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.SearchThreadsAsync(Text, new ThreadQuery(), None)));

        Assert.Equal(DiscordErrorKind.IndexNotReady, e.Kind);
        Assert.Equal(2, h.Network.Requests.Count);
    }

    [Fact]
    public async Task A_202_whose_wait_would_pass_the_deadline_fails_fast()
    {
        using var h = new ApiHarness(_ => FakeHandler.Json(HttpStatusCode.Accepted, """{"code":110000,"retry_after":30}"""));
        using var scope = ToolScope.Begin("search_messages", 5, h.Time.GetUtcNow() + TimeSpan.FromSeconds(20));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.Api.SearchMessagesAsync(new SearchQuery(), None).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(DiscordErrorKind.IndexNotReady, e.Kind);
        Assert.Single(h.Network.Requests);
    }

    // ---- Attachments -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_download_returns_bytes_and_content_type_and_never_carries_the_token()
    {
        using var h = new ApiHarness(cdn: _ =>
        {
            var content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var file = await h.PumpAsync(h.Api.DownloadAttachmentAsync(new Uri(SignedUrl), 1024, None));

        Assert.Equal([0x89, 0x50, 0x4E, 0x47], file.Bytes);
        Assert.Equal("image/png", file.ContentType);
        var request = Assert.Single(h.Cdn.Requests);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.Empty(h.Network.Requests);
        Assert.Empty(h.State.Read(s => s.RecentCalls.ToList())); // CDN is not an API call
    }

    [Fact]
    public async Task A_download_is_cut_off_at_the_cap_even_when_content_length_lies()
    {
        var body = new ApiEndlessStream();
        using var h = new ApiHarness(cdn: _ =>
        {
            var content = new StreamContent(body);
            content.Headers.ContentLength = 100;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        const long cap = 1024 * 1024;

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.DownloadAttachmentAsync(new Uri(SignedUrl), cap, None)));

        Assert.Equal(DiscordErrorKind.BadRequest, e.Kind);
        Assert.Contains("větší než 1 MB", e.Message, StringComparison.Ordinal);
        Assert.InRange(body.Served, cap, cap + (2 * 81920));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_expired_attachment_is_not_found(HttpStatusCode status)
    {
        using var h = new ApiHarness(cdn: _ => new HttpResponseMessage(status));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => h.PumpAsync(h.Api.DownloadAttachmentAsync(new Uri(SignedUrl), 1024, None)));

        Assert.Equal(DiscordErrorKind.NotFound, e.Kind);
        Assert.Contains("už není dostupná", e.Message, StringComparison.Ordinal);
        h.Governor.ThrowIfStopped(); // CDN refusals never feed the breaker
        Assert.Empty(h.State.Read(s => s.RecentInvalid.ToList()));
    }

    [Fact]
    public async Task Downloads_share_the_pacing_gate_but_not_the_budgets()
    {
        using var h = new ApiHarness(_ => ApiHarness.Ok(Fixture.Text("guild.json")), options: new DiscordOptions { GuildId = ApiHarness.GuildId, MaxApiCallsPer10Minutes = 1 });
        using var scope = ToolScope.Begin("get_attachment", 1, h.Time.GetUtcNow() + TimeSpan.FromMinutes(1));
        await h.Api.GetGuildAsync(None).WaitAsync(TimeSpan.FromSeconds(5));

        var download = h.Api.DownloadAttachmentAsync(new Uri(SignedUrl), 1024, None);
        Assert.False(download.IsCompleted); // waits for the next 1-second slot
        var file = await h.PumpAsync(download);

        Assert.Equal(3, file.Bytes.Length);
        Assert.Equal(1, scope.Calls);
        Assert.Single(h.Cdn.Requests);
    }
}
