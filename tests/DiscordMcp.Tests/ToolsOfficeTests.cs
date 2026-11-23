using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DiscordMcp.Discord;
using DiscordMcp.Office;
using DiscordMcp.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace DiscordMcp.Tests;

/// <summary>Parses in this process; the child-process path is covered by <see cref="OfficeChildProcessTests"/>.</summary>
internal sealed class InProcessOfficeReader : IOfficeReader
{
    public int Calls { get; private set; }

    public Task<OfficeExtract?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<OfficeExtract?>(OfficeExtractor.Extract(File.ReadAllBytes(path)));
    }
}

internal sealed class FixedOfficeReader(OfficeExtract? answer) : IOfficeReader
{
    public Task<OfficeExtract?> ReadAsync(string path, CancellationToken cancellationToken) => Task.FromResult(answer);
}

public class ToolsOfficeTests
{
    private const ulong MessageId = 1300000000000000960;
    private const ulong AttachmentId = 800000000000000960;
    private static readonly string Channel = Fixture.TextChannel.ToString(CultureInfo.InvariantCulture);
    private static readonly string CdnPath = $"/attachments/{Fixture.TextChannel}/{AttachmentId}/file";

    private static ToolsHarness WithFile(string fileName, byte[] bytes, Action<DiscordOptions>? configure = null, IOfficeReader? reader = null)
    {
        var api = ScriptedDiscordApi.WithFixtureMessages();
        api.Messages[Fixture.TextChannel].Add(new DiscordMessage
        {
            Id = MessageId,
            ChannelId = Fixture.TextChannel,
            Timestamp = Snowflake.ToTimestamp(MessageId),
            Author = new DiscordUser { Id = ToolsData.Author, Username = "petr" },
            Attachments =
            [
                new DiscordAttachment
                {
                    Id = AttachmentId,
                    Filename = fileName,
                    Size = bytes.Length,
                    Url = $"https://cdn.discordapp.com{CdnPath}?ex=6a000000&is=69f00000&hm=SECRET&",
                },
            ],
        });
        api.Files[CdnPath] = bytes;
        return new ToolsHarness(api, configure, office: reader);
    }

