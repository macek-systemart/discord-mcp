using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using DiscordMcp.Tools;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;

namespace DiscordMcp.Pdf;

/// <summary>
/// Reads untrusted PDF bytes: the text of each page rebuilt into lines (so an invoice label stays next to its amount)
/// and, for a page that is mostly one picture (a scan or a photo), that picture as a downscaled JPEG. Runs inside the
/// <c>pdf-extract</c> child process (<see cref="ChildProcessPdfReader"/>), never in the MCP server itself.
/// </summary>
internal static class PdfExtractor
{
    internal const int MaxPages = 300;
    internal const int MaxTextChars = 500_000;
    internal const long MaxImagePixels = 40_000_000;
    internal const int MaxImageSide = 1568;

    /// <summary>A page is a picture page when its largest image covers at least this share of the page.</summary>
    internal const double PicturePageCoverage = 0.25;

    private static readonly long[] Qualities = [85, 70, 55, 40];

    public static PdfExtract Extract(byte[] bytes, PdfRequest request)
    {
        try
        {
            using var document = PdfDocument.Open(bytes, new ParsingOptions
            {
                UseLenientParsing = true,
                SkipMissingFonts = true,
            });
            return Read(document, request);
        }
        catch (PdfDocumentEncryptedException)
        {
            return new PdfExtract { Failure = PdfFailure.Encrypted };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new PdfExtract { Failure = PdfFailure.Unreadable };
        }
    }

    private static PdfExtract Read(PdfDocument document, PdfRequest request)
    {
        var count = document.NumberOfPages;
        if (request.Page is { } wanted && (wanted < 1 || wanted > count))
        {
            return new PdfExtract { Failure = PdfFailure.PageOutOfRange, PageCount = count };
        }

        int[] numbers = request.Page is { } one ? [one] : [.. Enumerable.Range(1, Math.Min(count, MaxPages))];
        var truncated = request.Page is null && count > MaxPages;
        var pages = new List<PdfPageText>();
        var picturePages = new List<int>();
        var chars = 0;
        foreach (var number in numbers)
        {
            if (chars >= MaxTextChars)
            {
                truncated = true;
                break;
            }

            var page = document.GetPage(number);
            var text = PageText(page);
            if (text.Length > MaxTextChars - chars)
            {
                text = text[..(MaxTextChars - chars)];
                truncated = true;
            }

            chars += text.Length;
            pages.Add(new PdfPageText(number, text, LooksGarbled(text)));
            if (LargestPicture(page) is not null)
            {
                picturePages.Add(number);
            }
        }

        var result = new PdfExtract { PageCount = count, Pages = pages, Truncated = truncated, PicturePages = picturePages };
        var picturePage = request.Page ?? picturePages.FirstOrDefault();
        if (request.ImageBytes <= 0 || !picturePages.Contains(picturePage))
        {
            return result;
        }

        var (image, problem) = Picture(document.GetPage(picturePage), picturePage, request.ImageBytes);
        return result with { Image = image, ImageProblem = problem, ImageProblemPage = image is null ? picturePage : 0 };
    }

