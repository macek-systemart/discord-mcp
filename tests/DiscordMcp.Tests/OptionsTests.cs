namespace DiscordMcp.Tests;

public class OptionsTests
{
    [Theory]
    [InlineData("111111111111111111", 111111111111111111UL)]
    [InlineData(" 111111111111111111 ", 111111111111111111UL)]
    public void One_guild_id_is_accepted(string raw, ulong expected)
    {
        var o = new DiscordOptions();
        o.ApplyGuildId(raw);
        Assert.Equal(expected, o.GuildId);
        Assert.Null(o.GuildIdError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("111,222")]
    [InlineData("111 222")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("0")]
    public void Anything_but_one_guild_id_is_an_error(string? raw)
    {
        var o = new DiscordOptions();
        o.ApplyGuildId(raw);
        Assert.Null(o.GuildId);
        Assert.False(string.IsNullOrEmpty(o.GuildIdError));
    }

    [Fact]
    public void Limits_are_clamped_to_safe_ranges()
    {
        var o = new DiscordOptions
        {
            MinRequestIntervalMs = 10,
            MaxApiCallsPerTool = 50,
            MaxApiCallsPer10Minutes = 10_000,
            MaxApiCallsPerDay = 1_000_000,
        };
        o.Normalize();
        Assert.Equal(1000, o.MinRequestIntervalMs);
        Assert.Equal(5, o.MaxApiCallsPerTool);
        Assert.Equal(120, o.MaxApiCallsPer10Minutes);
        Assert.Equal(1000, o.MaxApiCallsPerDay);
    }
}
