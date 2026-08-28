# CS2SimpleVote

A lightweight, Workshop-collection–driven map voting plugin for Counter-Strike 2 ([CounterStrikeSharp](https://docs.cssharp.dev/)). RTV, nominations, scheduled votes, and live map management — no database, no fuss.

<p align="left">
  <img alt="version" src="https://img.shields.io/badge/version-1.5.0-blue">
  <img alt="platform" src="https://img.shields.io/badge/CS2-CounterStrikeSharp-orange">
</p>

---

## ✨ Features

- **Three map sources, one pool** — the Steam Workshop collection (`collection_maps.json`), manually added workshop maps (`workshop_maps.json`), and official stock maps (`stock_maps.json`) all merge into the same vote pool.
- **Enable/disable per map** — every map is one readable line with an `enabled` flag. Flip it to omit or include the map; that IS the omit system.
- **Realtime file edits** — all three files are re-read before every vote, nomination, and menu. Edit a flag mid-map and the very next vote honors it.
- **Auto-synced membership** — the collection file tracks the Steam collection (adds/removals/titles), the stock file tracks the server engine's own map list (seasonal rotations included). Only your `enabled` flags are yours to manage.
- **Rock the Vote (RTV)** — player-driven votes with a configurable ratio; threshold re-checks when players leave so votes never get stuck.
- **Nominations** — paged, searchable map nominations with per-player tracking.
- **Scheduled + forced votes** — auto-vote on a set round, or trigger votes/RTVs on demand as an admin.
- **Extend option** — optionally adds `[0] Extend Current Map` to every vote (`0` or `!0` to cast); if it wins, the next map is the current one.
- **Round-based or timed votes** — scheduled votes stay open for N rounds, or flip one flag to run them on a seconds timer instead.
- **Center-screen vote HUD** — optional live progress panel (options + running tallies) rendered natively, no resource plugins. Round-based votes flash it for 10s at each round end and at the vote's conclusion; timed votes keep it up all vote with a countdown.
- **Recent-map exclusion** — the last `recent_maps_count` played maps are kept out of votes entirely: the random pool *and* nominations. Works for workshop and stock maps alike.
- **Live config reload** — edit `CS2SimpleVote.json` and it applies on the next map change, no restart.
- **Hidden commands** — every command is intercepted silently and never spammed to public chat.

---

## 💬 Chat Commands

| Command | Description |
|---|---|
| `!rtv` | Rock the Vote to change the map |
| `!nominate [name]` / `!nom [name]` | Nominate a map (opens a picker if multiple match) |
| `!nominatelist` | Show currently nominated maps |
| `!revote` | Re-display vote options and recast |
| `!nextmap` | Show the next map |
| `!lastmap` | Show the previously played map |
| `!recentmaps [n]` | Show recently played maps |
| `!maplist` / `!maps` | Print the full map list to your console |
| `!help` | List available commands |

> Both `!` and `/` prefixes work, and commands are hidden from other players.
> During a vote, type the option's **number** to cast your vote — including `0` (or `!0`) for **Extend Current Map** when the extend option is enabled.

---

## 🛡️ Admin Commands

Requires the player's SteamID64 in the `admins` config list.

| Command | Description |
|---|---|
| `!addmap [workshop ID/URL]` | Add a workshop map to `workshop_maps.json` (or re-enable it if disabled) |
| `!addlist` | List manually added workshop maps |
| `!omitmap [words]` | Disable matching maps (`"enabled": false` in their file) |
| `!unomitmap [words]` | Re-enable matching maps |
| `!omitlist` | List all currently disabled maps across the three files |
| `!forcemap [name]` | Immediately change to a map (sees disabled maps too) |
| `!setnextmap [name]` | Set the next map directly (no vote; sees disabled maps too) |
| `!forcevote` | Force-start a map vote |
| `!forcertv` | Start an RTV-style vote (map changes when it ends) |
| `!finishvote` | End an active vote early |
| `!endwarmup` | End the current warmup |
| `!votedebug` | Show plugin state / diagnostics |

### `!omitmap` examples
```
!omitmap motel night     → disables "Motel at Night", "Night Motel v2", ...
!omitmap de_dust2        → disables the stock map de_dust2
!omitmap 3070321328      → disables that exact workshop ID
```
Omitting a map just sets `"enabled": false` on its line in `collection_maps.json` / `workshop_maps.json` / `stock_maps.json` — the same thing you can do by hand in the file. Matching is **case-insensitive**, **ignores word order**, and toggles **all matches**. `!unomitmap`, `!addmap`, or a hand edit turns a map back on.

---

## 🖥️ Console Commands

Run from the **server console**:

| Command | Description |
|---|---|
| `css_addmap <id/url>` | Add or re-enable a workshop map (`workshop_maps.json`) |
| `css_addlist` | List manually added workshop maps |
| `css_omitmap <words>` | Disable matching maps |
| `css_unomitmap <words>` | Re-enable matching maps |
| `css_setnextmap <name>` | Set the next map |
| `css_forcemap <name>` | Force a map change |
| `css_forcertv` | Start an RTV-style vote |
| `css_dumpmaps` | Dump all enabled map names + IDs to console |

---

## ⚙️ Configuration

Config lives at `addons/counterstrikesharp/configs/plugins/CS2SimpleVote/CS2SimpleVote.json`.

| Key | Default | Description |
|---|---|---|
| `steam_api_key` | `"YOUR_STEAM_API_KEY_HERE"` | Steam Web API key (required for collection + `!addmap`) |
| `collection_id` | `"123456789"` | Workshop collection ID to load maps from |
| `vote_on_round` | `5` | Round number that triggers the scheduled vote (`0` disables) |
| `enable_rtv` | `true` | Enable Rock the Vote |
| `rtv_ratio` | `0.50` | Fraction of players needed to trigger RTV (0–1] |
| `rtv_change_delay` | `5.0` | Seconds before an RTV winner loads |
| `enable_nominate` | `true` | Enable nominations |
| `nominate_per_page` | `8` | Maps shown per nomination page |
| `vote_options_count` | `5` | Number of maps in a vote (2–10) |
| `vote_open_for_rounds` | `3` | How many rounds a round-based scheduled vote stays open |
| `enable_extend_vote` | `false` | Add `[0] Extend Current Map` to votes (`0` / `!0` to cast); winning sets the next map to the current one |
| `enable_vote_hud` | `false` | Live center-screen progress panel; fully replaces the `VOTE NOW!` prompt |
| `enable_timed_vote` | `false` | Scheduled/forced votes run on a seconds timer instead of rounds |
| `timed_vote_seconds` | `60.0` | Timed vote duration in seconds (10–600; RTV votes are always 30s) |
| `show_midvote_progress` | `true` | Show running tallies in chat during a vote |
| `vote_reminder_enabled` | `true` | Periodically remind players to vote |
| `vote_reminder_interval` | `30.0` | Seconds between vote reminders |
| `postmap_change_delay` | `10.0` | Seconds before an end-of-match winner loads |
| `omit_recent_maps` | `true` | Keep recently played maps out of the pool |
| `recent_maps_count` | `10` | How many recent maps to remember/exclude |
| `enable_map_message` | `true` | Periodically announce the current map |
| `map_message_interval` | `300.0` | Seconds between current-map messages |
| `server_name` | `"My CS2 Server"` | Name shown in the current-map message |
| `collection_refresh_minutes` | `30.0` | Minutes between collection refreshes (`0` = once at load, min 1) |
| `admins` | `[]` | List of admin SteamID64s |

### Vote HUD (`enable_vote_hud`)

A live progress panel in the center of the screen (styled after [cs2-rockthevote](https://github.com/Oz-Lin/cs2-rockthevote)'s vote panel), rendered natively with `PrintToCenterHtml` — nothing external to install:

```
Vote for the Next Map!
Time remaining: 42s        ← timed votes only
!1 Dust II (3)
!2 Mirage (1)
!0 Extend Current Map (0)  ← when enable_extend_vote is on
```

While the HUD is enabled the plain `VOTE NOW!` center prompt is never shown. Visibility depends on the vote style:

- **Round-based votes** (`enable_timed_vote: false`): the panel is not up the whole vote — it appears for **10 seconds at the end of every round** the vote is open, and for **10 seconds when the vote concludes** (final tally + winner).
- **Timed votes** (`enable_timed_vote: true`, and all RTV votes): the panel stays up for the **entire vote** with a live seconds countdown at the top, then shows the 10-second conclusion panel.

### Example `CS2SimpleVote.json`
```json
{
  "steam_api_key": "XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX",
  "collection_id": "3070781055",
  "vote_on_round": 10,
  "enable_rtv": true,
  "rtv_ratio": 0.60,
  "vote_options_count": 8,
  "omit_recent_maps": true,
  "recent_maps_count": 5,
  "server_name": "My CS2 Server",
  "collection_refresh_minutes": 30.0,
  "admins": [76561198000000000, 76561198000000001]
}
```

---

## 📂 Data Files

Generated next to the config, all hand-editable:

| File | Purpose |
|---|---|
| `collection_maps.json` | Steam collection maps — membership/titles auto-synced, you own the `enabled` flags |
| `workshop_maps.json` | Manually added workshop maps — yours entirely (`!addmap` writes here) |
| `stock_maps.json` | Official stock maps — auto-synced from the server engine, you own the `enabled` flags |
| `recent_maps.json` | Recently played map history |
| `logs/YYYY-MM-DD/events.log` | Per-day event log (votes, RTVs, admin actions, map changes) |

All three map files share the same readable one-entry-per-line format and can be **edited live** — changes are re-read before every vote, nomination, and admin menu, no reload or map change required.

### Collection maps (`collection_maps.json`)

Auto-generated from your Steam Workshop collection and kept in sync on every refresh (`collection_refresh_minutes`): new collection maps appear **enabled**, removed maps are pruned, titles follow Steam. Set `"enabled": false` on a line to omit that map from votes without touching the collection.

```json
[
  { "id": "3070321328", "title": "Dust2 Remake", "enabled": true },
  { "id": "3121217565", "title": "Motel at Night", "enabled": false }
]
```

### Workshop maps (`workshop_maps.json`)

Your personal additions outside the collection — same format as above. Add lines by hand or via `!addmap`; a line with just an `"id"` works immediately (the title auto-fills from Steam on the next refresh). Deleting the file clears your manual list; nothing here is ever pruned automatically.

### Stock maps (`stock_maps.json`)

Generated straight from the server engine — map names from the `.vpk` files in the game's `maps/` folder, display titles from the server's own localization (`SFUI_Map_*`). Nothing to curate by hand:

- **First run** writes every installed stock map, sorted by prefix (`ar_`, `cs_`, `de_`, ...) then alphabetically, all `"enabled": false`.
- **Seasonal rotations** are automatic: maps added by a game update appear (disabled) and removed maps are pruned, while your `enabled` edits are always preserved.
- Enabled stock maps behave exactly like workshop maps everywhere: votes, RTV, nominations, `!forcemap` / `!setnextmap` (searchable by title *or* map name, e.g. `!nominate de_dust2`), omitting, and the recent-map exclusion.

```json
[
  { "map": "ar_baggage", "title": "Baggage", "enabled": false },
  { "map": "cs_office", "title": "Office", "enabled": true },
  { "map": "de_dust2", "title": "Dust II", "enabled": true }
]
```

> **Upgrading from 1.4 or earlier?** The plugin migrates automatically on first load: `custom_maps.json` becomes `workshop_maps.json`, omit patterns from `omitted_maps.json` are applied as `"enabled": false` flags, and `map_cache.json` seeds `collection_maps.json`. The old files are kept as `.bak`.

---

## 🚀 Installation

1. Install [CounterStrikeSharp](https://docs.cssharp.dev/) on your server.
2. Drop the compiled plugin into `addons/counterstrikesharp/plugins/CS2SimpleVote/`.
3. Start the server once to generate the config, then set your `steam_api_key`, `collection_id`, and `admins`.
4. Reload the plugin or restart — done.

> A [Steam Web API key](https://steamcommunity.com/dev/apikey) is required to resolve the collection and look up maps for `!addmap`.
