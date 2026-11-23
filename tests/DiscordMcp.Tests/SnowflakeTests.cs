using DiscordMcp.Discord;

namespace DiscordMcp.Tests;

public class SnowflakeTests
{
    [Fact]
    public void Round_trips_through_time_at_millisecond_precision()
    {
        var instant = new DateTimeOffset(2026, 11, 20, 13, 5, 0, 123, TimeSpan.Zero);
        var id = Snowflake.FromTimestamp(instant);
        Assert.Equal(instant, Snowflake.ToTimestamp(id));
        Assert.Equal(instant, Snowflake.ToTimestamp(id | 0x3FFFFF)); // worker/process/increment bits ignored
    }

    [Fact]
    public void Dates_before_the_discord_epoch_clamp_to_zero()
    {
        Assert.Equal(0UL, Snowflake.FromTimestamp(new DateTimeOffset(2010, 1, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("1300000000000000010", 1300000000000000010UL)]
    [InlineData(" 1300000000000000010 ", 1300000000000000010UL)]
    [InlineData("1", 1UL)] // "from the very beginning"
    public void Snowflake_cursors_pass_through(string raw, ulong expected)
    {
        Assert.True(Snowflake.TryParseCursor(raw, out var id));
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("2026-11-20", "2026-11-19T23:00:00Z")] // winter: Prague = UTC+1
    [InlineData("2026-07-01", "2026-06-30T22:00:00Z")] // summer: Prague = UTC+2
    [InlineData("2026-11-20 14:30", "2026-11-20T13:30:00Z")]
    [InlineData("2026-11-20T14:30", "2026-11-20T13:30:00Z")]
    [InlineData("2026-11-20T14:30:00Z", "2026-11-20T14:30:00Z")]
    [InlineData("2026-11-20T14:30:00+01:00", "2026-11-20T13:30:00Z")]
    [InlineData("2026-11-20 14:30 +01:00", "2026-11-20T13:30:00Z")]
    [InlineData("2026-03-29 02:30", "2026-03-29T01:30:00Z")] // skipped by the spring DST change → 03:30 CEST
    public void Dates_are_prague_wall_clock_unless_an_offset_is_given(string raw, string expectedUtc)
    {
        Assert.True(Snowflake.TryParseCursor(raw, out var id));
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), Snowflake.ToTimestamp(id));
    }

    [Theory]
    [InlineData("2026-10-25 02:30", false, "2026-10-25T00:30:00Z")] // repeated hour: after/around = first occurrence (CEST)
    [InlineData("2026-10-25 02:30", true, "2026-10-25T01:30:00Z")] // before = second occurrence (CET)
    [InlineData("2026-10-25 01:30", true, "2026-10-24T23:30:00Z")] // not ambiguous: the bound does not matter
    public void A_repeated_autumn_hour_never_drops_messages_from_either_bound(string raw, bool upperBound, string expectedUtc)
    {
        Assert.True(Snowflake.TryParseCursor(raw, out var id, upperBound));
        Assert.Equal(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture), Snowflake.ToTimestamp(id));
    }

    [Theory]
    [InlineData("9999-12-31")]
    [InlineData("9999-12-31 23:59")]
    [InlineData("2200-01-01")]
    [InlineData("2200-01-01T00:00:00Z")]
    public void Far_future_dates_mean_no_limit_instead_of_wrapping_around(string raw)
    {
        Assert.True(Snowflake.TryParseCursor(raw, out var id, upperBound: true));
        Assert.Equal(Snowflake.MaxCursor, id);
    }

    [Theory]
    [InlineData("0001-01-01")]
    [InlineData("2014-06-01 12:00")]
    public void Dates_before_discord_existed_are_the_smallest_cursor(string raw)
    {
        Assert.True(Snowflake.TryParseCursor(raw, out var id));
        Assert.Equal(0UL, id);
    }

    [Fact]
    public void Far_future_timestamps_clamp_instead_of_overflowing()
    {
        Assert.Equal(Snowflake.MaxCursor, Snowflake.FromTimestamp(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(Snowflake.FromTimestamp(new DateTimeOffset(2080, 1, 1, 0, 0, 0, TimeSpan.Zero)) < Snowflake.MaxCursor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("včera")]
    [InlineData("20.11.2026")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("20261120")] // a date without dashes, not a message id
    [InlineData("1763640000")] // a unix timestamp
    public void Anything_else_is_rejected(string? raw)
    {
        Assert.False(Snowflake.TryParseCursor(raw, out _));
    }

    [Fact]
    public void Prague_format_shows_the_offset()
    {
        Assert.Equal("2026-11-20 14:05 +01:00", DisplayTime.Format(new DateTimeOffset(2026, 11, 20, 13, 5, 0, TimeSpan.Zero)));
        Assert.Equal("2026-07-01 14:05 +02:00", DisplayTime.Format(new DateTimeOffset(2026, 7, 1, 12, 5, 0, TimeSpan.Zero)));
    }
}
