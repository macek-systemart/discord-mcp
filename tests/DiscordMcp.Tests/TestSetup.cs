using System.Runtime.CompilerServices;
using DiscordMcp.Discord;
using DiscordMcp.Localization;

namespace DiscordMcp.Tests;

/// <summary>
/// Pins the process defaults once, so no test depends on the machine: Czech texts and the Europe/Prague time zone.
/// A test that needs another language or zone uses <see cref="Languages.Use"/> or <see cref="DisplayTime.Use"/>, which
/// override it for the current async flow only; tests never call the process-wide setters.
/// </summary>
internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Pin()
    {
        Languages.SetDefault(Languages.Find("cs")!);
        DisplayTime.TryConfigure("Europe/Prague");
    }
}
