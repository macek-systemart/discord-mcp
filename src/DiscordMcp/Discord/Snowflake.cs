using System.Globalization;

namespace DiscordMcp.Discord;

/// <summary>Snowflake ↔ time, and the cursor inputs tools accept (a message id or an ISO date in the display time zone).</summary>
public static class Snowflake
{
    public const long DiscordEpochMs = 1_420_070_400_000;

    /// <summary>The largest cursor handed to Discord: snowflakes are signed 64-bit there (reached in 2084).</summary>
    public const ulong MaxCursor = long.MaxValue;

    /// <summary>
    /// Real message ids have at least this many digits. A number of 8–14 digits has the shape of a date or a Unix time
    /// (20261120, 1763640000) and is refused instead of being read as an id from 2015. Tiny numbers such as 1 stay
    /// valid: they mean "from the very beginning".
    /// </summary>
    private const int MinIdDigits = 15;

    private const int MinDateLikeDigits = 8;

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

    /// <summary>
    /// The smallest snowflake at <paramref name="instant"/>; 0 before the Discord epoch and <see cref="MaxCursor"/> from
    /// 2084 on (a far-future date such as 9999-12-31 means "no limit", it must not wrap around).
    /// </summary>
    public static ulong FromTimestamp(DateTimeOffset instant)
    {
        var ms = instant.ToUnixTimeMilliseconds() - DiscordEpochMs;
        return ms <= 0 ? 0 : ms >= 1L << 41 ? MaxCursor : (ulong)ms << 22;
    }

    /// <summary>
    /// Accepts a snowflake (digits, but not 8–14 of them) or a date: <c>2026-11-20</c> (midnight in the display time zone),
    /// <c>2026-11-20 14:00</c> (wall clock in the display time zone), or an ISO timestamp with an offset or <c>Z</c>.
    /// <paramref name="upperBound"/> is true for <c>before</c>: an hour repeated by the autumn DST change then means its
    /// later occurrence, otherwise its earlier one, so neither bound drops messages from that hour.
    /// </summary>
    public static bool TryParseCursor(string? raw, out ulong id, bool upperBound = false)
    {
        id = 0;
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        if (value.All(char.IsAsciiDigit))
        {
            return value.Length is < MinDateLikeDigits or >= MinIdDigits
                && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        // Offset-less forms first: "K" in OffsetFormats also matches a missing offset and would then use the machine's
        // time zone instead of the display zone.
        if (DateTime.TryParseExact(value, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            // Outside these years the zone offset could push the instant past DateTimeOffset's range.
            id = local.Year < 2015 ? 0 : local.Year > 9000 ? MaxCursor : FromTimestamp(FromWallClock(local, upperBound));
            return true;
        }

        if (DateTimeOffset.TryParseExact(value, OffsetFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withOffset))
        {
            id = FromTimestamp(withOffset);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Display-zone wall-clock → instant. A time skipped by the spring DST change maps to one hour later. A time repeated
    /// by the autumn change maps to its later occurrence when <paramref name="latest"/> is true, else to its earlier one.
    /// </summary>
    public static DateTimeOffset FromWallClock(DateTime local, bool latest = false)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (DisplayTime.Zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        if (DisplayTime.Zone.IsAmbiguousTime(unspecified))
        {
            // The larger offset is the earlier instant (summer time), the smaller one the later instant.
            var offsets = DisplayTime.Zone.GetAmbiguousTimeOffsets(unspecified);
            return new DateTimeOffset(unspecified, latest ? offsets.Min() : offsets.Max());
        }

        var offset = DisplayTime.Zone.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset);
    }
}
