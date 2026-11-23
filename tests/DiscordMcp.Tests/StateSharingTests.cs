using DiscordMcp.Http;

namespace DiscordMcp.Tests;

/// <summary>The CLI and the running server are separate processes sharing state.json.</summary>
public class StateSharingTests
{
    [Fact]
    public void A_hard_stop_cleared_by_another_process_is_seen_without_a_restart()
    {
        using var dir = new TempDir();
        var server = new StateStore(dir.Paths);
        var cli = new StateStore(dir.Paths);

        server.Update(s => s.HardStopReason = "token odmítnut (401)");
        Assert.Equal("token odmítnut (401)", cli.Read(s => s.HardStopReason));

        // Make sure the file time moves even on coarse timestamp resolution.
        Thread.Sleep(20);
        cli.Update(s => s.HardStopReason = null);
        File.SetLastWriteTimeUtc(dir.Paths.StateFile, DateTime.UtcNow.AddSeconds(1));

        Assert.Null(server.Read(s => s.HardStopReason));
    }

    [Fact]
    public void Calls_recorded_by_another_process_count_toward_the_same_budget()
    {
        using var dir = new TempDir();
        var server = new StateStore(dir.Paths);
        var cli = new StateStore(dir.Paths);
        var now = DateTimeOffset.UtcNow;

        server.Update(s => s.RecentCalls.Add(now));
        Thread.Sleep(20);
        cli.Update(s => s.RecentCalls.Add(now.AddSeconds(1)));
        File.SetLastWriteTimeUtc(dir.Paths.StateFile, DateTime.UtcNow.AddSeconds(1));

        Assert.Equal(2, server.Read(s => s.RecentCalls.Count));
    }
}