    /// <summary>
    /// Words grouped into lines by baseline (top to bottom) and ordered left to right; a wide gap becomes three spaces.
    /// Unlike content-stream order this keeps a two-column layout (label … amount) on one line.
    /// </summary>
    internal static string PageText(Page page)
    {
        var lines = new List<List<Word>>();
        var baseline = 0.0;
        var tolerance = 0.0;
        foreach (var word in page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderByDescending(Baseline))
        {
            var y = Baseline(word);
            if (lines.Count == 0 || Math.Abs(y - baseline) > tolerance)
            {
                lines.Add([]);
                baseline = y;
                tolerance = Math.Max(1.0, Math.Abs(word.BoundingBox.Height) * 0.5);
            }

            lines[^1].Add(word);
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            Word? previous = null;
            foreach (var word in line.OrderBy(w => w.BoundingBox.Left))
            {
                if (previous is not null)
                {
                    var gap = word.BoundingBox.Left - previous.BoundingBox.Right;
                    var charWidth = Math.Abs(previous.BoundingBox.Width) / Math.Max(1, previous.Text.Length);
                    sb.Append(gap > 3 * charWidth ? "   " : " ");
                }

                sb.Append(word.Text);
                previous = word;
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    private static double Baseline(Word word) =>
        word.Letters.Count > 0 ? word.Letters[0].StartBaseLine.Y : word.BoundingBox.Bottom;

    /// <summary>
    /// Fonts without a Unicode mapping yield symbols instead of letters: then fewer than 40 % of the visible
    /// characters are letters or digits.
    /// </summary>
    internal static bool LooksGarbled(string text)
    {
        var visible = 0;
        var readable = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            visible++;
            if (Rune.IsLetterOrDigit(rune))
            {
                readable++;
            }
        }

        return visible >= 20 && readable < visible * 0.4;
    }

    private static IPdfImage? LargestPicture(Page page)
    {
        var area = Math.Abs(page.Width * page.Height);
        var largest = page.GetImages().MaxBy(Area);
        return largest is not null && area > 0 && Area(largest) >= PicturePageCoverage * area ? largest : null;
    }

    private static double Area(IPdfImage image) => Math.Abs(image.BoundingBox.Width * image.BoundingBox.Height);

    private static (PdfPageImage? Image, PdfImageProblem Problem) Picture(Page page, int number, int budget)
    {
        var image = LargestPicture(page);
        if (image is null)
        {
            return (null, PdfImageProblem.Unsupported);
        }

        if ((long)image.WidthInSamples * image.HeightInSamples > MaxImagePixels)
        {
            return (null, PdfImageProblem.TooLarge);
        }

        byte[] encoded;
        if (AttachmentTools.SniffImage(image.RawBytes) is "image/jpeg" or "image/png")
        {
            encoded = image.RawBytes.ToArray();
        }
        else if (image.TryGetPng(out var png))
        {
            encoded = png;
        }
        else
        {
            return (null, PdfImageProblem.Unsupported);
        }

        try
        {
            return FitJpeg(encoded, page.Rotation.Value, budget, number) is { } fitted
                ? (fitted, PdfImageProblem.None)
                : (null, PdfImageProblem.TooLarge);
        }
        catch (Exception e) when (e is ArgumentException or ExternalException or OutOfMemoryException)
        {
            // GDI+ reports a broken image as ArgumentException or, historically, OutOfMemoryException.
            return (null, PdfImageProblem.Undecodable);
        }
    }

    /// <summary>
    /// Decodes with GDI+ (Windows codecs, patched by Windows Update), flattens onto white, applies the page rotation,
    /// fits the longer side into <see cref="MaxImageSide"/> and lowers quality, then size, until the JPEG fits the budget.
    /// </summary>
    internal static PdfPageImage? FitJpeg(byte[] encoded, int rotation, int budget, int page)
    {
        using var input = new MemoryStream(encoded, writable: false);
        using var source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        if ((long)source.Width * source.Height > MaxImagePixels)
        {
            return null;
        }

        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        var scale = Math.Min(1.0, (double)MaxImageSide / Math.Max(source.Width, source.Height));
        for (var attempt = 0; attempt < 6; attempt++, scale *= 0.7)
        {
            using var bitmap = Render(source, scale, rotation);
            foreach (var quality in Qualities)
            {
                using var output = new MemoryStream();
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
                bitmap.Save(output, codec, parameters);
                if (output.Length <= budget)
                {
                    return new PdfPageImage(page, output.ToArray(), bitmap.Width, bitmap.Height);
                }
            }
        }

        return null;
    }

    private static Bitmap Render(Image source, double scale, int rotation)
    {
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var attributes = new ImageAttributes())
        {
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.Clear(Color.White);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }

        bitmap.RotateFlip(rotation switch
        {
            90 => RotateFlipType.Rotate90FlipNone,
            180 => RotateFlipType.Rotate180FlipNone,
            270 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone,
        });
        return bitmap;
    }
}
