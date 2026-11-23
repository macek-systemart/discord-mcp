using System.Globalization;
using System.Text.RegularExpressions;
using DiscordMcp.Discord;

namespace DiscordMcp.Tests;

/// <summary>Cursor walks with a small output limit: every message exactly once, in both directions.</summary>
public class FormatterPagingTests
{
    private const int Total = 300;
    private static readonly string Channel = Fixture.TextChannel.ToString(CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private static List<DiscordMessage> Messages(int count) =>
        Enumerable.Range(0, count)
            .Select(i => ToolsData.Message(
                Fixture.TextChannel,
                Start.AddMinutes(i * 7),
                // Every 97th message alone exceeds the output limit: it must still be shown (cut) so paging moves on.
                $"Zpráva {i} " + new string('x', i % 97 == 0 ? 3000 : i % 40)))
            .ToList();

    private static ToolsHarness Harness(ScriptedDiscordApi api) => new(api, o => o.MaxOutputChars = 2000);

    [Fact]
    public async Task Walking_before_reaches_the_start_with_every_message_once()
    {
        var api = new ScriptedDiscordApi();
        var all = Messages(Total);
        api.Messages[Fixture.TextChannel] = all;
        using var h = Harness(api);

        var seen = new List<ulong>();
        string? before = null;
        var pages = 0;
        while (true)
        {
            Assert.True(++pages < 200, "paging does not terminate");
            var text = await h.Messages.ReadMessagesAsync(Channel, before: before);
            Assert.True(text.Length <= h.Options.MaxOutputChars, $"page {pages} has {text.Length} chars");
            var ids = ToolsData.RenderedIds(text);
            Assert.NotEmpty(ids);
            seen.AddRange(ids);

            var cursor = Regex.Match(text, @"starší: before=(\d+)");
            if (!cursor.Success)
            {
                Assert.Contains("starší: žádné (začátek kanálu)", text, StringComparison.Ordinal);
                break;
            }

            before = cursor.Groups[1].Value;
        }

        Assert.True(pages > 10);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(all.Select(m => m.Id).Order(), seen.Order());
    }

    [Fact]
    public async Task Walking_after_reaches_the_newest_with_every_message_once()
    {
        var api = new ScriptedDiscordApi();
        var all = Messages(Total);
        api.Messages[Fixture.TextChannel] = all;
        using var h = Harness(api);

        var seen = new List<ulong>();
        var after = "1";
        var pages = 0;
        while (true)
        {
            Assert.True(++pages < 200, "paging does not terminate");
            var text = await h.Messages.ReadMessagesAsync(Channel, after: after);
            var ids = ToolsData.RenderedIds(text);
            Assert.NotEmpty(ids);
            Assert.Equal(ids.Order(), ids);
            seen.AddRange(ids);

            var cursor = Regex.Match(text, @"novější: after=(\d+)");
            if (!cursor.Success)
            {
                Assert.Contains("novější: zatím žádné", text, StringComparison.Ordinal);
                break;
            }

            after = cursor.Groups[1].Value;
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(all.Select(m => m.Id).Order(), seen.Order());
    }

    [Fact]
    public async Task Around_keeps_the_target_in_the_middle_with_both_cursors()
    {
        var api = new ScriptedDiscordApi();
        var all = Messages(Total);
        api.Messages[Fixture.TextChannel] = all;
        using var h = Harness(api);
        var target = all[150].Id;

        var text = await h.Messages.ReadMessagesAsync(Channel, around: target.ToString(CultureInfo.InvariantCulture));

        var ids = ToolsData.RenderedIds(text);
        Assert.Contains(target, ids);
        Assert.True(ids.Count < 50);
        Assert.True(ids.First() < target && ids.Last() > target);
        Assert.Contains($"starší: before={ids.First()}", text, StringComparison.Ordinal);
        Assert.Contains($"novější: after={ids.Last()}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_offsets_walk_every_hit_once()
    {
        const int hits = 120;
        var all = Messages(hits);
        var newestFirst = all.OrderByDescending(m => m.Id).ToList();
        var api = new ScriptedDiscordApi
        {
            Search = q => new MessageSearchResponse
            {
                TotalResults = hits,
                Messages = newestFirst.Skip(q.Offset).Take(q.Limit).Select(m => new List<DiscordMessage> { m }).ToList(),
            },
        };
        using var h = Harness(api);

        var seen = new List<ulong>();
        var offset = 0;
        var pages = 0;
        while (true)
        {
            Assert.True(++pages < 200, "paging does not terminate");
            var text = await h.Messages.SearchMessagesAsync(query: "zpráva", offset: offset);
            Assert.True(text.Length <= h.Options.MaxOutputChars, $"page {pages} has {text.Length} chars");
            var ids = ToolsData.RenderedIds(text);
            Assert.NotEmpty(ids);
            seen.AddRange(ids);

            var next = Regex.Match(text, @"další: offset=(\d+)");
            if (!next.Success)
            {
                Assert.Contains("konec výsledků", text, StringComparison.Ordinal);
                break;
            }

            offset = int.Parse(next.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        Assert.True(pages > 5);
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(all.Select(m => m.Id).Order(), seen.Order());
        Assert.All(api.SearchQueries, q => Assert.Equal(("timestamp", "desc"), (q.SortBy, q.SortOrder)));
    }

    [Fact]
    public async Task Search_shows_channel_and_thread_labels()
    {
        using var h = new ToolsHarness();

        var text = await h.Messages.SearchMessagesAsync(query: "Hledaný");

        Assert.Contains("── 🧵 „Diskuse k vláknu“ (1300000000000000006) v #obecny\n", text, StringComparison.Ordinal);
        Assert.Contains("── #obecny (300000000000000001)\n", text, StringComparison.Ordinal);
        Assert.EndsWith("zobrazeno 2 (pozice 1–2 z 2) · konec výsledků", text, StringComparison.Ordinal);
        Assert.NotNull(h.Context.FindThread(Fixture.StartedThread));
    }
}
