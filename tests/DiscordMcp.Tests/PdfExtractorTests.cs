using System.Text;
using DiscordMcp.Pdf;
using DiscordMcp.Tools;
using UglyToad.PdfPig.Content;

namespace DiscordMcp.Tests;

public class PdfExtractorTests
{
    private static PdfExtract Extract(byte[] pdf, int? page = null, int imageBytes = 550_000) =>
        PdfExtractor.Extract(pdf, new PdfRequest(page, imageBytes));

    [Fact]
    public void Invoice_label_and_amount_stay_on_one_line()
    {
        var result = Extract(TestPdf.Invoice());

        Assert.Equal(PdfFailure.None, result.Failure);
        Assert.Equal(1, result.PageCount);
        var lines = result.Pages.Single().Text.Split('\n');
        Assert.Equal("Faktura 2026-0042", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("Lamaci svetylka 50 ks", StringComparison.Ordinal) && l.EndsWith("4 682,00 Kc", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Celkem k uhrade   ", StringComparison.Ordinal) && l.EndsWith("4 682,00 Kc", StringComparison.Ordinal));
        Assert.False(result.Pages.Single().Garbled);
        Assert.Empty(result.PicturePages);
        Assert.Null(result.Image);
    }

    [Fact]
    public void A_requested_page_is_read_alone()
    {
        var result = Extract(TestPdf.Text(["jedna"], ["dva"], ["tri"]), page: 2);

        Assert.Equal(3, result.PageCount);
        var page = Assert.Single(result.Pages);
        Assert.Equal(2, page.Number);
        Assert.Equal("dva", page.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void A_page_outside_the_document_is_reported(int page)
    {
        var result = Extract(TestPdf.Text(["jedna"], ["dva"], ["tri"]), page);

        Assert.Equal(PdfFailure.PageOutOfRange, result.Failure);
        Assert.Equal(3, result.PageCount);
    }

    [Theory]
    [InlineData("%PDF-1.7\nthis is not a pdf at all")]
    [InlineData("hello")]
    public void Broken_bytes_are_unreadable_not_an_exception(string content)
    {
        Assert.Equal(PdfFailure.Unreadable, Extract(Encoding.ASCII.GetBytes(content)).Failure);
    }

    [Fact]
    public void A_scanned_page_comes_back_as_a_downscaled_jpeg_within_budget()
    {
        var result = Extract(TestPdf.Scan(1, 2480, 3508));

        Assert.Equal([1], result.PicturePages);
        var image = Assert.IsType<PdfPageImage>(result.Image);
        Assert.Equal(1, image.Page);
        Assert.Equal("image/jpeg", AttachmentTools.SniffImage(image.Jpeg));
        Assert.True(image.Jpeg.Length <= 550_000, $"{image.Jpeg.Length} B");
        Assert.Equal((image.Width, image.Height), TestPdf.Size(image.Jpeg));
        Assert.Equal(PdfExtractor.MaxImageSide, Math.Max(image.Width, image.Height));
        Assert.Equal(PdfImageProblem.None, result.ImageProblem);
    }

    [Fact]
    public void A_tight_budget_lowers_quality_and_size_until_the_jpeg_fits()
    {
        var result = Extract(TestPdf.Scan(1, 2480, 3508), imageBytes: 40_000);

        var image = Assert.IsType<PdfPageImage>(result.Image);
        Assert.True(image.Jpeg.Length <= 40_000, $"{image.Jpeg.Length} B");
        Assert.True(Math.Max(image.Width, image.Height) < PdfExtractor.MaxImageSide);
    }

    [Fact]
    public void Page_rotation_is_applied_to_the_picture()
    {
        var result = Extract(TestPdf.Scan(1, 1000, 1400, new PageRotationDegrees(90)));

        var image = Assert.IsType<PdfPageImage>(result.Image);
        Assert.True(image.Width > image.Height, $"{image.Width}×{image.Height}");
    }

    [Fact]
    public void A_png_picture_is_converted_too()
    {
        var result = Extract(TestPdf.Scan(1, 800, 1100, png: true));

        var image = Assert.IsType<PdfPageImage>(result.Image);
        Assert.Equal("image/jpeg", AttachmentTools.SniffImage(image.Jpeg));
        Assert.Equal((800, 1100), (image.Width, image.Height));
    }

    [Fact]
    public void Without_an_image_budget_no_picture_is_decoded()
    {
        var result = Extract(TestPdf.Scan(2), imageBytes: 0);

        Assert.Equal([1, 2], result.PicturePages);
        Assert.Null(result.Image);
        Assert.Equal(PdfImageProblem.None, result.ImageProblem);
    }

    [Fact]
    public void The_first_picture_page_is_shown_unless_a_page_is_requested()
    {
        var pdf = TestPdf.Scan(3);

        Assert.Equal(1, Extract(pdf).Image?.Page);
        Assert.Equal(3, Extract(pdf, page: 3).Image?.Page);
    }

    [Fact]
    public void A_small_logo_does_not_make_a_picture_page()
    {
        var result = Extract(TestPdf.TextWithLogo());

        Assert.Empty(result.PicturePages);
        Assert.Null(result.Image);
        Assert.Contains("Dodaci list c. 17", result.Pages.Single().Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("!#$%&()*+,-./:;<=>?@[]^_{|}~!#$%&()*+,-./", true)]
    [InlineData("Celkem k uhrade   4 682,00 Kc   DPH 21 %   splatnost 14 dni", false)]
    [InlineData("!#$%", false)] // too short to judge
    public void Symbol_soup_is_flagged_as_garbled(string text, bool garbled)
    {
        Assert.Equal(garbled, PdfExtractor.LooksGarbled(text));
    }
}
