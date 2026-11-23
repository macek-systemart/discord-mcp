using System.Net.Http.Headers;
using DiscordMcp.Http;

namespace DiscordMcp.Tests;

public class GuardTests
{
    private const ulong Guild = 111111111111111111;
    private static readonly ApiScope Server = new(Guild, allowAccountEndpoints: false);
    private static readonly ApiScope Cli = new(Guild, allowAccountEndpoints: true);

    private static HttpRequestMessage Get(string url) => new(HttpMethod.Get, url);

    [Theory]
    [InlineData("guilds/111111111111111111")]
    [InlineData("guilds/111111111111111111/channels")]
    [InlineData("guilds/111111111111111111/messages/search?content=ahoj%20sv%C4%9Bte&limit=25")]
    [InlineData("users/@me/guilds/111111111111111111/member")]
    [InlineData("channels/222222222222222222")]
    [InlineData("channels/222222222222222222/messages?before=1&limit=100")]
    [InlineData("channels/222222222222222222/messages/pins?limit=50")]
    [InlineData("channels/222222222222222222/threads/search?limit=25")]
    public void Api_guard_allows_the_endpoint_table(string relative)
    {
        ApiGuard.Validate(Get(ApiGuard.BaseAddress + relative), Server);
    }

    [Theory]
    [InlineData("users/@me")]
    [InlineData("users/@me/guilds")]
    [InlineData("guilds/111111111111111111/roles")]
    [InlineData("guilds/111111111111111111/threads/active")]
    [InlineData("channels/222222222222222222/threads/archived/public")]
    [InlineData("channels/222222222222222222/threads/archived/private")]
    [InlineData("channels/222222222222222222/users/@me/threads/archived/private")]
    public void Account_and_probe_routes_are_cli_only(string relative)
    {
        ApiGuard.Validate(Get(ApiGuard.BaseAddress + relative), Cli);
        Assert.Throws<RequestGuardException>(() => ApiGuard.Validate(Get(ApiGuard.BaseAddress + relative), Server));
    }

    [Theory]
    [InlineData("users/@me/channels")] // DM list
    [InlineData("users/@me/guilds")] // only for the CLI
    [InlineData("guilds/999999999999999999")] // another guild
    [InlineData("guilds/999999999999999999/channels")]
    [InlineData("guilds/999999999999999999/messages/search")]
    [InlineData("users/@me/guilds/999999999999999999/member")]
    [InlineData("channels/222222222222222222/messages/333333333333333333")] // single message: not usable by user tokens
    [InlineData("channels/abc/messages")]
    [InlineData("channels/0222/messages")]
    [InlineData("channels/222222222222222222/messages/")]
    [InlineData("channels/222222222222222222/typing")]
    [InlineData("guilds/111111111111111111/members")]
    [InlineData("invites/abc")]
    public void Api_guard_refuses_routes_outside_the_table(string relative)
    {
        Assert.Throws<RequestGuardException>(() => ApiGuard.Validate(Get(ApiGuard.BaseAddress + relative), Server));
    }

    [Theory]
    [InlineData("https://discord.com/api/v10/channels/../users/@me/channels")]
    [InlineData("https://discord.com/api/v10/channels/%2e%2e/users/@me/channels")]
    [InlineData("https://discord.com/api/v10/channels/%2E%2E%2Fusers/@me/channels")]
    [InlineData("https://discord.com/api/v10/channels/222222222222222222%2fmessages")]
    [InlineData("https://discord.com/api/v10/channels\\222222222222222222/messages")]
    public void Api_guard_refuses_traversal(string url)
    {
        Assert.Throws<RequestGuardException>(() => ApiGuard.Validate(Get(url), Server));
    }

