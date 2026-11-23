using System.Security.Cryptography;
using System.Text.Json;

namespace DiscordMcp.Auth;

/// <summary>The user token plus the account it belongs to, captured when <c>set-token</c> verified it.</summary>
public sealed record StoredToken(string Token, ulong UserId, string Username);

public interface ITokenStore
{
    StoredToken? Load();

    void Save(StoredToken token);

    bool Clear();
}

/// <summary>
/// DPAPI (CurrentUser) protected file. Any process running as this Windows user can decrypt it; the file only keeps
/// the token out of plain-text configs, repos and other Windows installs (dual boot: run set-token on each system).
/// </summary>
public sealed class DpapiTokenStore(AppPaths paths) : ITokenStore
{
    private static readonly byte[] Entropy = "discord-mcp/token/v1"u8.ToArray();

    public StoredToken? Load()
    {
        if (!File.Exists(paths.TokenFile))
        {
            return null;
        }

        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(paths.TokenFile), Entropy, DataProtectionScope.CurrentUser);
            var token = JsonSerializer.Deserialize<StoredToken>(plain);
            return token is { Token.Length: > 0, UserId: > 0 } ? token : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            // Written by another Windows install or user, or corrupted: behave as if no token was stored.
            return null;
        }
    }

    public void Save(StoredToken token)
    {
        Directory.CreateDirectory(paths.BaseDirectory);
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(token), Entropy, DataProtectionScope.CurrentUser);
        var temp = paths.TokenFile + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, paths.TokenFile, overwrite: true);
    }

    public bool Clear()
    {
        if (!File.Exists(paths.TokenFile))
        {
            return false;
        }

        File.Delete(paths.TokenFile);
        return true;
    }
}

/// <summary>
/// The token used for outgoing requests: the stored one, or a candidate that <c>set-token</c> is verifying.
/// The stored token is read on every access (a small DPAPI file, at most about one request per second), so a running
/// MCP server uses a new <c>set-token</c> immediately; a cached old token would hit 401 again and re-latch the stop.
/// </summary>
public sealed class TokenHolder(ITokenStore store)
{
    private volatile string? _candidate;

    public StoredToken? Stored => store.Load();

    public string? Current => _candidate ?? Stored?.Token;

    public void UseCandidate(string token) => _candidate = token;
}
