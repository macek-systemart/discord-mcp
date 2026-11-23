using System.Drawing;
using System.Drawing.Imaging;
using DiscordMcp.Pdf;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace DiscordMcp.Tests;

/// <summary>Parses in this process (fast, deterministic); the child-process path has its own tests.</summary>
internal sealed class InProcessPdfReader : IPdfReader
{
    public List<PdfRequest> Requests { get; } = [];

    public Task<PdfExtract?> ReadAsync(string path, PdfRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult<PdfExtract?>(PdfExtractor.Extract(File.ReadAllBytes(path), request));
    }
}

/// <summary>Returns a fixed answer, as a compromised child process could.</summary>
internal sealed class FixedPdfReader(PdfExtract? answer) : IPdfReader
{
    public Task<PdfExtract?> ReadAsync(string path, PdfRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(answer);
}

/// <summary>Synthetic PDFs built with PdfPig's writer (Latin-1 text only: standard fonts use WinAnsi).</summary>
internal static class TestPdf
{
    /// <summary>
    /// A two-column invoice. All labels are written before all amounts, so content-stream order would separate them;
    /// only layout-based extraction keeps "label … amount" on one line.
    /// </summary>
    public static byte[] Invoice()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddText("Faktura 2026-0042", 16, new PdfPoint(50, 780), font);
        page.AddText("Polozka", 11, new PdfPoint(50, 700), font);
        page.AddText("Lamaci svetylka 50 ks", 11, new PdfPoint(50, 680), font);
        page.AddText("Celkem k uhrade", 11, new PdfPoint(50, 640), font);
        page.AddText("Cena", 11, new PdfPoint(420, 700), font);
        page.AddText("4 682,00 Kc", 11, new PdfPoint(420, 680), font);
        page.AddText("4 682,00 Kc", 11, new PdfPoint(420, 640), font);
        return builder.Build();
    }

    /// <summary>One text page per entry; each entry's lines go top to bottom.</summary>
    public static byte[] Text(params string[][] pages)
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var lines in pages)
        {
            var page = builder.AddPage(PageSize.A4);
            for (var i = 0; i < lines.Length; i++)
            {
                page.AddText(lines[i], 10, new PdfPoint(40, 800 - (i * 14)), font);
            }
        }

        return builder.Build();
    }

    /// <summary>Pages that are one full-page picture each (a scan), optionally rotated, optionally with a PNG picture.</summary>
    public static byte[] Scan(int pages, int width = 1240, int height = 1754, PageRotationDegrees? rotation = null, bool png = false)
    {
        using var builder = new PdfDocumentBuilder();
        for (var i = 1; i <= pages; i++)
        {
            var page = builder.AddPage(PageSize.A4);
            var picture = Picture(width, height, $"Uctenka strana {i}", png ? ImageFormat.Png : ImageFormat.Jpeg);
            var area = new PdfRectangle(0, 0, page.PageSize.Width, page.PageSize.Height);
            if (png)
            {
                page.AddPng(picture, area);
            }
            else
            {
                page.AddJpeg(picture, area);
            }

            if (rotation is { } r)
            {
                page.SetRotation(r);
            }
        }

        return builder.Build();
    }

    /// <summary>A text page with a small logo: not a picture page.</summary>
    public static byte[] TextWithLogo()
    {
        using var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);
        page.AddJpeg(Picture(200, 100, "LOGO", ImageFormat.Jpeg), new PdfRectangle(40, 760, 140, 810));
        page.AddText("Dodaci list c. 17", 12, new PdfPoint(160, 780), font);
        return builder.Build();
    }

    /// <summary>A receipt-like picture: dark text lines on white with some texture so JPEG has work to do.</summary>
    public static byte[] Picture(int width, int height, string label, ImageFormat format)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font(FontFamily.GenericMonospace, Math.Max(8, height / 60f)))
        {
            g.Clear(Color.White);
            var random = new Random(width + height);
            for (var i = 0; i < width * height / 400; i++)
            {
                var gray = random.Next(200, 256);
                g.FillRectangle(new SolidBrush(Color.FromArgb(gray, gray, gray)), random.Next(width), random.Next(height), 3, 3);
            }

            for (var line = 0; line < 40; line++)
            {
                g.DrawString($"{label} radek {line:00}   {random.Next(10, 9999)},{random.Next(10, 99)} Kc", font, Brushes.Black, 20, 20 + (line * height / 42f));
            }
        }

        using var output = new MemoryStream();
        bitmap.Save(output, format);
        return output.ToArray();
    }

    public static (int Width, int Height) Size(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        using var image = Image.FromStream(stream);
        return (image.Width, image.Height);
    }
}
