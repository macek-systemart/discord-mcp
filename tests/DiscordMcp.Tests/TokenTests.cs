using DiscordMcp.Auth;
using DiscordMcp.Cli;

namespace DiscordMcp.Tests;

public class TokenTests
{
    // Synthetic token in Discord's shape, split so secret scanners do not mistake it for a leaked one.
    private const string Valid = "MTIzNDU2Nzg5MDEyMzQ1Njc4" + "." + "GabcDe" + "." + "abcdefghijklmnopqrstuvwxyz0123456789AB";

    [Fact]
    public void Dpapi_store_round_trips_and_clears()
    {
        using var dir = new TempDir();
        var store = new DpapiTokenStore(dir.Paths);
        Assert.Null(store.Load());

        store.Save(new StoredToken(Valid, 42, "me"));
        Assert.Equal(new StoredToken(Valid, 42, "me"), store.Load());

        Assert.True(store.Clear());
        Assert.Null(store.Load());
        Assert.False(store.Clear());
    }

    [Fact]
    public void A_file_that_does_not_decrypt_reads_as_no_token()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Paths.TokenFile, [1, 2, 3, 4]);
        Assert.Null(new DpapiTokenStore(dir.Paths).Load());
    }

    [Fact]
    public void Stored_file_is_not_plain_text()
    {
        using var dir = new TempDir();
        new DpapiTokenStore(dir.Paths).Save(new StoredToken(Valid, 42, "me"));
        var bytes = File.ReadAllText(dir.Paths.TokenFile, System.Text.Encoding.Latin1);
        Assert.DoesNotContain(Valid, bytes, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Valid, Valid)]
    [InlineData("  " + Valid + "  ", Valid)]
    [InlineData("\"" + Valid + "\"", Valid)]
    [InlineData("'" + Valid + "'", Valid)]
    public void Pasted_tokens_are_trimmed(string raw, string expected)
    {
        Assert.True(Commands.TryNormalizeToken(raw, out var token, out _));
        Assert.Equal(expected, token);
    }

    [Theory]
    [InlineData("Bot " + Valid)]
    [InlineData("Bearer " + Valid)]
    [InlineData("abc def ghi jkl mno pqr stu vwx yz0 123")]
    [InlineData("short")]
    [InlineData("")]
    public void Wrong_tokens_are_rejected(string raw)
    {
        Assert.False(Commands.TryNormalizeToken(raw, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Candidate_token_wins_over_the_stored_one()
    {
        var holder = new TokenHolder(new MemoryTokenStore(new StoredToken("stored", 1, "me")));
        Assert.Equal("stored", holder.Current);
        holder.UseCandidate("candidate");
        Assert.Equal("candidate", holder.Current);
        Assert.Equal("stored", holder.Stored?.Token);
    }
}
