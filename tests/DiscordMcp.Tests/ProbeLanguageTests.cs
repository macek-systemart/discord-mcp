using DiscordMcp.Cli;
using DiscordMcp.Localization;

namespace DiscordMcp.Tests;

/// <summary>
/// <c>check --probe</c> prints in the active language. A fake network answers every endpoint with an empty object, which
/// walks the probe through its "cannot compute" and "skipped" branches; nothing real is contacted.
/// </summary>
public class ProbeLanguageTests
{
    private static async Task<(int Exit, string Output, int Requests)> RunAsync(string code)
    {
        using var _ = Languages.Use(Languages.Find(code)!);
        var network = new FakeHandler();
        using var client = new HttpClient(network) { BaseAddress = new Uri("https://discord.com/api/v10/") };
        var output = new StringWriter();

        var exit = await new Probe(client, Fixture.GuildId, Fixture.MyUserId, output).RunAsync(CancellationToken.None);

        return (exit, output.ToString(), network.Requests.Count);
    }

    [Fact]
    public async Task The_probe_prints_czech_labels_in_Czech()
    {
        var (exit, output, requests) = await RunAsync("cs");

        Assert.Equal(0, exit);
        Assert.True(requests >= 5);
        Assert.Contains("Sonda serveru 111111111111111111: 1 GET na endpoint", output, StringComparison.Ordinal);
        Assert.Contains("roles=chybí", output, StringComparison.Ordinal);
        Assert.Contains("(přeskočeno: ", output, StringComparison.Ordinal);
        Assert.Contains($"Hotovo: {requests} requestů", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_probe_prints_english_labels_in_English_with_no_czech_left()
    {
        var (exit, output, requests) = await RunAsync("en");

        Assert.Equal(0, exit);
        Assert.Contains("Probe of server 111111111111111111: 1 GET per endpoint", output, StringComparison.Ordinal);
        Assert.Contains("roles=missing", output, StringComparison.Ordinal);
        Assert.Contains("(skipped: ", output, StringComparison.Ordinal);
        Assert.Contains($"Done: {requests} requests", output, StringComparison.Ordinal);
        Assert.False(output.Any("ěščřžýáíéůúňťďĚŠČŘŽÝÁÍÉŮÚŇŤĎ".Contains), output);
    }
}
