using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using DiscordMcp.Localization;

namespace DiscordMcp.Office;

/// <summary>
/// Text from ZIP-based files using only the BCL: Word, Excel and PowerPoint (OOXML), OpenDocument text, spreadsheet
/// and presentation, and for any other ZIP the list of its entries. Runs inside the <c>office-extract</c> child process.
/// Parts are streamed through <see cref="XmlReader"/> (no DTD, no resolver) with caps on the bytes actually inflated,
/// never on declared sizes; nothing is written to disk.
/// </summary>
internal static partial class OfficeExtractor
{
    internal const int MaxTextChars = 500_000;
    internal const long MaxEntryBytes = 64L * 1024 * 1024;
    internal const long MaxTotalBytes = 256L * 1024 * 1024;
    internal const int MaxListedEntries = 500;
    internal const int MaxParts = 200;
    internal const int MaxRepeat = 50;
    internal const int MaxColumns = 200;
    internal const int MaxSpaces = 100;

    /// <summary>Extracts in the active language (<see cref="Languages.Current"/>): tests and in-process callers.</summary>
    public static OfficeExtract Extract(byte[] bytes) => Extract(bytes, Languages.Current);

    /// <summary>
    /// Extracts with the section labels (header, footer, slide, sheet, table, entry count) in <paramref name="texts"/>.
    /// The child process gets its language from the parent explicitly (see <see cref="OfficeCommand"/>).
    /// </summary>
    public static OfficeExtract Extract(byte[] bytes, Texts texts)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var reader = new Reader(zip, texts);
            var kind = reader.Detect();
            reader.Read(kind);
            var text = Tidy(reader.Text.ToString());
            return text.Length == 0 && reader.Broken
                ? new OfficeExtract { Failed = true, Kind = kind }
                : new OfficeExtract { Kind = kind, Text = text, Truncated = reader.Truncated || reader.Broken };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return new OfficeExtract { Failed = true };
        }
    }

    /// <summary>No trailing spaces, at most one empty line in a row.</summary>
    private static string Tidy(string text) =>
        BlankLines().Replace(string.Join('\n', text.Split('\n').Select(line => line.TrimEnd())), "\n\n").Trim();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();

    [GeneratedRegex(@"^ppt/slides/slide(\d{1,6})\.xml$")]
    private static partial Regex SlidePart();

    [GeneratedRegex(@"^xl/worksheets/sheet(\d{1,6})\.xml$")]
    private static partial Regex SheetPart();

    [GeneratedRegex(@"^word/(header|footer)(\d{1,6})\.xml$")]
    private static partial Regex HeaderFooterPart();

    [GeneratedRegex(@"\[(h+|m+|s+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex ElapsedTime();

    private enum DateKind
    {
        Date,
        Time,
        DateTime,
    }

    private sealed class Reader(ZipArchive zip, Texts texts)
    {
        private const string RelationshipsNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string OdfText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        private const string OdfTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";

        private static readonly XmlReaderSettings Settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };

        /// <summary>Alternate content (text boxes appear twice), tab-stop lists, moved-away and deleted text, phonetics.</summary>
        private static readonly HashSet<string> OoxmlSkipped = ["Fallback", "tabs", "tabLst", "moveFrom", "delText", "instrText", "rPh"];

        private static readonly HashSet<string> OdfSkipped = ["annotation", "tracked-changes"];

        private long _inflated;

        public StringBuilder Text { get; } = new();

        public bool Truncated { get; private set; }

        /// <summary>A part was malformed or cut by a cap.</summary>
        public bool Broken { get; private set; }

        private bool Full => Text.Length >= MaxTextChars;

        public OfficeKind Detect()
        {
            if (zip.GetEntry("word/document.xml") is not null)
            {
                return OfficeKind.Word;
            }

            if (zip.GetEntry("xl/workbook.xml") is not null)
            {
                return OfficeKind.Excel;
            }

            if (zip.GetEntry("ppt/presentation.xml") is not null)
            {
                return OfficeKind.PowerPoint;
            }

            return (zip.GetEntry("mimetype") is { } mimetype ? ReadSmall(mimetype) : "") switch
            {
                "application/vnd.oasis.opendocument.text" => OfficeKind.OpenDocumentText,
                "application/vnd.oasis.opendocument.spreadsheet" => OfficeKind.OpenDocumentSpreadsheet,
                "application/vnd.oasis.opendocument.presentation" => OfficeKind.OpenDocumentPresentation,
                _ => OfficeKind.Zip,
            };
        }

        public void Read(OfficeKind kind)
        {
            switch (kind)
            {
                case OfficeKind.Word:
                    Parse(zip.GetEntry("word/document.xml"), Ooxml);
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var (entry, isHeader) in zip.Entries
                        .Select(e => (Entry: e, Match: HeaderFooterPart().Match(e.FullName)))
                        .Where(p => p.Match.Success)
                        .OrderBy(p => p.Match.Groups[1].Value == "header" ? 0 : 1)
                        .ThenBy(p => int.Parse(p.Match.Groups[2].Value, CultureInfo.InvariantCulture))
                        .Take(MaxParts)
                        .Select(p => (p.Entry, p.Match.Groups[1].Value == "header")))
                    {
                        Section(Marker(isHeader ? texts.HeaderLabel : texts.FooterLabel), entry, Ooxml, seen);
                    }

                    break;
                case OfficeKind.Excel:
                    Excel();
                    break;
                case OfficeKind.PowerPoint:
                    foreach (var (entry, number) in zip.Entries
                        .Select(e => (Entry: e, Match: SlidePart().Match(e.FullName)))
                        .Where(p => p.Match.Success)
                        .Select(p => (p.Entry, Number: int.Parse(p.Match.Groups[1].Value, CultureInfo.InvariantCulture)))
                        .OrderBy(p => p.Number)
                        .Take(MaxParts))
                    {
                        Section(Marker(texts.SlideLabel(number)), entry, Ooxml, seen: null);
                    }

                    break;
                case OfficeKind.Zip:
                    List();
                    break;
                default:
                    Parse(zip.GetEntry("content.xml"), x => Odf(x, numberRows: kind == OfficeKind.OpenDocumentSpreadsheet));
                    break;
            }
        }

        private void List()
        {
            var entries = zip.Entries;
            Add(texts.EntryList(entries.Count) + "\n");
            foreach (var entry in entries.Take(MaxListedEntries))
            {
                Add($"{entry.FullName} ({entry.Length} B)\n");
            }

            if (entries.Count > MaxListedEntries)
            {
                Truncated = true;
            }
        }

        /// <summary>A section label on its own line: <c>--- label ---</c>.</summary>
        private static string Marker(string label) => $"\n--- {label} ---\n";

        /// <summary>A labelled part; dropped when it has no text or repeats one already shown (Word has up to three headers).</summary>
        private void Section(string label, ZipArchiveEntry entry, Action<XmlReader> body, HashSet<string>? seen)
        {
            var mark = Text.Length;
            Add(label);
            var start = Text.Length;
            Parse(entry, body);
            var added = Text.ToString(start, Text.Length - start).Trim();
            if (added.Length == 0 || (seen is not null && !seen.Add(added)))
            {
                Text.Length = mark;
            }
        }

        private void Parse(ZipArchiveEntry? entry, Action<XmlReader> body)
        {
            if (entry is null)
            {
                return;
            }

            if (Full || _inflated >= MaxTotalBytes)
            {
                Truncated = true;
                return;
            }

            try
            {
                using var stream = new CappedStream(entry.Open(), Math.Min(MaxEntryBytes, MaxTotalBytes - _inflated), this);
                using var xml = XmlReader.Create(stream, Settings);
                body(xml);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Malformed, unsupported or cut by a cap: keep what was read so far.
                Broken = true;
            }
        }

        private string ReadSmall(ZipArchiveEntry entry)
        {
            try
            {
                using var stream = new CappedStream(entry.Open(), 200, this);
                var buffer = new byte[200];
                var length = 0;
                int read;
                while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
                {
                    length += read;
                }

                return Encoding.ASCII.GetString(buffer, 0, length).Trim();
            }
            catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
            {
                return "";
            }
        }

        /// <summary>Every node except the subtrees of <paramref name="skipped"/> elements.</summary>
        private static IEnumerable<XmlReader> Walk(XmlReader x, HashSet<string> skipped)
        {
            var more = x.Read();
            while (more)
            {
                if (x.NodeType == XmlNodeType.Element && skipped.Contains(x.LocalName))
                {
                    if (x.IsEmptyElement)
                    {
                        more = x.Read();
                    }
                    else
                    {
                        x.Skip();
                        more = !x.EOF;
                    }

                    continue;
                }

                yield return x;
                more = x.Read();
            }
        }

        private static bool IsText(XmlReader x) =>
            x.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;

        /// <summary>WordprocessingML and DrawingML: runs of <c>t</c>, paragraphs as lines, table cells joined by " | ".</summary>
        private void Ooxml(XmlReader x)
        {
            var inText = false;
            var cells = 0;
            foreach (var node in Walk(x, OoxmlSkipped))
            {
                if (Full)
                {
                    Truncated = true;
                    return;
                }

                if (node.NodeType == XmlNodeType.Element)
                {
                    switch (node.LocalName)
                    {
                        case "t":
                            inText = !node.IsEmptyElement;
                            break;
                        case "tab":
                            Add("   ");
                            break;
                        case "br" or "cr":
                            Add(cells > 0 ? " " : "\n");
                            break;
                        case "tc" when !node.IsEmptyElement:
                            cells++;
                            break;
                        case "p" when node.IsEmptyElement:
                            Add(cells > 0 ? " " : "\n");
                            break;
                    }
                }
                else if (node.NodeType == XmlNodeType.EndElement)
                {
                    switch (node.LocalName)
                    {
                        case "t":
                            inText = false;
                            break;
                        case "p":
                            Add(cells > 0 ? " " : "\n");
                            break;
                        case "tc":
                            cells = Math.Max(0, cells - 1);
                            TrimSpaces();
                            Add(" | ");
                            break;
                        case "tr":
                            EndRow();
                            break;
                    }
                }
                else if (inText && IsText(node))
                {
                    Add(node.Value);
                }
            }
        }

        private void Excel()
        {
            var shared = SharedStrings();
            var dates = DateStyles();
            var sheets = Sheets();
            foreach (var (name, entry) in sheets.Take(MaxParts))
            {
                if (Full)
                {
                    Truncated = true;
                    return;
                }

                Add(Marker(texts.SheetLabel(name)));
                Parse(entry, x => SheetRows(x, shared, dates));
            }

            if (sheets.Count > MaxParts)
            {
                Truncated = true;
            }
        }

        private List<string> SharedStrings()
        {
            var strings = new List<string>();
            Parse(zip.GetEntry("xl/sharedStrings.xml"), x =>
            {
                StringBuilder? item = null;
                var inText = false;
                foreach (var node in Walk(x, OoxmlSkipped))
                {
                    if (node.NodeType == XmlNodeType.Element && node.LocalName == "si")
                    {
                        item = new StringBuilder();
                    }
                    else if (node.NodeType == XmlNodeType.Element && node.LocalName == "t")
                    {
                        inText = !node.IsEmptyElement;
                    }
                    else if (node.NodeType == XmlNodeType.EndElement && node.LocalName == "t")
                    {
                        inText = false;
                    }
                    else if (node.NodeType == XmlNodeType.EndElement && node.LocalName == "si" && item is not null)
                    {
                        strings.Add(item.ToString());
                        item = null;
                    }
                    else if (inText && item is not null && item.Length < 32_768 && IsText(node))
                    {
                        item.Append(node.Value);
                    }
                }
            });
            return strings;
        }

        /// <summary>Cell style index → date kind, from the number formats of <c>cellXfs</c>.</summary>
        private Dictionary<int, DateKind> DateStyles()
        {
            var custom = new Dictionary<int, string>();
            var formats = new List<int>();
            Parse(zip.GetEntry("xl/styles.xml"), x =>
            {
                var inCellXfs = false;
                foreach (var node in Walk(x, OoxmlSkipped))
                {
                    if (node.NodeType == XmlNodeType.Element && node.LocalName == "numFmt"
                        && int.TryParse(node.GetAttribute("numFmtId"), CultureInfo.InvariantCulture, out var id))
                    {
                        custom[id] = node.GetAttribute("formatCode") ?? "";
                    }
                    else if (node.NodeType == XmlNodeType.Element && node.LocalName == "cellXfs")
                    {
                        inCellXfs = !node.IsEmptyElement;
                    }
                    else if (node.NodeType == XmlNodeType.EndElement && node.LocalName == "cellXfs")
                    {
                        inCellXfs = false;
                    }
                    else if (inCellXfs && node.NodeType == XmlNodeType.Element && node.LocalName == "xf" && formats.Count < 100_000)
                    {
                        formats.Add(int.TryParse(node.GetAttribute("numFmtId"), CultureInfo.InvariantCulture, out var format) ? format : 0);
                    }
                }
            });

            var styles = new Dictionary<int, DateKind>();
            for (var i = 0; i < formats.Count; i++)
            {
                if (DateKindOf(formats[i], custom) is { } kind)
                {
                    styles[i] = kind;
                }
            }

            return styles;
        }

        private static DateKind? DateKindOf(int format, Dictionary<int, string> custom)
        {
            switch (format)
            {
                case >= 14 and <= 17:
                    return DateKind.Date;
                case 22:
                    return DateKind.DateTime;
                case (>= 18 and <= 21) or (>= 45 and <= 47):
                    return DateKind.Time;
            }

            // Elapsed time ([h]:mm, [mm]:ss) is a duration, not a point in time: keep the number.
            if (!custom.TryGetValue(format, out var code) || ElapsedTime().IsMatch(code))
            {
                return null;
            }

            // First section only, without quoted text, [colour/locale] blocks and escaped characters.
            var plain = Regex.Replace(code.Split(';')[0], @"""[^""]*""|\[[^\]]*\]|\\.", "", RegexOptions.None, TimeSpan.FromSeconds(1)).ToLowerInvariant();
            var time = plain.Contains('h') || plain.Contains('s');
            var date = plain.Contains('y') || plain.Contains('d') || (plain.Contains('m') && !time);
            return (date, time) switch
            {
                (true, true) => DateKind.DateTime,
                (true, false) => DateKind.Date,
                (false, true) => DateKind.Time,
                _ => null,
            };
        }

        /// <summary>Sheets in workbook order (name and part), via the workbook relationships; part names as a fallback.</summary>
        private List<(string Name, ZipArchiveEntry Entry)> Sheets()
        {
            var targets = new Dictionary<string, string>(StringComparer.Ordinal);
            Parse(zip.GetEntry("xl/_rels/workbook.xml.rels"), x =>
            {
                foreach (var node in Walk(x, OoxmlSkipped))
                {
                    if (node.NodeType == XmlNodeType.Element && node.LocalName == "Relationship"
                        && node.GetAttribute("Id") is { } id && node.GetAttribute("Target") is { } target)
                    {
                        targets[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                    }
                }
            });

            var sheets = new List<(string, ZipArchiveEntry)>();
            Parse(zip.GetEntry("xl/workbook.xml"), x =>
            {
                foreach (var node in Walk(x, OoxmlSkipped))
                {
                    if (node.NodeType == XmlNodeType.Element && node.LocalName == "sheet"
                        && node.GetAttribute("id", RelationshipsNamespace) is { } id
                        && targets.TryGetValue(id, out var target) && zip.GetEntry(target) is { } entry)
                    {
                        sheets.Add((node.GetAttribute("name") ?? entry.Name, entry));
                    }
                }
            });

            return sheets.Count > 0
                ? sheets
                : [.. zip.Entries
                    .Select(e => (Entry: e, Match: SheetPart().Match(e.FullName)))
                    .Where(p => p.Match.Success)
                    .OrderBy(p => int.Parse(p.Match.Groups[1].Value, CultureInfo.InvariantCulture))
                    .Select(p => (texts.DefaultSheetName(p.Match.Groups[1].Value), p.Entry))];
        }

        /// <summary>Rows as "number: cell | cell", columns placed by the cell reference so gaps stay visible.</summary>
        private void SheetRows(XmlReader x, List<string> shared, Dictionary<int, DateKind> dates)
        {
            var row = 0;
            var cells = new List<string>();
            var next = 0;
            var column = 0;
            string? type = null;
            var style = 0;
            var value = new StringBuilder();
            var inValue = false;
            var inInline = false;
            foreach (var node in Walk(x, OoxmlSkipped))
            {
                if (Full)
                {
                    Truncated = true;
                    return;
                }

                if (node.NodeType == XmlNodeType.Element)
                {
                    switch (node.LocalName)
                    {
                        case "row":
                            row = int.TryParse(node.GetAttribute("r"), CultureInfo.InvariantCulture, out var r) ? r : row + 1;
                            cells.Clear();
                            next = 0;
                            break;
                        case "c":
                            column = ColumnOf(node.GetAttribute("r")) ?? next;
                            next = column + 1;
                            type = node.GetAttribute("t");
                            style = int.TryParse(node.GetAttribute("s"), CultureInfo.InvariantCulture, out var s) ? s : 0;
                            value.Clear();
                            break;
                        case "v":
                            inValue = !node.IsEmptyElement;
                            break;
                        case "is":
                            inInline = !node.IsEmptyElement;
                            break;
                        case "t" when inInline:
                            inValue = !node.IsEmptyElement;
                            break;
                    }
                }
                else if (node.NodeType == XmlNodeType.EndElement)
                {
                    switch (node.LocalName)
                    {
                        case "v" or "t":
                            inValue = false;
                            break;
                        case "is":
                            inInline = false;
                            break;
                        case "c":
                            Place(cells, column, Display(type, value.ToString(), style, shared, dates));
                            break;
                        case "row":
                            AddRow(row, cells);
                            break;
                    }
                }
                else if (inValue && value.Length < 32_768 && IsText(node))
                {
                    value.Append(node.Value);
                }
            }
        }

        private static string Display(string? type, string raw, int style, List<string> shared, Dictionary<int, DateKind> dates)
        {
            var text = type switch
            {
                "s" => int.TryParse(raw, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < shared.Count ? shared[i] : "",
                "b" => raw.Trim() == "1" ? "TRUE" : "FALSE",
                "inlineStr" or "str" or "e" or "d" => raw,
                _ => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                    ? dates.TryGetValue(style, out var kind) && FormatDate(number, kind) is { } formatted
                        ? formatted
                        : number.ToString("G15", CultureInfo.InvariantCulture) // drops binary float tails like 0.30000000000000004
                    : raw.Trim(),
            };
            return text.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
        }

        private static string? FormatDate(double serial, DateKind kind)
        {
            if (serial is < 0 or > 2_958_465)
            {
                return null;
            }

            var value = DateTime.FromOADate(serial);
            return kind switch
            {
                DateKind.Date => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                DateKind.Time => value.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                _ => value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
        }

        /// <summary>Zero-based column of a reference such as "AB12"; null when absent or malformed.</summary>
        private static int? ColumnOf(string? reference)
        {
            if (string.IsNullOrEmpty(reference))
            {
                return null;
            }

            var column = 0;
            var letters = 0;
            foreach (var c in reference)
            {
                if (!char.IsAsciiLetter(c))
                {
                    break;
                }

                column = (column * 26) + (char.ToUpperInvariant(c) - 'A' + 1);
                if (++letters > 3)
                {
                    return null;
                }
            }

            return letters == 0 ? null : column - 1;
        }

        private static void Place(List<string> cells, int column, string text)
        {
            if (column >= MaxColumns || text.Length == 0)
            {
                return;
            }

            while (cells.Count <= column)
            {
                cells.Add("");
            }

            cells[column] = text;
        }

        private void AddRow(int? number, List<string> cells)
        {
            var last = cells.FindLastIndex(c => c.Length > 0);
            if (last >= 0)
            {
                Add($"{(number is { } n ? $"{n}: " : "")}{string.Join(" | ", cells.Take(last + 1))}\n");
            }
        }

        /// <summary>OpenDocument <c>content.xml</c>: paragraphs, spaces and tabs, tables with repeats capped, slides labelled.</summary>
        private void Odf(XmlReader x, bool numberRows)
        {
            StringBuilder? cell = null;
            var cells = new List<string>();
            var paragraphs = 0;
            var row = 0;
            var rowRepeat = 1;
            var columnRepeat = 1;
            var slide = 0;
            foreach (var node in Walk(x, OdfSkipped))
            {
                if (Full)
                {
                    Truncated = true;
                    return;
                }

                if (node.NodeType == XmlNodeType.Element)
                {
                    switch (node.LocalName)
                    {
                        case "p" or "h" when !node.IsEmptyElement:
                            paragraphs++;
                            break;
                        case "s":
                            Append(cell, new string(' ', Repeat(node.GetAttribute("c", OdfText), MaxSpaces)));
                            break;
                        case "tab":
                            Append(cell, "   ");
                            break;
                        case "line-break":
                            Append(cell, cell is null ? "\n" : " ");
                            break;
                        case "table":
                            Add(Marker(texts.TableLabel(node.GetAttribute("name", OdfTable))));
                            row = 0;
                            break;
                        case "table-row":
                            cells.Clear();
                            rowRepeat = Repeat(node.GetAttribute("number-rows-repeated", OdfTable), 1_048_576);
                            if (node.IsEmptyElement)
                            {
                                row += rowRepeat;
                            }

                            break;
                        case "table-cell" or "covered-table-cell":
                            columnRepeat = Repeat(node.GetAttribute("number-columns-repeated", OdfTable), 16_384);
                            if (node.IsEmptyElement)
                            {
                                AddCells(cells, "", columnRepeat);
                            }
                            else
                            {
                                cell = new StringBuilder();
                            }

                            break;
                        case "page":
                            Add(Marker(texts.SlideLabel(++slide)));
                            break;
                    }
                }
                else if (node.NodeType == XmlNodeType.EndElement)
                {
                    switch (node.LocalName)
                    {
                        case "p" or "h":
                            paragraphs = Math.Max(0, paragraphs - 1);
                            Append(cell, cell is null ? "\n" : " ");
                            break;
                        case "table-cell" or "covered-table-cell":
                            AddCells(cells, cell?.ToString().Trim() ?? "", columnRepeat);
                            cell = null;
                            break;
                        case "table-row":
                            var shown = cells.Any(c => c.Length > 0) ? Math.Min(rowRepeat, MaxRepeat) : 0;
                            for (var i = 0; i < shown; i++)
                            {
                                AddRow(numberRows ? row + i + 1 : null, cells);
                            }

                            row += rowRepeat;
                            break;
                    }
                }
                else if (paragraphs > 0 && IsText(node))
                {
                    Append(cell, node.Value);
                }
            }
        }

        private void Append(StringBuilder? cell, string text)
        {
            if (cell is null)
            {
                Add(text);
            }
            else if (cell.Length < 32_768)
            {
                cell.Append(text);
            }
        }

        private static void AddCells(List<string> cells, string text, int repeat)
        {
            // Empty cells only matter between filled ones (AddRow drops trailing ones); a filled cell repeats at most MaxRepeat times.
            var count = Math.Min(text.Length == 0 ? repeat : Math.Min(repeat, MaxRepeat), MaxColumns - cells.Count);
            for (var i = 0; i < count; i++)
            {
                cells.Add(text);
            }
        }

        private static int Repeat(string? value, int max) =>
            int.TryParse(value, CultureInfo.InvariantCulture, out var n) ? Math.Clamp(n, 1, max) : 1;

        private void Add(string text)
        {
            if (Full)
            {
                Truncated = true;
                return;
            }

            var room = MaxTextChars - Text.Length;
            if (text.Length > room)
            {
                Text.Append(text, 0, room);
                Truncated = true;
            }
            else
            {
                Text.Append(text);
            }
        }

        private void TrimSpaces()
        {
            while (Text.Length > 0 && Text[^1] == ' ')
            {
                Text.Length--;
            }
        }

        /// <summary>Ends a table row: drops the separator after the last cell.</summary>
        private void EndRow()
        {
            TrimSpaces();
            if (Text.Length > 0 && Text[^1] == '|')
            {
                Text.Length--;
                TrimSpaces();
            }

            Add("\n");
        }

        internal void Inflated(long bytes) => _inflated += bytes;

        internal void HitCap() => Truncated = true;
    }

    /// <summary>Reads at most <paramref name="limit"/> inflated bytes, then reports end of stream and marks the cut.</summary>
    private sealed class CappedStream(Stream inner, long limit, Reader owner) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_read >= limit)
            {
                owner.HitCap();
                return 0;
            }

            var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, limit - _read)]);
            _read += read;
            owner.Inflated(read);
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
