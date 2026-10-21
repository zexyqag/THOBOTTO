# THOBOTTO

A Discord bot for one community server (or a few), run on your own machine: music through helper bots, voice commands understood on the server itself, an Activity inside voice channels, events, points and mischief, game roles and game-server boards, a hall of fame, quotes, a soundboard, emoji proposals, Wrapped recaps, moderation, a gate for newcomers, and an archive of everything that can be exported with the bot's own records.

Nothing said in voice leaves the machine: speech is turned into text locally (Whisper or Parakeet), and audio is never stored.

## What it does

Each module is switched on per server on the panel's Setup page.

| Area | Module | What it does |
|---|---|---|
| Music & voice | Music | Music in voice channels, played by helper bots: queue, playlists, lyrics, karaoke, vote to skip, Last.fm scrobbling |
| | Watching together | Watch videos together in a voice channel's Activity, in step and without ads |
| | Music quiz | A music quiz in the Activity: name the song a helper plays |
| | Voice commands | Say a helper's name and what to do; only members who opt in are listened to; a language model reads commands in members' own words |
| | Helper voices | Helpers speak in voice in their personality's voice: replies, questions, now playing, hello and goodbye |
| | Voice channels | Join a hub voice channel to get a channel of your own, named after what people play |
| Community | Events | Plan events, RSVPs and reminders; time polls; repeating game sessions |
| | Points | Points earned by being active, spent on mischief |
| | Games | Game roles, a role picker, and sessions linked to the game server board |
| | Game servers | Live status of game servers on a board channel |
| | Hall of fame | Messages many people react to are reposted to a showcase and earn a bonus |
| | Quotes | The quote book: memorable lines from messages and voice |
| | Sounds | A soundboard of the server's own, played by a helper; join sounds |
| | Emojis and stickers | Members propose emojis and stickers; votes decide, unused ones retire |
| | Wrapped | Music and Discord recaps, on demand, monthly and at the end of the year |
| | Mischief | `/rename`: anyone renames anyone (never themselves), with a reason, for points |
| Safety | Moderation | `/mod`: warnings, timeouts, kicks, bans and appeals through the bot, with a case book; AutoMod rules |
| | Gate | New members verify in a waiting room; raids and too-new accounts are held; a greeting when someone is let in |
| Records | Archive | Keeps every message, edit, deletion and reaction, and the server's channels, roles and members, for history and for moving platforms; exportable |

The **panel** is a web app the bot serves: each member sees their own servers and settings, moderators their tools, the bot's owner the whole bot. The **Activity** opens inside a Discord voice channel (the rocket menu) with music, watching together, the quiz, polls, the soundboard, Wrapped stories and live captions.

## What it needs

- A Discord application with a bot. In the Developer Portal, turn on the **Message Content** intent (the archive, quotes, the hall of fame), the **Server Members** intent (the gate, join sounds, greetings, forgetting a member's preferences when they leave) and the **Presence** intent (naming voice channels after games). The bot asks only for the intents it has been allowed.
- Helper bots: one more Discord application per helper. Music, voice commands and the soundboard are played by helpers, so a server needs at least one; their tokens are added on the panel's Helper bots page.
- PostgreSQL (the example runs 18).
- [Lavalink](https://lavalink.dev) 4 for music, the quiz and watching along, with the YouTube plugin (and LavaSrc for Spotify links).
- Optional: a [llama.cpp](https://github.com/ggml-org/llama.cpp) server for voice commands in members' own words; a [Kokoro](https://github.com/remsky/Kokoro-FastAPI) server for helper voices (Piper runs inside the image otherwise); the [yt-dlp token helper](https://github.com/Brainicism/bgutil-ytdlp-pot-provider) for watching YouTube together; a [Spotify token service](https://github.com/topi314/spotify-tokener) for Spotify links; an S3 bucket for the archive's attachments.

## Running it

The image is built for linux-x64 and published by the repository's CI as `ghcr.io/<owner>/thobotto`: `:X.Y.Z` and `:latest` for releases, `:main` for the newest commit, `:<branch>` for branches.

[compose.example.yaml](compose.example.yaml) runs the bot with PostgreSQL, Lavalink and the optional services. Copy it, then:

```sh
THOBOTTO_IMAGE=ghcr.io/<owner>/thobotto:latest DISCORD_TOKEN=... POSTGRES_PASSWORD=... docker compose up -d
```

Put a reverse proxy with WebSocket support in front of port 8080 under your own host name, then in the Developer Portal:

- OAuth2 → Redirects: `https://<your host>/signin-discord` (the panel's login; the bot reads its own address from it).
- Activities → Settings: enable Activities, with the URL mapping `/` → `<your host>/activity`.

Everything else is set on the panel by the bot's owner (the application's owner): helper bots, speech engine and model, the language model server, Last.fm, Spotify, YouTube sign-in, the voice relay.

Settings the bot reads from its environment, as `Section__Key`:

| Setting | What |
|---|---|
| `Discord__Token` | The bot's token (required) |
| `ConnectionStrings__Postgres` | An Npgsql connection string (required) |
| `Lavalink__BaseAddress`, `Lavalink__Passphrase` | Where Lavalink is, and its password |
| `Relay__Host` | The host name helpers' Lavalink traffic reaches the bot's voice relay on (the compose service name) |
| `Archive__Storage` | `none` (default), `folder` (`Archive__Folder`) or `s3` (`Archive__S3__ServiceUrl`, `Bucket`, `AccessKey`, `SecretKey`): where the archive keeps attachments and the server's images |
| `DataProtection__Directory` | Where the key that encrypts stored secrets lives (set in the image; keep the volume) |
| `Listening__Models` | Where speech models are kept (set in the image; keep the volume) |

A wrong or missing setting stops the bot at start with one line saying which.

Back up the `postgres` volume and the `keys` volume together: the keys decrypt the secrets the database holds. The `models` volume only saves a download.

To export a server's archive without Discord, with an empty folder mounted at `/export`:

```sh
docker exec thobotto dotnet THOBOTTO.dll export <server id> /export/<a new folder>
```

The export holds the server's snapshot, every message with its versions, reactions and attachments, the same in DiscordChatExporter's format, and the bot's own records (quotes, hall of fame, points, Wrapped, events, cases and more).

## Versions

Releases are tagged `vX.Y.Z` and get a GitHub Release with the changes since the one before. Before 1.0.0, a release may change storage and formats without a migration path from older ones; the bot refuses a database newer than itself, and backups from an older format aren't restored.

## Developing

.NET 10 SDK and a PostgreSQL server. `dotnet test THOBOTTO.slnx` runs the unit tests and, with `TEST_POSTGRES` set to a server it may create databases on, the database tests; `dotnet format THOBOTTO.slnx --verify-no-changes` is the style check CI runs. The Activity (`activity/`, TypeScript and Vite) and the game-server query tool (`gamedig/`) are built in the Dockerfile; `dotnet watch run --project src` runs the bot against user secrets for the token and the connection string.

## License

[GNU Affero General Public License v3.0](LICENSE).
