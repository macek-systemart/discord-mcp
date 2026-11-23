using System.Globalization;

namespace DiscordMcp.Discord;

/// <summary>
/// All times shown to the user and Claude are wall-clock times in one zone with an explicit offset. The zone is the
/// process default (set once at program entry from <c>Discord:TimeZone</c>, an IANA id such as <c>Europe/Prague</c>,
/// else the system zone) unless <see cref="Use"/> overrides it for the current async flow (tests).
/// </summary>
public static class DisplayTime
{
    private sealed record Setting(TimeZoneInfo Zone, string IanaId);

    private static readonly AsyncLocal<Setting?> Override = new();
    private static volatile Setting _default = Local();

    public static TimeZoneInfo Zone => (Override.Value ?? _default).Zone;

    /// <summary>The IANA id of <see cref="Zone"/> (a Windows zone is converted), for the server instructions.</summary>
    public static string ZoneId => (Override.Value ?? _default).IanaId;

    /// <summary>
    /// Resolves a configured zone id. An empty id is the system zone. An id that names no zone returns false, and the
    /// system zone is returned anyway.
    /// </summary>
    public static bool TryResolve(string? id, out TimeZoneInfo zone, out string ianaId)
    {
        var trimmed = id?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
                ianaId = trimmed.Contains('/', StringComparison.Ordinal) || trimmed == "UTC" ? trimmed : IanaIdOf(zone);
                return true;
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Fall through to the system zone; the caller reports the bad id.
            }
        }

        var local = Local();
        zone = local.Zone;
        ianaId = local.IanaId;
        return string.IsNullOrEmpty(trimmed);
    }

    /// <summary>Sets the process default. False when <paramref name="id"/> names no zone (the system zone is used).</summary>
    public static bool TryConfigure(string? id)
    {
        var known = TryResolve(id, out var zone, out var ianaId);
        _default = new Setting(zone, ianaId);
        return known;
    }

    /// <summary>Overrides the zone for the current async flow until the result is disposed.</summary>
    public static IDisposable Use(string? id)
    {
        TryResolve(id, out var zone, out var ianaId);
        var previous = Override.Value;
        Override.Value = new Setting(zone, ianaId);
        return new Restore(() => Override.Value = previous);
    }

    public static DateTimeOffset ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    /// <summary><c>2026-11-20 14:03 +01:00</c></summary>
    public static string Format(DateTimeOffset instant) =>
        ToLocal(instant).ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    /// <summary><c>14:03:27</c>, for "try again at" messages.</summary>
    public static string FormatClock(DateTimeOffset instant) =>
        ToLocal(instant).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    private static Setting Local() => new(TimeZoneInfo.Local, IanaIdOf(TimeZoneInfo.Local));

    /// <summary>
    /// A zone found by IANA id on Windows reports a Windows id, which is converted back with the user's region first
    /// (Central Europe Standard Time is Europe/Prague in CZ, not Europe/Budapest).
    /// </summary>
    private static string IanaIdOf(TimeZoneInfo zone)
    {
        if (zone.HasIanaId)
        {
            return zone.Id;
        }

        try
        {
            var region = RegionInfo.CurrentRegion.TwoLetterISORegionName;
            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, region, out var regional) && regional is not null)
            {
                return regional;
            }

            if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && iana is not null)
            {
                return iana;
            }
        }
        catch (Exception e) when (e is PlatformNotSupportedException or ArgumentException)
        {
            // No ICU data: the Windows id is the best name left.
        }

        return zone.Id;
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
