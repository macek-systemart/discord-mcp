using System.Text.Json;
using DiscordMcp.Discord;

namespace DiscordMcp.Tests;

/// <summary>Synthetic payloads under Fixtures/ (see Fixtures/README.md for the id map).</summary>
internal static class Fixture
{
    public const ulong GuildId = 111111111111111111;
    public const ulong MyUserId = 700000000000000001;
    public const ulong TextChannel = 300000000000000001;
    public const ulong VoiceChannel = 300000000000000002;
    public const ulong ForumChannel = 300000000000000003;
    public const ulong HistorylessChannel = 300000000000000004;
    public const ulong ModeratorChannel = 300000000000000005;
    public const ulong ObfuscatedChannel = 300000000000000006;
    public const ulong UnknownTypeChannel = 300000000000000007;
    public const ulong StartedThread = 1300000000000000006;

    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static T Load<T>(string name) =>
        JsonSerializer.Deserialize<T>(Text(name), DiscordJson.Options) ?? throw new InvalidOperationException(name);
}
