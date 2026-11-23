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
