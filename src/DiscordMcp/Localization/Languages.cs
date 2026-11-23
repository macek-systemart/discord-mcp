using System.Globalization;

namespace DiscordMcp.Localization;

/// <summary>
/// The supported languages and the one in effect. <see cref="Current"/> is the process default (set once at program
/// entry from <c>Discord:Language</c> or the Windows UI language) unless <see cref="Use"/> overrides it for the current
/// async flow, which is how tests run in a language of their own without touching each other.
/// </summary>
public static class Languages
{
    private static readonly Texts EnglishTexts = new English();
    private static readonly AsyncLocal<Texts?> Override = new();
    private static volatile Texts _default = EnglishTexts;

    /// <summary>Every supported language. Adding a language is one more entry here.</summary>
    public static IReadOnlyList<Texts> All { get; } = [new Czech(), EnglishTexts];

    /// <summary>Used when neither the configuration nor the UI language names a supported language.</summary>
    public static Texts Fallback => EnglishTexts;

    /// <summary>The comma-separated codes of <see cref="All"/>, for messages.</summary>
    public static string Supported => string.Join(", ", All.Select(l => l.Code));

    /// <summary>The language of this async flow if <see cref="Use"/> set one, else the process default.</summary>
    public static Texts Current => Override.Value ?? _default;

    /// <summary>The language with this code (case-insensitive; <c>en-US</c> finds <c>en</c>), or null.</summary>
    public static Texts? Find(string? code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        var separator = trimmed.IndexOfAny(['-', '_']);
        var primary = separator > 0 ? trimmed[..separator] : trimmed;
        return All.FirstOrDefault(l => string.Equals(l.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? All.FirstOrDefault(l => string.Equals(l.Code, primary, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A configured code wins; otherwise the UI culture and its parents are tried (<c>cs-CZ</c> → <c>cs</c>); otherwise
    /// <see cref="Fallback"/>. An unknown configured code is ignored here: the caller reports it.
    /// </summary>
    public static Texts Resolve(string? configured, CultureInfo uiCulture)
    {
        if (Find(configured) is { } explicitLanguage)
        {
            return explicitLanguage;
        }

        for (var culture = uiCulture; culture.Name.Length > 0; culture = culture.Parent)
        {
            if (Find(culture.Name) is { } match)
            {
                return match;
            }
        }

        return Fallback;
    }

    /// <summary>Sets the process default. Called once at program entry, before anything produces text.</summary>
    public static void SetDefault(Texts texts) => _default = texts;

    /// <summary>Overrides the language for the current async flow until the result is disposed.</summary>
    public static IDisposable Use(Texts texts)
    {
        var previous = Override.Value;
        Override.Value = texts;
        return new Restore(() => Override.Value = previous);
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                restore();
            }
        }
    }
}