    private static async Task<string> GetAsync(ToolsHarness h, int? offset = null, int? page = null)
    {
        var result = await h.Attachments.GetAttachmentAsync(
            Channel, MessageId.ToString(CultureInfo.InvariantCulture), AttachmentId.ToString(CultureInfo.InvariantCulture), offset, page);
        Assert.Null(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    private static string Fenced(string text)
    {
        var nonce = Regex.Match(text, "<<<attachment-([0-9a-f]{8})\n").Groups[1].Value;
        var start = text.IndexOf($"<<<attachment-{nonce}\n", StringComparison.Ordinal) + 23;
        var end = text.IndexOf($"\nattachment-{nonce}>>>", StringComparison.Ordinal);
        Assert.True(nonce.Length == 8 && end > start, text);
        return text[start..end];
    }

    [Fact]
    public async Task A_word_document_comes_back_as_fenced_text_and_is_kept_on_disk()
    {
        using var h = WithFile("faktura.docx", TestZip.Fixture("faktura.docx"));

        var text = await GetAsync(h);

        Assert.StartsWith(Formatting.MessageFormatter.UntrustedHeader, text, StringComparison.Ordinal);
        Assert.Contains("„faktura.docx“", text, StringComparison.Ordinal);
        Assert.Contains(", dokument Word, text: znaky 0–", text, StringComparison.Ordinal);
        Assert.Contains("\nLámací světýlka | 50 | 4 182,00 Kč\n", Fenced(text), StringComparison.Ordinal);
        Assert.Contains("konec textu", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Uloženo:", text, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(h.Paths.DownloadsDirectory, $"{MessageId}_{AttachmentId}.docx")));
    }

    [Fact]
    public async Task An_excel_workbook_explains_its_row_format()
    {
        using var h = WithFile("faktury.xlsx", TestZip.Fixture("faktury.xlsx"));

        var text = await GetAsync(h);

        Assert.Contains(", sešit Excel, text:", text, StringComparison.Ordinal);
        Assert.Contains("\n2: 2026-11-20 | Alza.cz | 4682 |  | světýlka\n", Fenced(text), StringComparison.Ordinal);
        Assert.Contains("data jako RRRR-MM-DD", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_plain_zip_is_listed_and_the_name_does_not_matter()
    {
        using var h = WithFile("doklady.bin", TestZip.Create(("uctenka.txt", "ahoj")));

        var text = await GetAsync(h);

        Assert.Contains(", archiv ZIP, text:", text, StringComparison.Ordinal);
        Assert.Contains("uctenka.txt (4 B)", Fenced(text), StringComparison.Ordinal);
        Assert.Contains("obsah se nerozbaluje", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entry_names_are_sanitized_like_any_untrusted_text()
    {
        var name = "faktura" + (char)0x202E + "fdp.exe";
        using var h = WithFile("data.zip", TestZip.Create((name, "x")));

        var text = await GetAsync(h);

        Assert.DoesNotContain((char)0x202E, text);
        Assert.Contains("fakturafdp.exe (1 B)", Fenced(text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_office_and_protected_documents_are_only_saved_with_a_reason()
    {
        byte[] ole = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        using var h = WithFile("stara.doc", ole);

        var text = await GetAsync(h);

        Assert.Contains("je starý formát Office (.doc, .xls, .ppt) nebo dokument chráněný heslem", text, StringComparison.Ordinal);
        Assert.Contains("Uloženo:", text, StringComparison.Ordinal);
        Assert.Equal(0, ((InProcessOfficeReader)h.Office).Calls);
    }

    [Fact]
    public async Task A_failed_or_compromised_child_falls_back_to_saving()
    {
        var docx = TestZip.Fixture("faktura.docx");
        foreach (var answer in new OfficeExtract?[] { null, new() { Failed = true }, new() { Kind = (OfficeKind)99, Text = "x" }, new() { Text = null! } })
        {
            using var h = WithFile("faktura.docx", docx, reader: new FixedOfficeReader(answer));

            var text = await GetAsync(h);

            Assert.Contains("se nepodařilo přečíst", text, StringComparison.Ordinal);
            Assert.Contains("Uloženo:", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Paging_reuses_the_saved_document()
    {
        var lines = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"<w:p><w:r><w:t>Řádek {i:000} položka nákupu</w:t></w:r></w:p>"));
        var docx = TestZip.Create(("word/document.xml", $"""<w:document xmlns:w="{TestZip.WordNs}"><w:body>{lines}</w:body></w:document>"""));
        using var h = WithFile("faktura.docx", docx, o => o.MaxOutputChars = 2_000);

        var first = await GetAsync(h);
        var next = Regex.Match(first, @"další část: offset=(\d+)$").Groups[1].Value;
        var second = await GetAsync(h, offset: int.Parse(next, CultureInfo.InvariantCulture));

        Assert.Contains($"text: znaky {next}–", second, StringComparison.Ordinal);
        Assert.Single(h.Api.Downloads, CdnPath);
    }

    [Fact]
    public async Task Page_is_refused_for_documents()
    {
        using var h = WithFile("faktura.docx", TestZip.Fixture("faktura.docx"));

        var e = await Assert.ThrowsAsync<DiscordApiException>(() => GetAsync(h, page: 1));

        Assert.Equal("Parametr page platí jen pro PDF.", e.Message);
    }
}

/// <summary>The real <c>DiscordMcp.exe office-extract</c> child (built next to the tests).</summary>
public class OfficeChildProcessTests
{
    [Fact]
    public async Task The_child_process_reads_a_word_document_and_reports_a_broken_one()
    {
        using var dir = new TempDir();
        var good = Path.Combine(dir.Path, "faktura.docx");
        var bad = Path.Combine(dir.Path, "rozbity.zip");
        await File.WriteAllBytesAsync(good, TestZip.Fixture("faktura.docx"));
        await File.WriteAllBytesAsync(bad, [0x50, 0x4B, 0x03, 0x04, .. Encoding.ASCII.GetBytes("nope")]);
        var reader = new ChildProcessOfficeReader(NullLogger<ChildProcessOfficeReader>.Instance);

        var word = await reader.ReadAsync(good, CancellationToken.None);
        var broken = await reader.ReadAsync(bad, CancellationToken.None);

        Assert.Equal(OfficeKind.Word, word?.Kind);
        Assert.Contains("Lámací světýlka | 50 | 4 182,00 Kč", word!.Text, StringComparison.Ordinal);
        Assert.True(broken?.Failed);
    }
}
