namespace DiscordMcp;

/// <summary>Per-user runtime files. <c>DISCORD_MCP_HOME</c> overrides the base directory (used by tests).</summary>
public sealed class AppPaths
{
    public AppPaths(string? baseDirectory = null)
    {
        BaseDirectory = baseDirectory
            ?? Environment.GetEnvironmentVariable("DISCORD_MCP_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "discord-mcp");
    }

    public string BaseDirectory { get; }

    public string TokenFile => Path.Combine(BaseDirectory, "token.dat");

    public string StateFile => Path.Combine(BaseDirectory, "state.json");

    public string DownloadsDirectory => Path.Combine(BaseDirectory, "downloads");
}
