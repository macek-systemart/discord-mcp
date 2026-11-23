using System.Globalization;
using DiscordMcp.Discord;
using DiscordMcp.Localization;
using Microsoft.Extensions.Configuration;

namespace DiscordMcp;

/// <summary>
/// Process-wide settings that must be in place before anything produces text (options included): the language
/// (<c>Discord:Language</c> → else the Windows UI language → else English) and the display time zone
/// (<c>Discord:TimeZone</c>, an IANA id → else the system zone). Read from appsettings.json next to the exe and from
/// environment variables (<c>Discord__Language</c>, <c>Discord__TimeZone</c>). Not from the command line: its first
/// argument is the verb, and a CLI switch such as <c>--selftest</c> would swallow the next argument as its value.
/// </summary>
internal static class Startup
{
    public const string LanguageKey = $"{DiscordOptions.SectionName}:Language";
    public const string TimeZoneKey = $"{DiscordOptions.SectionName}:TimeZone";

    public static void Configure()
    {
        var config = Build();
        Apply(config[LanguageKey], config[TimeZoneKey], CultureInfo.CurrentUICulture, Console.Error);
    }

    /// <summary>Sets the process defaults from the configured values; see <see cref="Resolve"/> for the fallbacks.</summary>
    internal static void Apply(string? language, string? timeZone, CultureInfo uiCulture, TextWriter warnings)
    {
        Languages.SetDefault(Resolve(language, timeZone, uiCulture, warnings));
        DisplayTime.TryConfigure(timeZone);
    }

    /// <summary>
    /// The language to use. An unknown language code falls back to the UI-culture rule and an invalid time zone to the
    /// system zone; both are reported on <paramref name="warnings"/> (stderr), in the language that ended up in use.
    /// Changes no process state.
    /// </summary>
    internal static Texts Resolve(string? language, string? timeZone, CultureInfo uiCulture, TextWriter warnings)
    {
        var texts = Languages.Resolve(language, uiCulture);
        if (!string.IsNullOrWhiteSpace(language) && Languages.Find(language) is null)
        {
            warnings.WriteLine(texts.UnknownLanguageWarning(language.Trim(), Languages.Supported));
        }

        if (!DisplayTime.TryResolve(timeZone, out _, out _))
        {
            warnings.WriteLine(texts.UnknownTimeZoneWarning(timeZone!.Trim()));
        }

        return texts;
    }

    internal static IConfiguration Build()
    {
        try
        {
            return new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException)
        {
            // A broken appsettings.json fails the server host later with its own message; here it must not stop a CLI verb.
            return new ConfigurationBuilder().AddEnvironmentVariables().Build();
        }
    }
}