    [Theory]
    [InlineData("http://discord.com/api/v10/guilds/111111111111111111")]
    [InlineData("https://discord.com:8443/api/v10/guilds/111111111111111111")]
    [InlineData("https://evil.example/api/v10/guilds/111111111111111111")]
    [InlineData("https://discord.com.evil.example/api/v10/guilds/111111111111111111")]
    [InlineData("https://cdn.discordapp.com/api/v10/guilds/111111111111111111")]
    [InlineData("https://user:pw@discord.com/api/v10/guilds/111111111111111111")]
    [InlineData("https://discord.com/api/v9/guilds/111111111111111111")]
    public void Api_guard_refuses_other_hosts_schemes_and_versions(string url)
    {
        Assert.Throws<RequestGuardException>(() => ApiGuard.Validate(Get(url), Server));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    public void Api_guard_refuses_every_method_but_get(string method)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), ApiGuard.BaseAddress + "guilds/111111111111111111");
        Assert.Throws<RequestGuardException>(() => ApiGuard.Validate(request, Server));
    }

    [Fact]
    public void Api_guard_refuses_a_get_with_a_body()
    {
        var request = Get(ApiGuard.BaseAddress + "guilds/111111111111111111");
        request.Content = new StringContent("{}");
        Assert.Throws<RequestGuardException>(() => ApiGuard.Validate(request, Server));
    }

    [Fact]
    public void Api_guard_without_a_guild_allows_no_guild_route()
    {
        var scope = new ApiScope(guildId: null, allowAccountEndpoints: false);
        Assert.Throws<RequestGuardException>(() => ApiGuard.Validate(Get(ApiGuard.BaseAddress + "guilds/111111111111111111"), scope));
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/file.png?ex=1&is=2&hm=3")]
    [InlineData("https://media.discordapp.net/attachments/1/2/file.png?width=100")]
    public void Cdn_guard_allows_attachment_urls(string url)
    {
        CdnGuard.Validate(Get(url));
    }

    [Theory]
    [InlineData("https://discord.com/api/v10/users/@me")]
    [InlineData("https://cdn.discordapp.com/avatars/1/2.png")]
    [InlineData("https://cdn.discordapp.com/attachments/../avatars/1.png")]
    [InlineData("http://cdn.discordapp.com/attachments/1/2/file.png")]
    [InlineData("https://images-ext-1.discordapp.net/external/abc")]
    [InlineData("https://evil.example/attachments/1/2/file.png")]
    public void Cdn_guard_refuses_other_urls(string url)
    {
        Assert.Throws<RequestGuardException>(() => CdnGuard.Validate(Get(url)));
    }

    [Fact]
    public void Cdn_guard_refuses_any_authorization_header()
    {
        var raw = Get("https://cdn.discordapp.com/attachments/1/2/file.png");
        raw.Headers.TryAddWithoutValidation("Authorization", "token");
        Assert.Throws<RequestGuardException>(() => CdnGuard.Validate(raw));

        var typed = Get("https://cdn.discordapp.com/attachments/1/2/file.png");
        typed.Headers.Authorization = new AuthenticationHeaderValue("Bot", "x");
        Assert.Throws<RequestGuardException>(() => CdnGuard.Validate(typed));
    }

    [Fact]
    public void Guard_messages_never_contain_the_query()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://cdn.discordapp.com/attachments/1/2/f.png?ex=1&hm=SECRET");
        var e = Assert.Throws<RequestGuardException>(() => CdnGuard.Validate(request));
        Assert.DoesNotContain("SECRET", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refused_requests_never_reach_the_network_or_carry_the_token()
    {
        var network = new FakeHandler();
        using var pipeline = new ApiPipeline(network);

        await Assert.ThrowsAsync<RequestGuardException>(() => pipeline.Client.GetAsync("users/@me/channels"));
        await Assert.ThrowsAsync<RequestGuardException>(() => pipeline.Client.PostAsync("guilds/111111111111111111", new StringContent("{}")));
        await Assert.ThrowsAsync<RequestGuardException>(() => pipeline.Client.GetAsync("https://cdn.discordapp.com/attachments/1/2/f.png"));

        Assert.Empty(network.Requests);
    }

    [Fact]
    public async Task Allowed_requests_carry_the_raw_token()
    {
        var network = new FakeHandler();
        using var pipeline = new ApiPipeline(network);

        using var _ = await pipeline.Client.GetAsync("guilds/111111111111111111");

        var request = Assert.Single(network.Requests);
        Assert.Equal(ApiPipeline.Token, Assert.Single(request.Headers.GetValues("Authorization")));
    }

    [Fact]
    public void Socket_handler_never_follows_redirects_or_keeps_cookies()
    {
        using var handler = HttpRegistration.CreateSocketHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }
}
