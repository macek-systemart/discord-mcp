# Contributing

Thanks for helping. A few rules keep this project safe for the people who run it against their own account.

## Ground rules

- **Read-only, always.** No feature may send, edit, react, join or open a gateway connection. The HTTP guards
  (`src/DiscordMcp/Http/Guards.cs`) allow only body-less GET requests to an allowlist of routes. Widen that list only
  for a read endpoint, and explain why in the PR.
- **Tests never touch the real Discord.** Use the fakes (`FakeHandler`, a fake `IDiscordApi`, `FakeTimeProvider`) and
  the synthetic fixtures in `tests/DiscordMcp.Tests/Fixtures`. Never paste real API payloads into fixtures: they carry
  other people's messages and signed CDN URLs.
- **Never log or print** the token, request headers, full URLs with query strings or signed CDN URLs.
- **Every user-visible text goes through `Localization`** (see below). Tool descriptions and server instructions are
  for the model and stay in English.
- **Keep it small.** Match the existing style: file-scoped namespaces, primary constructors, `ConfigureAwait(false)`
  in library code, XML docs on public types. The design contract is [`docs/IMPLEMENTATION.md`](docs/IMPLEMENTATION.md).

## Build and test

```powershell
dotnet build DiscordMcp.slnx
dotnet test DiscordMcp.slnx
```

Tests pin Czech output and the `Europe/Prague` time zone themselves, so they pass on any machine.

## Adding a language

Texts live in `src/DiscordMcp/Localization`:

| File | Role |
|---|---|
| `Texts.cs` | Abstract class with one member per text: a property, or a method with typed parameters. |
| `English.cs`, `Czech.cs` | The implementations. |
| `Languages.cs` | The registry of available languages. |

To add, say, German:

1. **Create the class.** Copy `English.cs` to `German.cs` and rename the class.
2. **Set the codes.** Set `Code` to the two-letter ISO 639-1 code (`de`) and give the language its own self marker,
   for example `[ICH]`, plus a neutralized form of it.
3. **Translate.** Translate every string. Keep the cursor tokens `offset=`, `before=` and `after=`, IDs, numbers and
   dates exactly as they are; only the words around them change.
4. **Register it** with one line in `Languages.cs`.
5. **Run `dotnet test`.** The compiler already refuses a class that misses a text. The parity test also checks that
   every text is non-empty and has no leftover placeholders.

Users pick the language with `Discord__Language=de`. Without that setting it is chosen from the Windows display
language.

## Pull requests

- One topic per PR, with tests for the behaviour you change.
- `dotnet test` must be green.
- For anything security-relevant, describe the threat you considered: sanitizer, guards, child processes, token
  handling.

## Releasing

Releases are built by [`release.yml`](.github/workflows/release.yml) on a GitHub Windows runner, never on a
maintainer's machine.

1. **Dry run.** Actions → **Release** → **Run workflow** on `main`. It runs the tests, publishes the self-contained
   single-file exe and smoke-tests it offline with [`smoke-test.ps1`](.github/scripts/smoke-test.ps1): usage text,
   both parser child processes and an MCP session, with an empty data folder so nothing can reach Discord. It does not
   create a release.
2. **Version.** Set `<Version>` in `Directory.Build.props` to the new version, so local builds report it too.
3. **Tag.** `git tag v1.2.3` on that commit and `git push origin v1.2.3`. A tag with a suffix, such as `v1.2.3-rc.1`,
   becomes a prerelease. The workflow runs the same steps, then attests build provenance for the zip and the exe and
   creates the GitHub release with the zip and its `.sha256`. The release text comes from
   [`.github/release-notes.md`](.github/release-notes.md), followed by notes generated from the commits.
4. **Dependencies.** When a bundled package or its version changes, update `THIRD-PARTY-NOTICES.txt`. It ships in
   the zip.
