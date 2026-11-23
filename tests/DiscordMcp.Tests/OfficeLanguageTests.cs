using DiscordMcp.Localization;
using DiscordMcp.Office;
using Microsoft.Extensions.Logging.Abstractions;

namespace DiscordMcp.Tests;

/// <summary>The office-extract child writes labels into the text: they follow the language the parent passes.</summary>
public class OfficeLanguageTests
{
    private static readonly Texts English = Languages.Find("en")!;

    private static string[] Lines(byte[] bytes, Texts language) => OfficeExtractor.Extract(bytes, language).Text.Split('\n');

    [Fact]
    public void Word_headers_and_footers_are_labelled_in_the_language_asked_for()
    {
        var english = Lines(TestZip.Fixture("faktura.docx"), English);
        var czech = Lines(TestZip.Fixture("faktura.docx"), Languages.Find("cs")!);

        Assert.Contains("--- header ---", english);
        Assert.Contains("--- footer ---", english);
        Assert.DoesNotContain("--- záhlaví ---", english);
        Assert.Contains("--- záhlaví ---", czech);
        Assert.Contains("--- zápatí ---", czech);
    }

    [Fact]
    public void Slides_sheets_and_tables_are_labelled_in_English()
    {
        Assert.Equal(["--- slide 1 ---", "Rozpočet tábora", "Jídlo 12 000 Kč", "Doprava 4 500 Kč", "", "--- slide 2 ---", "Shrnutí", "Celkem 16 500 Kč"], Lines(TestZip.Fixture("rozpocet.pptx"), English));
        Assert.Contains("--- slide 2 ---", Lines(TestZip.Fixture("rozpocet.odp"), English));
        Assert.Contains("--- sheet Faktury ---", Lines(TestZip.Fixture("faktury.xlsx"), English));
        Assert.Contains("--- table ---", Lines(TestZip.Fixture("faktura.odt"), English));
        Assert.Contains("--- table Faktury ---", Lines(TestZip.Fixture("faktury.ods"), English));
    }

    [Fact]
    public void A_sheet_without_a_name_and_the_entry_list_are_labelled_in_English()
    {
        var xlsx = TestZip.Create(
            ("xl/workbook.xml", $"""<workbook xmlns="{TestZip.SheetNs}"><sheets/></workbook>"""),
            ("xl/worksheets/sheet1.xml", $"""<worksheet xmlns="{TestZip.SheetNs}"><sheetData><row r="3"><c r="B3" t="inlineStr"><is><t>a</t></is></c></row></sheetData></worksheet>"""));
        var zip = TestZip.Create(("a.txt", "ahoj"), ("b/c.jpg", "12345678"));

        Assert.Equal(["--- sheet Sheet1 ---", "3:  | a"], Lines(xlsx, English));
        Assert.Equal(["2 entries:", "a.txt (4 B)", "b/c.jpg (8 B)"], Lines(zip, English));
        Assert.Equal(["1 entry:", "a.txt (4 B)"], Lines(TestZip.Create(("a.txt", "ahoj")), English));
    }

    [Fact]
    public void An_in_process_call_without_a_language_uses_the_active_one()
    {
        using (Languages.Use(English))
        {
            Assert.Contains("--- header ---", Lines(TestZip.Fixture("faktura.docx"), Languages.Current));
            Assert.Contains("--- header ---", OfficeExtractor.Extract(TestZip.Fixture("faktura.docx")).Text.Split('\n'));
        }

        Assert.Contains("--- záhlaví ---", OfficeExtractor.Extract(TestZip.Fixture("faktura.docx")).Text.Split('\n'));
    }

    [Fact]
    public void The_parent_passes_the_active_language_code_to_the_child_as_an_argument()
    {
        Assert.Equal(["office-extract", "C:\\x\\file.docx", "en"], ChildProcessOfficeReader.Arguments("C:\\x\\file.docx", English));
        Assert.Equal(["office-extract", "C:\\x\\file.docx", "cs"], ChildProcessOfficeReader.Arguments("C:\\x\\file.docx", Languages.Find("cs")!));
    }

    [Fact]
    public async Task The_real_child_labels_the_text_in_the_language_the_parent_is_using()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "faktura.docx");
        await File.WriteAllBytesAsync(file, TestZip.Fixture("faktura.docx"));
        var reader = new ChildProcessOfficeReader(NullLogger<ChildProcessOfficeReader>.Instance);

        OfficeExtract? english;
        using (Languages.Use(English))
        {
            english = await reader.ReadAsync(file, CancellationToken.None);
        }

        var czech = await reader.ReadAsync(file, CancellationToken.None); // the test default: Czech

        Assert.Contains("\n--- header ---\n", english!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("--- záhlaví ---", english.Text, StringComparison.Ordinal);
        Assert.Contains("\n--- záhlaví ---\n", czech!.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("--- header ---", czech.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_child_still_reads_without_a_language_or_with_an_unknown_one_and_rejects_extra_arguments()
    {
        // The child's own default depends on the machine, so this only checks that the argument handling does not fail.
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "faktura.docx");
        await File.WriteAllBytesAsync(file, TestZip.Fixture("faktura.docx"));

        var plain = await RunAsync(file);
        var unknown = await RunAsync(file, "xx");
        var extra = await RunAsync(file, "en", "extra");

        Assert.False(plain!.Failed);
        Assert.False(unknown!.Failed);
        Assert.Null(extra);
    }

    private static Task<OfficeExtract?> RunAsync(params string[] arguments) =>
        ChildProcess.RunJsonAsync<OfficeExtract>([OfficeCommand.Verb, .. arguments], OfficeJson.Options, NullLogger.Instance, CancellationToken.None);
}
