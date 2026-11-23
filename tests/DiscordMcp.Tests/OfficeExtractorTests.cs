using System.IO.Compression;
using System.Text;
using DiscordMcp.Office;

namespace DiscordMcp.Tests;

/// <summary>Synthetic ZIP-based files: hand-written parts for edge cases, plus files saved by real Office (Fixtures/office).</summary>
internal static class TestZip
{
    public static byte[] Create(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return output.ToArray();
    }

    /// <summary>One entry made of <paramref name="head"/>, <paramref name="filler"/> repeated, then <paramref name="tail"/>.</summary>
    public static byte[] Bomb(string name, string head, char filler, long count, string tail)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open())
        {
            stream.Write(Encoding.UTF8.GetBytes(head));
            var block = Encoding.ASCII.GetBytes(new string(filler, 1 << 20));
            for (long written = 0; written < count; written += block.Length)
            {
                stream.Write(block);
            }

            stream.Write(Encoding.UTF8.GetBytes(tail));
        }

        return output.ToArray();
    }

    public static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "office", name));

    public const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    public const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
}

public class OfficeExtractorTests
{
    private static string[] Lines(OfficeExtract result) => result.Text.Split('\n');

    [Fact]
    public void Word_keeps_paragraphs_tabs_tables_and_headers_and_shows_a_text_box_once()
    {
        var result = OfficeExtractor.Extract(TestZip.Fixture("faktura.docx"));

        Assert.False(result.Failed);
        Assert.Equal(OfficeKind.Word, result.Kind);
        var lines = Lines(result);
        Assert.Equal("Faktura 2026-0042", lines[0]);
        Assert.Contains("Celkem k úhradě   4 682,00 Kč", lines);
        Assert.Contains("Lámací světýlka | 50 | 4 182,00 Kč", lines);
        Assert.Contains("Doprava | 1 | 500,00 Kč", lines);
        Assert.Single(lines, l => l.Contains("Poznámka v rámečku", StringComparison.Ordinal));
        var header = Array.IndexOf(lines, "--- záhlaví ---");
        var footer = Array.IndexOf(lines, "--- zápatí ---");
        Assert.True(header > 0 && footer > header, result.Text);
        Assert.Equal("Spolek – záhlaví faktury", lines[header + 1]);
        Assert.Equal("IBAN CZ00 0000 0000 0000 0000 0000", lines[footer + 1]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Excel_rows_keep_columns_and_show_dates_and_formula_results()
    {
        var result = OfficeExtractor.Extract(TestZip.Fixture("faktury.xlsx"));

        Assert.Equal(OfficeKind.Excel, result.Kind);
        Assert.Equal(
            ["--- list Faktury ---", "1: Datum | Dodavatel | Částka |  | Poznámka", "2: 2026-11-20 | Alza.cz | 4682 |  | světýlka",
             "3: 2026-11-21 | Albert | 1234.5", "4:  | Celkem | 5916.5", "", "--- list Souhrn ---", "1: Počet dokladů | 2"],
            Lines(result));
    }

    [Fact]
    public void Excel_numbers_lose_float_tails_and_durations_stay_numbers()
    {
        var result = OfficeExtractor.Extract(TestZip.Fixture("cisla.xlsx"));

        Assert.Equal(
            ["--- list Cisla ---", "1: S DPH | 5665.22", "2: Soucet | 0.3", "3: Sazba | 0.21", "4: Odpracovano | 1.5625",
             "5: Cas | 13:30:00", "6: Kdy | 2026-11-23 18:00"],
            Lines(result));
    }

    [Fact]
    public void PowerPoint_and_impress_slides_come_in_order()
    {
        string[] expected = ["--- snímek 1 ---", "Rozpočet tábora", "Jídlo 12 000 Kč", "Doprava 4 500 Kč", "", "--- snímek 2 ---", "Shrnutí", "Celkem 16 500 Kč"];

        Assert.Equal(expected, Lines(OfficeExtractor.Extract(TestZip.Fixture("rozpocet.pptx"))));
        Assert.Equal(expected, Lines(OfficeExtractor.Extract(TestZip.Fixture("rozpocet.odp"))));
        Assert.Equal(OfficeKind.OpenDocumentPresentation, OfficeExtractor.Extract(TestZip.Fixture("rozpocet.odp")).Kind);
    }

    [Fact]
    public void OpenDocument_text_and_spreadsheet_are_read()
    {
        var text = OfficeExtractor.Extract(TestZip.Fixture("faktura.odt"));
        var sheet = OfficeExtractor.Extract(TestZip.Fixture("faktury.ods"));

        Assert.Equal(OfficeKind.OpenDocumentText, text.Kind);
        Assert.Contains("Celkem k úhradě   4 682,00 Kč", Lines(text));
        Assert.Contains("Lámací světýlka | 50 | 4 182,00 Kč", Lines(text));
        Assert.Contains("--- tabulka ---", Lines(text));
        Assert.Equal(OfficeKind.OpenDocumentSpreadsheet, sheet.Kind);
        Assert.Contains("--- tabulka Faktury ---", Lines(sheet));
        Assert.Contains("2: 20.11.2026 | Alza.cz | 4,682.00 Kč |  | světýlka", Lines(sheet));
        Assert.Contains("1: Počet dokladů | 2", Lines(sheet));
    }

    [Fact]
    public void Shared_strings_join_rich_text_runs_and_skip_phonetics()
    {
        var xlsx = TestZip.Create(
            ("xl/workbook.xml", $"""<workbook xmlns="{TestZip.SheetNs}"><sheets/></workbook>"""),
            ("xl/sharedStrings.xml", $"""<sst xmlns="{TestZip.SheetNs}"><si><r><t xml:space="preserve">Ahoj </t></r><r><t>světe</t></r><rPh><t>XX</t></rPh></si><si><t>druhý</t></si></sst>"""),
            ("xl/worksheets/sheet1.xml", $"""<worksheet xmlns="{TestZip.SheetNs}"><sheetData><row r="3"><c r="B3" t="s"><v>0</v></c><c r="D3" t="s"><v>1</v></c><c r="E3" t="inlineStr"><is><t>vložený</t></is></c><c r="F3" t="b"><v>1</v></c><c r="G3" t="s"><v>99</v></c></row></sheetData></worksheet>"""));

        var result = OfficeExtractor.Extract(xlsx);

        Assert.Equal(["--- list List1 ---", "3:  | Ahoj světe |  | druhý | vložený | TRUE"], Lines(result));
    }

    [Fact]
    public void A_doctype_is_refused_so_no_entity_expands()
    {
        var docx = TestZip.Create(("word/document.xml",
            $"""<?xml version="1.0"?><!DOCTYPE d [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]><w:document xmlns:w="{TestZip.WordNs}"><w:body><w:p><w:r><w:t>&b;</w:t></w:r></w:p></w:body></w:document>"""));

        var result = OfficeExtractor.Extract(docx);

        Assert.True(result.Failed);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void Inflated_bytes_are_capped_whatever_the_entry_claims()
    {
        var docx = TestZip.Bomb("word/document.xml",
            $"""<w:document xmlns:w="{TestZip.WordNs}"><w:body><w:p><w:r><w:t>začátek</w:t></w:r></w:p><w:x a=" """,
            'a', OfficeExtractor.MaxEntryBytes + (8 << 20), "\"/></w:body></w:document>");

        var result = OfficeExtractor.Extract(docx);

        Assert.False(result.Failed);
        Assert.Equal("začátek", result.Text);
        Assert.True(result.Truncated);
    }

    [Fact]
    public void Text_is_capped()
    {
        var paragraph = "<w:p><w:r><w:t>" + new string('x', 999) + "</w:t></w:r></w:p>";
        var body = string.Concat(Enumerable.Repeat(paragraph, (OfficeExtractor.MaxTextChars / 1000) + 50));
        var docx = TestZip.Create(("word/document.xml", $"""<w:document xmlns:w="{TestZip.WordNs}"><w:body>{body}</w:body></w:document>"""));

        var result = OfficeExtractor.Extract(docx);

        Assert.True(result.Truncated);
        Assert.True(result.Text.Length <= OfficeExtractor.MaxTextChars);
    }

    [Fact]
    public void OpenDocument_repeats_are_capped()
    {
        const string table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        const string text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        var content = $"""
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:table="{table}" xmlns:text="{text}"><office:body><office:spreadsheet>
            <table:table table:name="List1">
            <table:table-row table:number-rows-repeated="2000000000"><table:table-cell table:number-columns-repeated="16384"/></table:table-row>
            <table:table-row><table:table-cell><text:p>a<text:s text:c="1000000"/>b</text:p></table:table-cell><table:table-cell table:number-columns-repeated="16384"/><table:table-cell><text:p>za mezerou</text:p></table:table-cell></table:table-row>
            <table:table-row table:number-rows-repeated="1000"><table:table-cell table:number-columns-repeated="1000"><text:p>x</text:p></table:table-cell></table:table-row>
            </table:table></office:spreadsheet></office:body></office:document-content>
            """;
        var ods = TestZip.Create(("mimetype", "application/vnd.oasis.opendocument.spreadsheet"), ("content.xml", content));

        var result = OfficeExtractor.Extract(ods);

        Assert.Equal(OfficeKind.OpenDocumentSpreadsheet, result.Kind);
        var lines = Lines(result);
        Assert.Equal("--- tabulka List1 ---", lines[0]);
        Assert.StartsWith("1048577: a" + new string(' ', OfficeExtractor.MaxSpaces) + "b", lines[1], StringComparison.Ordinal);
        Assert.Equal(1 + 1 + OfficeExtractor.MaxRepeat, lines.Length);
        Assert.All(lines.Skip(2), l => Assert.Equal(OfficeExtractor.MaxRepeat, l.Split(" | ").Length));
    }

    [Fact]
    public void Any_other_zip_is_listed_not_unpacked()
    {
        var zip = TestZip.Create(("faktura.txt", "ahoj"), ("slozka/sken.jpg", "12345678"));

        var result = OfficeExtractor.Extract(zip);

        Assert.Equal(OfficeKind.Zip, result.Kind);
        Assert.Equal(["2 položek:", "faktura.txt (4 B)", "slozka/sken.jpg (8 B)"], Lines(result));
    }

    [Fact]
    public void A_broken_zip_fails_as_data()
    {
        var bytes = Encoding.ASCII.GetBytes("PK\x03\x04 this is not a zip archive at all");

        Assert.True(OfficeExtractor.Extract(bytes).Failed);
    }
}
