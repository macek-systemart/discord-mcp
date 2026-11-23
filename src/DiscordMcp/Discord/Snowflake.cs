using System.Globalization;

namespace DiscordMcp.Discord;

/// <summary>Snowflake ↔ time, and the cursor inputs tools accept (a message id or an ISO date in the display time zone).</summary>
public static class Snowflake
{
    public const long DiscordEpochMs = 1_420_070_400_000;

    private static readonly string[] LocalFormats =
    [
        "yyyy-MM-dd",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd'T'HH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.FFFFFFF",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
    ];

    private static readonly string[] OffsetFormats =
    [
        "yyyy-MM-dd'T'HH:mmK",
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd HH:mmK",
        "yyyy-MM-dd HH:mm:ssK",
        "yyyy-MM-dd HH:mm zzz",
        "yyyy-MM-dd HH:mm:ss zzz",
    ];

    public static DateTimeOffset ToTimestamp(ulong id) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)(id >> 22) + DiscordEpochMs);

    /// <summary>The smallest snowflake at <paramref name="instant"/>; 0 before the Discord epoch.</summary>
    public static ulong FromTimestamp(DateTimeOffset instant)
    {
        var ms = instant.ToUnixTimeMilliseconds() - DiscordEpochMs;
        return ms <= 0 ? 0 : (ulong)ms << 22;
    }

    /// <summary>
    /// Accepts a snowflake (digits) or a date: <c>2026-11-20</c> (midnight in the display time zone), <c>2026-11-20 14:00</c>
    /// (wall clock in the display time zone), or an ISO timestamp with an offset or <c>Z</c>.
    /// </summary>
    public static bool TryParseCursor(string? raw, out ulong id)
    {
        id = 0;
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.All(char.IsAsciiDigit))
        {
            return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        // Offset-less forms first: "K" in OffsetFormats also matches a missing offset and would then use the machine's
        // time zone instead of the display zone.
        if (DateTime.TryParseExact(value, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            id = FromTimestamp(FromWallClock(local));
            return true;
        }

        if (DateTimeOffset.TryParseExact(value, OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            id = FromTimestamp(withOffset);
            return true;
        }

        return false;
    }

    /// <summary>Display-zone wall-clock → instant. A time skipped by the spring DST change maps to one hour later.</summary>
    public static DateTimeOffset FromWallClock(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (DisplayTime.Zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        var offset = DisplayTime.Zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }
}
