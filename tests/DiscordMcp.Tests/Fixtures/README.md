# Test fixtures

Synthetic payloads only — shaped after the documented Discord API v10 objects and the 2026-11-23 probe.
Never paste real API responses here (they contain other people's messages and signed CDN URLs).

Ids used across files:

| id | what |
|---|---|
| 111111111111111111 | guild (and its @everyone role) |
| 700000000000000001 | me (synthetic user `tester`) — member with role 500000000000000001 |
| 700000000000000009 | guild owner |
| 500000000000000001 / …002 | roles "Člen" / "Moderátor" (MANAGE_THREADS) |
| 300000000000000000 | category |
| 300000000000000001 | text channel (readable) |
| 300000000000000002 | voice channel with text chat |
| 300000000000000003 | forum |
| 300000000000000004 | announcement channel, @everyone denied READ_MESSAGE_HISTORY → view without history |
| 300000000000000005 | moderators only (hidden for me) |
| 300000000000000006 | obfuscated hidden channel (flag 1<<17) |
| 300000000000000007 | unknown channel type 20 |
| 300000000000000008 | NSFW text channel |
| 1300000000000000006 | message that started thread 1300000000000000006 |
| 310000000000000001 | thread announced by a type-18 message |
| 990000000000000000 | foreign guild (crosspost source) — must never be fetched |
