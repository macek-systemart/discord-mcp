using System.Text;
using DiscordMcp.Auth;
using DiscordMcp.Cli;
using DiscordMcp.Discord;
using DiscordMcp.Formatting;
using DiscordMcp.Http;
using DiscordMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DiscordMcp.Tests;

public class FinalCheckTests
{
    [Fact]
    public void Every_code_point_survives_every_sanitizer_without_throwing()
    {
        // Chunks of 4096 consecutive code points (surrogate range as lone UTF-16 units) through Name, Line and Text.
        for (var start = 0; start <= 0x10FFFF; start += 4096)
        {
            var sb = new StringBuilder();
            for (var cp = start; cp < Math.Min(start + 4096, 0x110000); cp++)
            {
                if (cp is >= 0xD800 and <= 0xDFFF)
                {
                    sb.Append((char)cp);
                }
                else
                {
                    sb.Append(char.ConvertFromUtf32(cp));
                }
            }

            var chunk = sb.ToString();
            var name = Sanitizer.Name(chunk, int.MaxValue);
            var line = Sanitizer.Line(chunk, int.MaxValue);
            var text = Sanitizer.Indented(chunk);
            Assert.DoesNotContain(Sanitizer.SelfMarker, name + line + text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/zapis..v2.pdf?ex=1&hm=2")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/...txt")]
    public void Attachment_names_with_dots_pass_the_cdn_guard(string url)
    {
        CdnGuard.Validate(new HttpRequestMessage(HttpMethod.Get, url));
    }

    [Theory]
    [InlineData("https://cdn.discordapp.com/attachments/1/../../avatars/x.png")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/%2e%2e/x.png")]
    [InlineData("https://cdn.discordapp.com/attachments/1/2/a%2fb.png")]
    public void Traversal_segments_still_fail_the_cdn_guard(string url)
    {
        Assert.Throws<RequestGuardException>(() => CdnGuard.Validate(new HttpRequestMessage(HttpMethod.Get, url)));
    }

    [Fact]
    public void A_new_token_is_used_at_once()
    {
        var store = new MemoryTokenStore(new StoredToken("old", 1, "me"));
        var holder = new TokenHolder(store);
        Assert.Equal("old", holder.Current);
        store.Save(new StoredToken("new", 1, "me"));
        Assert.Equal("new", holder.Current);
    }

    [Fact]
    public async Task Selftest_runs_every_tool_and_prints_no_content()
    {
        using var dir = new TempDir();
        var api = ScriptedDiscordApi.WithFixtureMessages();
        foreach (var thread in api.Threads.Threads)
        {
            api.ChannelLookup[thread.Id] = thread;
        }

        var options = new DiscordOptions { GuildId = Fixture.GuildId };
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(options))
            .AddSingleton<TimeProvider>(new FakeTimeProvider(ToolsHarness.Now))
            .AddSingleton(new TokenHolder(new MemoryTokenStore(new StoredToken("CANARY-selftest", Fixture.MyUserId, "tester"))))
            .AddSingleton<IDiscordApi>(api)
            .AddSingleton<ToolRunner>()
            .AddSingleton(dir.Paths)
            .AddLogging()
            .AddDiscordDomain()
            .BuildServiceProvider();
        var output = new StringWriter();

        var exit = await new SelfTest(services, output).RunAsync(CancellationToken.None);

        var text = output.ToString();
        Assert.DoesNotContain("[CHYBA]", text, StringComparison.Ordinal);
        Assert.Equal(0, exit);
        Assert.True(text.Split('\n').Count(l => l.Contains("[OK]", StringComparison.Ordinal)) >= 8, text);
        foreach (var leak in new[] { "Hledaný", "Ahoj", "Souhlasím", "obecny", "Testovací server", "CANARY" })
        {
            Assert.DoesNotContain(leak, text, StringComparison.Ordinal);
        }
    }
}
