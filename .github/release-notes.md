**Windows x64, self-contained:** no .NET install needed. Česky níže.

1. Download `discord-mcp-{version}-win-x64.zip`, extract it to a folder of your choice, for example `C:\Tools\discord-mcp`.
2. Verify that the exe was built by this repository's release workflow (needs the [GitHub CLI](https://cli.github.com)):
   ```powershell
   gh attestation verify C:\Tools\discord-mcp\DiscordMcp.exe --repo {repo}
   ```
3. Continue with [Install](https://github.com/{repo}#install) from step 2: store the token, find the server ID, add the
   server to Claude Desktop.

**Updating:** quit Claude Desktop, replace `DiscordMcp.exe`, start it again. The stored token and state stay where
they are.

Read [Risks](https://github.com/{repo}#-risks) before you use it: it signs in with your user token, which Discord's
Terms of Service do not allow.

### Česky

1. Stáhni `discord-mcp-{version}-win-x64.zip` a rozbal ho do libovolné složky, třeba `C:\Tools\discord-mcp`.
2. Ověř, že exe postavil release workflow tohohle repa (potřebuje [GitHub CLI](https://cli.github.com)):
   `gh attestation verify C:\Tools\discord-mcp\DiscordMcp.exe --repo {repo}`
3. Pokračuj [instalací](https://github.com/{repo}/blob/main/README.cs.md#instalace) od kroku 2.

**Aktualizace:** ukonči Claude Desktop, nahraď `DiscordMcp.exe` a spusť ho znovu. Uložený token i stav zůstanou.

Před použitím si přečti [Rizika](https://github.com/{repo}/blob/main/README.cs.md#-rizika).
