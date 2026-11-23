using DiscordMcp.Discord;

namespace DiscordMcp.Tests;

/// <summary>The display zone is configurable. Tests override it per async flow and never touch the process default.</summary>
public class DisplayTimeTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_pinned_test_default_is_Prague()
    {
        Assert.Equal("Europe/Prague", DisplayTime.ZoneId);
        Assert.Equal("2026-11-20 13:00 +01:00", DisplayTime.Format(Noon));
    }

    [Fact]
    public void A_configured_iana_zone_decides_the_formats_and_the_name_in_the_instructions()
    {
        using var _ = DisplayTime.Use("Asia/Tokyo");

        Assert.Equal("Asia/Tokyo", DisplayTime.ZoneId);
        Assert.Equal("2026-11-20 21:00 +09:00", DisplayTime.Format(Noon));
        Assert.Equal("21:00:00", DisplayTime.FormatClock(Noon));
    }

    [Fact]
    public void An_invalid_zone_means_the_system_zone_and_is_reported_by_TryResolve()
    {
        Assert.False(DisplayTime.TryResolve("Nope/Nowhere", out var zone, out var ianaId));
        Assert.Equal(TimeZoneInfo.Local, zone);
        Assert.False(string.IsNullOrWhiteSpace(ianaId));

        using var _ = DisplayTime.Use("Nope/Nowhere");
        Assert.Equal(TimeZoneInfo.Local, DisplayTime.Zone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_zone_configured_means_the_system_zone_without_a_complaint(string? configured)
    {
        Assert.True(DisplayTime.TryResolve(configured, out var zone, out _));
        Assert.Equal(TimeZoneInfo.Local, zone);
    }

    [Fact]
    public void A_zone_configured_by_its_windows_id_is_named_by_an_iana_id_for_the_server_instructions()
    {
        Assert.True(DisplayTime.TryResolve("Central Europe Standard Time", out var zone, out var ianaId));

        Assert.Equal("Central Europe Standard Time", zone.Id);
        Assert.Contains("/", ianaId, StringComparison.Ordinal); // Europe/Prague or Europe/Budapest, depending on the region
        Assert.False(string.IsNullOrWhiteSpace(DisplayTime.TryResolve(null, out _, out var system) ? system : ""));
    }

    [Theory]
    [InlineData("Asia/Tokyo", "2026-11-20", "2026-11-19T15:00:00Z")]
    [InlineData("Asia/Tokyo", "2026-11-20 14:00", "2026-11-20T05:00:00Z")]
    [InlineData("America/New_York", "2026-11-20 14:00", "2026-11-20T19:00:00Z")]
    [InlineData("America/New_York", "2026-07-01", "2026-07-01T04:00:00Z")]
    [InlineData("Europe/Prague", "2026-11-20", "2026-11-19T23:00:00Z")]
    public void A_cursor_date_without_an_offset_is_read_in_the_configured_zone(string zone, string date, string expectedUtc)
    {
        using var _ = DisplayTime.Use(zone);

        Assert.True(Snowflake.TryParseCursor(date, out var id));

        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), Snowflake.ToTimestamp(id));
    }

    [Fact]
    public void A_cursor_with_an_explicit_offset_ignores_the_zone()
    {
        using var _ = DisplayTime.Use("Asia/Tokyo");

        Assert.True(Snowflake.TryParseCursor("2026-11-20T12:00:00Z", out var id));

        Assert.Equal(Noon, Snowflake.ToTimestamp(id));
    }

    [Fact]
    public void A_time_skipped_by_the_spring_change_maps_to_one_hour_later_in_any_zone()
    {
        using var _ = DisplayTime.Use("America/New_York");

        var instant = Snowflake.FromWallClock(new DateTime(2026, 3, 8, 2, 30, 0, DateTimeKind.Unspecified));

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 30, 0, TimeSpan.FromHours(-4)), instant);
    }

    [Fact]
    public async Task The_override_stays_in_its_own_flow()
    {
        using var _ = DisplayTime.Use("Asia/Tokyo");

        var other = await Task.Run(() =>
        {
            using var inner = DisplayTime.Use("America/New_York");
            return DisplayTime.ZoneId;
        });

        Assert.Equal("America/New_York", other);
        Assert.Equal("Asia/Tokyo", DisplayTime.ZoneId);
    }
}
