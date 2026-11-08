# osu-twitch-commands

A [Streamer.bot](https://streamer.bot) extension that adds a complete set of **osu! chat commands** to a Twitch channel:

- live beatmap info, PP estimates at any accuracy, and the current skin with an automatic download link (local data via [tosu](https://github.com/tosuapp/tosu));
- player statistics, best scores and recent plays (via the [osu! API v2](https://osu.ppy.sh/docs/index.html));
- automatic announcements of the PP gained during the stream.

Everything runs inside Streamer.bot: no external server, no database, no account credentials beyond a read-only osu! API token.

---

## Contents

- [Commands](#commands)
- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Installation](#installation)
  - [1. Get the files](#1-get-the-files)
  - [2. Install the skin-export helper](#2-install-the-skin-export-helper)
  - [3. Import the commands](#3-import-the-commands)
  - [4. Create the osu! API credentials](#4-create-the-osu-api-credentials)
  - [5. Configure tosu](#5-configure-tosu)
  - [6. Test everything](#6-test-everything)
- [Configuration](#configuration)
- [Repository layout](#repository-layout)
- [Rebuilding the helper](#rebuilding-the-helper)
- [Troubleshooting](#troubleshooting)
- [License](#license)

---

## Commands

| Command | Example output | Data source |
| --- | --- | --- |
| `!np` | `senya - Sakuretsu Irony [Satellite] [7.66★] +HDDT \| 228 bpm - 1953x \| 98%: 565.47pp, 99%: 601.25pp, 100%: 643.79pp \| DL: osu.ppy.sh/b/4838687` | tosu |
| `!acc <0-100>` | `Kommisar - Chipstream [Challenge] [6.86★] +HD \| 98%: 300.23pp` | tosu |
| `!pp <pp>` | `senya - Sakuretsu Irony [Satellite] [7.66★] \| 98.80%: 600.02pp` | tosu |
| `!skin` | `Current Skin: Amaestric \| DL: https://x0.at/63nu.osk` | tosu + helper |
| `!stats <username>` | `Player: dt_gamesplays [🇮🇹 IT] \| Acc: 98.83% \| PP: 10745pp \| Rank: #5190 \| Level: 102 \| Game Time: 1994.47h \| Join Date: 06/01/2019 14:29:30 \| PL: osu.ppy.sh/u/13716488` | osu! API v2 |
| `!top <username> [n]` | `senya - Sakuretsu Irony [Satellite] [7.66★] +HDDT \| 1952x / 1953x - 99.50%: 614.05pp \| DL: osu.ppy.sh/b/4838687` | osu! API v2 |
| `!recent <username>` | `Toby Fox - Megalovania [Another] [4.20★] \| DL: osu.ppy.sh/b/777` | osu! API v2 |
| `!best <username>` | `Player: dt_gamesplays \| Best Rank: #3889 \|  Best Acc: 98.89%` | osu! API v2 + [osutrack](https://osutrack-api.ameo.dev) |
| *PP Gain* (timed, every 15 s) | `+10pp! Total: 15000pp` | tosu |

Notes:

- `%starRating%` is the **modded** star rating: computed by tosu while playing and by the osu! beatmap-attributes endpoint for stored scores.
- `%mods%` is empty when no mod is active (`NM` is never shown), otherwise it contains the leading space and plus (`" +HDDT"`).
- `!pp` accuracy has a granularity of ~0.05–0.2 PP because tosu calculates PP per integer hit count.
- `!recent` only sees the **last 24 hours** (osu! API behaviour): "No recent play found." is a valid answer.

---

## How it works

All commands are Streamer.bot actions in the group `Commands: Osu!`. Each chat command is an action with two sub-actions:

1. an **inline C#** sub-action that fetches the data, formats it and sets Streamer.bot arguments;
2. a **Twitch reply** sub-action that answers the originating message using those arguments.

Data sources:

- **tosu** (`http://127.0.0.1:24050`) — reads osu! memory (stable and lazer).
  - `GET /json/v2` for the live beatmap, play, mods and PP values;
  - `GET /api/calculate/pp?mode=0&mods=<bitset>&acc=<acc>` for PP estimates.
- **osu! API v2** (`https://osu.ppy.sh/api/v2`) — OAuth2 **client credentials** with the `public` scope.
  - the access token is requested once per action per process and cached until 5 minutes before `expires_in`;
  - `GET /users/@{username}/osu` for profile data and the numeric user id;
  - `GET /users/{id}/scores/best?mode=osu&limit=n` and `/scores/recent?mode=osu&limit=1`;
  - `POST /beatmaps/{id}/attributes` with the score mods for the modded star rating and max combo.
- **osutrack** (`https://osutrack-api.ameo.dev/peak`) — historical peak global rank for `!best`.

### `!skin`

The command builds a `.osk` archive of the skin that is currently selected in game and publishes it, then replies with a direct download link.

- **osu!stable**: tosu reports `folders.skin`; the folder is zipped as-is (files at the archive root, no wrapper folder).
- **osu!lazer**: skins are stored inside `client.realm`, so the bundled helper `osu-skin-export.exe` reads the Realm database, resolves the file hashes and writes the archive.
- The link is cached in a text file named after the skin:
  - stable: `<osu! installation>\Links\<skin name>.txt`
  - lazer: `<lazer data folder>\Links\<skin name>.txt`
- If the cached link is younger than 6 hours it is reused as-is; after that a cheap HTTP range request validates it.
- Uploads are anonymous: [x0.at](https://x0.at) first, [catbox.moe](https://catbox.moe) as fallback. x0.at keeps the original file name (`Amaestric R & B with Labels.osk`), catbox assigns a random id.
- Built-in lazer skins (argon, triangles, classic, retro) contain no files in `client.realm` and cannot be exported.

### PP Gain

A Timed Action (`PP Tracker`, every 15 s) compares the current PP (from tosu) with the persisted global `osuPpBase`. When the increase is at least 0.05 PP it announces the gain; after a restart the baseline is realigned silently, so no stale PP are announced.

---

## Requirements

| Requirement | Needed for |
| --- | --- |
| Windows x64 | everything |
| Streamer.bot **1.0.7** or newer (the export requires at least `1.0.0-alpha.1`) | everything |
| Twitch account connected in Streamer.bot (broadcaster + bot) | chat replies |
| [tosu](https://github.com/tosuapp/tosu) running, HTTP server on `127.0.0.1:24050` | `!np`, `!acc`, `!pp`, `!skin`, PP Gain |
| osu! running (stable **or** lazer) | tosu |
| osu! account with an OAuth application (read-only, `public` scope) | `!stats`, `!top`, `!recent`, `!best` |
| [.NET 8 Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0) | lazer skin export helper only |

Nothing else has to be installed: Streamer.bot ships the JSON library used by the actions.

---

## Installation

### 1. Get the files

```powershell
git clone https://github.com/DavPointer/osu-twitch-commands.git
```

Or download the ZIP from the GitHub page and extract it anywhere.

### 2. Install the skin-export helper

The helper is a small .NET 8 console application that exports osu!lazer skins out of `client.realm`.

Copy the whole `data\osu-skin` folder into the Streamer.bot **data** folder:

```
<Streamer.bot>\data\osu-skin\osu-skin-export.exe
<Streamer.bot>\data\osu-skin\osu-skin-export.dll
<Streamer.bot>\data\osu-skin\osu-skin-export.deps.json
<Streamer.bot>\data\osu-skin\osu-skin-export.runtimeconfig.json
<Streamer.bot>\data\osu-skin\Realm.dll
<Streamer.bot>\data\osu-skin\realm-wrappers.dll
<Streamer.bot>\data\osu-skin\MongoDB.Bson.dll
<Streamer.bot>\data\osu-skin\Remotion.Linq.dll
```

With a default installation the final path is `C:\Program Files\Streamer.bot\data\osu-skin`.

The actions locate the helper through Streamer.bot's own base directory, so the sub-folder **must** be named `osu-skin`. If you use osu!stable only, you can skip this step: the stable path zips the skin folder directly with no helper.

Quick check from PowerShell (replace the data folder with your lazer storage folder):

```powershell
& '<Streamer.bot>\data\osu-skin\osu-skin-export.exe' --root 'E:\osu-lazer' --out "$env:TEMP\test.osk"
```

It prints a single JSON line such as
`{"ok":true,"skin":"Amaestric","path":"...","entries":337,"bytes":16612192,"skipped":0}`.
`{"ok":false,...}` means something is wrong (missing Realm database, unknown skin, missing .NET 8 runtime, ...).

### 3. Import the commands

1. Close any running Streamer.bot instance is **not** required, but importing into a clean profile is simpler.
2. In Streamer.bot open **Integrations → Import** (or drag the file onto the window).
3. Select `osu-twitch-commands-v1.0.0.sb` and confirm the import.
4. Open the **Commands** tab and **enable** the eight commands (`!np`, `!skin`, `!acc`, `!best`, `!stats`, `!recent`, `!pp`, `!top`). The export ships them disabled to avoid clashing with existing command names.
5. Open the **Actions** tab and **enable** the `PP Gain` action if you want the automatic PP announcements.
6. Make sure the `PP Tracker` timed action is enabled (**Servers/Timers → Timed Actions**, interval 15 s, repeat).

All nine actions land in the action group `Commands: Osu!`.

> If a command name already exists in your profile, Streamer.bot will refuse to import it. Rename or remove the existing command first, then import again.

### 4. Create the osu! API credentials

`!stats`, `!top`, `!recent` and `!best` use the osu! API v2 with the **client credentials** grant: a read-only guest token, no user login, no access to private data.

1. Open <https://osu.ppy.sh/home/account/edit#oauth> and click **New OAuth Application**.
2. Fill in:
   - **Application Name**: anything, e.g. `Streamer.bot`;
   - **Application URL**: `http://localhost`;
   - **Callback URL**: `http://localhost` (unused by this grant, but required by the form).
3. Save and copy the **Client ID** and the **Client Secret**.
4. In Streamer.bot open the **Globals** tab and create two variables:

| Name | Type | Persisted | Value |
| --- | --- | --- | --- |
| `osuClientId` | `string` | yes | your client id |
| `osuClientSecret` | `string` | yes | your client secret |

The secret stays in Streamer.bot's `data\globals.db`; it is never logged and never sent anywhere except `https://osu.ppy.sh/oauth/token`.

Notes:

- The token lives 24 hours and is refreshed automatically; the actions cache it in memory until 5 minutes before expiry.
- Without these globals the four API commands reply `osu! API is not configured.` and log
  `osu! OAuth client is not configured: set the osuClientId and osuClientSecret globals.`
- Respect the [rate limit](https://osu.ppy.sh/docs/index.html) of ~60 requests per minute. Each command uses 1–3 requests, so a busy chat is fine.

### 5. Configure tosu

1. Install [tosu](https://github.com/tosuapp/tosu) and start it while osu! is running.
2. Leave the defaults: HTTP/WebSocket server on port **24050**.
3. Verify with a browser or PowerShell:

```powershell
Invoke-RestMethod http://127.0.0.1:24050/json/v2
```

You should get a JSON object with `folders`, `beatmap`, `play`, `performance`, `settings` and `profile` sections. The commands read `folders.game`, `folders.skin`, `folders.songs`, `beatmap.*`, `play.mods.*`, `performance.*` and `settings.skin.name`.

tosu works with both osu!stable and osu!lazer; only the skin export path differs (see [`!skin`](#skin)).

### 6. Test everything

Start osu!, tosu and Streamer.bot, connect the Twitch bot, then send the commands in your own chat:

| Command | Expected |
| --- | --- |
| `!np` | current beatmap, stars, mods, bpm, combo, 98/99/100% PP, link — or `No beatmap is currently playing.` |
| `!acc 98` | PP for 98% accuracy |
| `!pp 300` | accuracy needed for 300 PP |
| `!skin` | skin name and a download link (the first run can take ~10 s while the archive is built and uploaded) |
| `!stats dt_gamesplays` | profile summary |
| `!top dt_gamesplays 3` | third best score |
| `!recent dt_gamesplays` | last play of the day |
| `!best dt_gamesplays` | peak global rank |
| PP Gain | automatic message after your PP increase |

Check Streamer.bot's **Logs** tab (or `logs\log_YYYYMMDD.log`) for `CSxxxx` errors: the inline C# is pre-compiled at startup and any compile error is reported there.

---

## Configuration

Everything is tuned inside the inline C# sub-actions; nothing else has to be configured.

| Constant | Where | Default | Meaning |
| --- | --- | --- | --- |
| tosu URL | `!np`, `!acc`, `!pp`, `!skin`, `PP Gain` | `http://127.0.0.1:24050` | change both the `/json/v2` and `/api/calculate/pp` base |
| `TokenUrl` / `ApiBase` | `!stats`, `!top`, `!recent`, `!best` | osu! endpoints | only change if osu! moves them |
| `LinkCheckHours` | `!skin` | `6` | how long a cached skin link is trusted before a range check |
| upload chain | `!skin` | x0.at → catbox | anonymous hosting, no account |
| interval | `PP Tracker` timer | `15` s | PP Gain polling frequency |
| `MinGain` | `PP Gain` | `0.05` | smallest PP increase worth announcing |

Global variables used:

| Name | Persisted | Written by | Purpose |
| --- | --- | --- | --- |
| `osuClientId` | yes | you | OAuth client id |
| `osuClientSecret` | yes | you | OAuth client secret |
| `osuPpBase` | yes | PP Gain | last announced PP total |
| `osuPpSession` | no | PP Gain | per-process marker, avoids announcing after a restart |

---

## Repository layout

```
osu-twitch-commands/
├── osu-twitch-commands-v1.0.0.sb   Streamer.bot export (9 actions, 8 commands, 1 timer)
├── data/osu-skin/                  helper binaries -> copy into <Streamer.bot>\data\osu-skin
└── src/
    ├── actions/                    C# sources embedded in the export (for reading and edits)
    │   ├── np.cs  acc.cs  pp.cs  skin.cs  pp-gain.cs
    │   └── best.cs  stats.cs  recent.cs  top.cs
    └── helper/                     source of osu-skin-export (net8.0, Realm 20.1.0)
```

The `.sb` file is a gzip-compressed JSON container: header `SBAE`, then the zlib stream, then a JSON with `meta` and `data` (`actions`, `commands`, `timers`, `queues`, ...). The sources in `src/actions` are the exact C# embedded in it; editing them does **not** change the export automatically.

---

## Rebuilding the helper

```powershell
cd src\helper
dotnet publish -c Release -r win-x64 --self-contained false -o ..\..\data\osu-skin
```

The folder must contain `osu-skin-export.exe` plus the managed dependencies (`Realm.dll`, `realm-wrappers.dll`, `MongoDB.Bson.dll`, `Remotion.Linq.dll`, `.deps.json`, `.runtimeconfig.json`). A framework-dependent build requires the .NET 8 Runtime on the target machine; use `--self-contained true` if you prefer a large, dependency-free build.

The export uses [Realm .NET 20.1.0](https://github.com/realm/realm-dotnet) to read `client.realm` in read-only, dynamic mode, so it also works while osu!lazer is running.

---

## Troubleshooting

| Message / symptom | Cause | Fix |
| --- | --- | --- |
| `osu! API is not configured.` | `osuClientId` / `osuClientSecret` missing | create the persisted globals (step 4) |
| `Failed to fetch data from osu!.` | invalid credentials, network error, rate limit | check the log: `osu! OAuth token request failed: ...` |
| `osu! player not found.` | wrong username | use the exact osu! username |
| `osu! profile data is not available.` | account with no statistics | try another account |
| `No recent play found.` | no play in the last 24 h | this is osu! API behaviour |
| `No beatmap is currently playing.` | tosu idle / in the menus | start a beatmap |
| `Failed to fetch data from tosu.` | tosu not running or wrong port | start tosu, check `http://127.0.0.1:24050/json/v2` |
| `Skin not found.` | tosu reports a skin folder that does not exist | check `folders.skin` / `settings.skin.name` |
| `Could not create the .osk file.` | lazer export failed | check the log line `!skin: lazer export failed: ...`, the helper files and the .NET 8 runtime |
| `Skin upload failed.` | both x0.at and catbox rejected the file | check the log, retry later or use a smaller skin |
| `PP calculation failed, try again.` | tosu returned an invalid payload | retry while a beatmap is playing |
| Skin link points to an old version | the cache is keyed by skin name | edit the skin, or delete `Links\<skin name>.txt` |
| Commands imported but silent | they are imported **disabled** | enable them in the Commands tab |
| Embedded C# errors at startup (`CSxxxx`) | modified sources / missing reference | restore the original `.sb` or recompile the sources |
| Helper window flashes / no output | .NET 8 runtime missing | install the x64 .NET 8 Runtime |

---

## License

Licensed under the [Apache License 2.0](LICENSE).

Bundled third-party components:

- [Realm .NET](https://github.com/realm/realm-dotnet) — Apache 2.0
- [MongoDB.Bson](https://github.com/mongodb/mongo-csharp-driver) — Apache 2.0
- [Remotion.Linq](https://github.com/remotion/remotion) — Apache 2.0

Streamer.bot, osu!, tosu, osutrack, x0.at and catbox.moe are not affiliated with this project; use them according to their own terms.
