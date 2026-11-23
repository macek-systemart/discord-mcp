using System.Globalization;
using DiscordMcp.Localization;

namespace DiscordMcp;

/// <summary>
/// Limits that keep the footprint of a user-token client small. Values come from the <c>Discord</c> configuration
/// section; <see cref="Normalize"/> clamps them so a config typo can never turn the server into a crawler.
/// </summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";
    public const string GuildIdVariable = "DISCORD_GUILD_ID";

    /// <summary>The one guild this server exposes. Null when missing or invalid (see <see cref="GuildIdError"/>).</summary>
    public ulong? GuildId { get; set; }

    /// <summary>Why <see cref="GuildId"/> is null, as an actionable message for the user.</summary>
    public string? GuildIdError { get; set; }

    public int MinRequestIntervalMs { get; set; } = 1000;
    public int MaxApiCallsPerTool { get; set; } = 5;
    public int MaxApiCallsPer10Minutes { get; set; } = 60;
    public int MaxApiCallsPerDay { get; set; } = 300;
    public int MaxToolWaitSeconds { get; set; } = 20;
    public int MaxAutoRetryAfterSeconds { get; set; } = 5;
    public int BreakerThreshold { get; set; } = 5;
    public int BreakerWindowMinutes { get; set; } = 10;
    public int BreakerOpenMinutes { get; set; } = 10;
    public int GuildCacheTtlMinutes { get; set; } = 10;
    public int MessageCacheTtlSeconds { get; set; } = 120;
    public int MaxOutputChars { get; set; } = 24_000;
    public int MaxInlineImageBytes { get; set; } = 700_000; // base64 (×4/3) + text must stay under the 1 MB tool-result limit of Claude Desktop
    public long MaxAttachmentBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>Parses the raw <c>DISCORD_GUILD_ID</c> value. Exactly one snowflake is accepted.</summary>
    public void ApplyGuildId(string? raw)
    {
        GuildId = null;
        GuildIdError = null;
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            GuildIdError = Languages.Current.GuildIdMissing(GuildIdVariable);
            return;
        }

        if (value.IndexOfAny([',', ';', ' ', '\t']) >= 0)
        {
            GuildIdError = Languages.Current.GuildIdSingleOnly(GuildIdVariable);
            return;
        }

        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
        {
            GuildIdError = Languages.Current.GuildIdInvalid(GuildIdVariable);
            return;
        }

        GuildId = id;
    }

    /// <summary>Clamps every limit into a safe range. Lower bounds protect the account, upper bounds the user.</summary>
    public void Normalize()
    {
        MinRequestIntervalMs = Math.Clamp(MinRequestIntervalMs, 1000, 10_000);
        MaxApiCallsPerTool = Math.Clamp(MaxApiCallsPerTool, 1, 5);
        MaxApiCallsPer10Minutes = Math.Clamp(MaxApiCallsPer10Minutes, 1, 120);
        MaxApiCallsPerDay = Math.Clamp(MaxApiCallsPerDay, 1, 1000);
        MaxToolWaitSeconds = Math.Clamp(MaxToolWaitSeconds, 5, 60);
        MaxAutoRetryAfterSeconds = Math.Clamp(MaxAutoRetryAfterSeconds, 0, 10);
        BreakerThreshold = Math.Clamp(BreakerThreshold, 1, 20);
        BreakerWindowMinutes = Math.Clamp(BreakerWindowMinutes, 1, 60);
        BreakerOpenMinutes = Math.Clamp(BreakerOpenMinutes, 1, 120);
        GuildCacheTtlMinutes = Math.Clamp(GuildCacheTtlMinutes, 1, 60);
        MessageCacheTtlSeconds = Math.Clamp(MessageCacheTtlSeconds, 0, 600);
        MaxOutputChars = Math.Clamp(MaxOutputChars, 2_000, 100_000);
        MaxInlineImageBytes = Math.Clamp(MaxInlineImageBytes, 50_000, 750_000);
        MaxAttachmentBytes = Math.Clamp(MaxAttachmentBytes, 1024 * 1024, 100L * 1024 * 1024);
    }
}
