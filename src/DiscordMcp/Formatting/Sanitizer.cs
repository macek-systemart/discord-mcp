using System.Globalization;
using System.Text;
using DiscordMcp.Localization;

namespace DiscordMcp.Formatting;

/// <summary>
/// Makes untrusted Discord text safe to embed in tool output: no control or invisible format characters, line breaks
/// only where this server puts them, and no literal self marker (<c>[JÁ]</c>, <c>[ME]</c>, …) of any supported
/// language that could pass for one of ours, whichever language is active.
/// Code points are compared numerically on purpose: invisible characters must never appear literally in this file.
/// </summary>
public static class Sanitizer
{
    /// <summary>Prefix of every content line after the first: no content line ever starts at column 0.</summary>
    public const string Continuation = "  │ ";

    /// <summary>Marks messages written by the token's own account, in the active language.</summary>
    public static string SelfMarker => Languages.Current.SelfMarker;

    private const int ZeroWidthJoiner = 0x200D;
    private const int EmojiVariationSelector = 0xFE0F;

    /// <summary>One line: line breaks and tabs become spaces, the result is cut to <paramref name="maxLength"/>.</summary>
    public static string Line(string? value, int maxLength = 200)
    {
        var text = Clean(value, keepLineBreaks: false).Trim();
        return Truncate(text, maxLength);
    }

    /// <summary>
    /// A display name or username shown in a message header. Besides <see cref="Line"/>, square brackets become
    /// parentheses, so no name can imitate a marker such as <c>[JÁ]</c> or <c>[bot]</c> next to the author.
    /// </summary>
    public static string Name(string? value, int maxLength = 80)
    {
        // NFKC folds compatibility forms (fullwidth ［］ and the like) into ASCII first; then every opening or closing
        // bracket of any script becomes a parenthesis.
        var line = Line(value, int.MaxValue);
        string folded;
        try
        {
            folded = line.Normalize(NormalizationForm.FormKC);
        }
        catch (ArgumentException)
        {
            // Line already left only valid scalar values; if folding still fails, the bracket mapping below applies anyway.
            folded = line;
        }

        var sb = new StringBuilder(folded.Length);
        foreach (var rune in folded.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.OpenPunctuation)
            {
                sb.Append('(');
            }
            else if (category == UnicodeCategory.ClosePunctuation)
            {
                sb.Append(')');
            }
            else
            {
                sb.Append(rune.ToString());
            }
        }

        return Truncate(sb.ToString(), maxLength);
    }

    /// <summary>Multi-line text with every line break normalized to <c>\n</c>.</summary>
    public static string Text(string? value, int maxLength = int.MaxValue) =>
        Truncate(Clean(value, keepLineBreaks: true).TrimEnd(), maxLength);

    /// <summary>
    /// Text that follows a header on the same line: the first line stays as is, every later line is prefixed with
    /// <see cref="Continuation"/>.
    /// </summary>
    public static string Indented(string? value, int maxLength = int.MaxValue)
    {
        var text = Text(value, maxLength);
        return text.Contains('\n', StringComparison.Ordinal)
            ? string.Join("\n", text.Split('\n').Select((line, i) => i == 0 ? line : (Continuation + line).TrimEnd()))
            : text;
    }

    /// <summary>Cuts to <paramref name="maxLength"/> characters (ending with "…") without splitting a surrogate pair.</summary>
    public static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = Math.Max(0, maxLength - 1);
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value[..cut] + "…";
    }

    private static string Clean(string? value, bool keepLineBreaks)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        var sb = new StringBuilder(value.Length);
        var afterCarriageReturn = false;
        var afterEmoji = false;
        Span<char> buffer = stackalloc char[2];

        // EnumerateRunes yields U+FFFD for ill-formed UTF-16 (lone surrogates), so only scalar values reach the filter.
        foreach (var rune in value.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp == '\n' && afterCarriageReturn)
            {
                afterCarriageReturn = false;
                continue;
            }

            afterCarriageReturn = cp == '\r';
            if (IsLineBreak(cp))
            {
                sb.Append(keepLineBreaks ? '\n' : ' ');
                afterEmoji = false;
            }
            else if (cp == '\t')
            {
                sb.Append(' ');
                afterEmoji = false;
            }
            else if (cp is ZeroWidthJoiner or EmojiVariationSelector)
            {
                // Emoji glue survives only right after an emoji; anywhere else it could split a marker like [JÁ].
                if (afterEmoji)
                {
                    sb.Append((char)cp);
                }
            }
            else if (IsAllowed(rune))
            {
                sb.Append(buffer[..rune.EncodeToUtf16(buffer)]);
                afterEmoji = Rune.GetUnicodeCategory(rune) is UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol;
            }
        }

        // NFC, so a combining-accent spelling of the marker is caught too. Only allowed scalar values are left, so
        // normalization should not fail; if it ever does, fail closed by dropping every combining mark.
        var filtered = sb.ToString();
        string text;
        try
        {
            text = filtered.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            text = string.Concat(filtered.EnumerateRunes()
                .Where(r => Rune.GetUnicodeCategory(r) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
                .Select(r => r.ToString()));
        }

        // Every language's marker, not only the active one, and in any letter case: Discord content must never forge
        // "this is the user" in any language. The price is that ordinary text such as [me](link) becomes (ME)(link).
        foreach (var language in Languages.All)
        {
            text = text.Replace(language.SelfMarker, language.NeutralizedSelfMarker, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    /// <summary>
    /// Drops C0/C1 controls, every format character (Cf: bidi overrides and isolates, zero-width spaces, BOM, soft
    /// hyphen, the invisible tag characters used for "ASCII smuggling"), every other default-ignorable code point
    /// (variation selectors used for "emoji smuggling", the combining grapheme joiner, Hangul fillers, …) and all
    /// noncharacters (U+FDD0–FDEF and every U+xxFFFE/U+xxFFFF). ZWJ and VS16 are handled by the caller.
    /// </summary>
    private static bool IsAllowed(Rune rune)
    {
        var cp = rune.Value;
        if ((cp & 0xFFFE) == 0xFFFE || cp is >= 0xFDD0 and <= 0xFDEF || IsDefaultIgnorable(cp))
        {
            return false;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            UnicodeCategory.Control => false,
            UnicodeCategory.Format => false,
            UnicodeCategory.Surrogate => false,
            _ => true,
        };
    }

    /// <summary>Unicode Default_Ignorable_Code_Point (DerivedCoreProperties.txt): rendered invisibly by design.</summary>
    private static bool IsDefaultIgnorable(int cp) => cp is
        0x00AD or 0x034F or 0x061C or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFEFF or 0xFFA0
        or (>= 0x180B and <= 0x180F)
        or (>= 0x200B and <= 0x200F)
        or (>= 0x202A and <= 0x202E)
        or (>= 0x2060 and <= 0x206F)
        or (>= 0xFE00 and <= 0xFE0F)
        or (>= 0xFFF0 and <= 0xFFF8)
        or (>= 0x1BCA0 and <= 0x1BCA3)
        or (>= 0x1D173 and <= 0x1D17A)
        or (>= 0xE0000 and <= 0xE0FFF);

    /// <summary>LF, CR, VT, FF, NEL, LINE SEPARATOR, PARAGRAPH SEPARATOR.</summary>
    private static bool IsLineBreak(int cp) => cp is 0x0A or 0x0D or 0x0B or 0x0C or 0x85 or 0x2028 or 0x2029;
}
