# CS2SimpleVote

A lightweight, Workshop-collection–driven map voting plugin for Counter-Strike 2 ([CounterStrikeSharp](https://docs.cssharp.dev/)). RTV, nominations, scheduled votes, and live map management — no database, no fuss.

<p align="left">
  <img alt="version" src="https://img.shields.io/badge/version-1.3.0-blue">
  <img alt="platform" src="https://img.shields.io/badge/CS2-CounterStrikeSharp-orange">
</p>

---

## ✨ Features

- **Workshop collection powered** — pulls its whole map pool from a Steam Workshop collection and auto-refreshes on an interval, no restart needed.
- **Rock the Vote (RTV)** — player-driven votes with a configurable ratio; threshold re-checks when players leave so votes never get stuck.
- **Nominations** — paged, searchable map nominations with per-player tracking.
- **Scheduled + forced votes** — auto-vote on a set round, or trigger votes/RTVs on demand as an admin.
- **Add maps on the fly** — `!addmap` pulls any workshop item into the pool by ID or URL, saved to `custom_maps.json`.
- **Omit maps instantly** — `!omitmap` hides maps by keyword across votes, RTV, and nominations — case-insensitive, word-order-independent, matches all.
- **Recent-map exclusion** — recently played maps are kept out of the random pool.
- **Hidden commands** — every command is intercepted silently and never spammed to public chat.
- **Quiet & tick-safe** — all disk I/O is offloaded off the game thread.

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

---

## 🛡️ Admin Commands

Requires the player's SteamID64 in the `admins` config list.

| Command | Description |
|---|---|
| `!addmap [workshop ID/URL]` | Add a workshop map to the pool (saved to `custom_maps.json`) |
| `!omitmap [words]` | Hide all maps whose names contain the given words |
| `!unomitmap [words]` | Remove a saved omit pattern |
| `!omitlist` | List saved omit patterns and how many maps each matches |
| `!forcemap [name]` | Immediately change to a map |
| `!setnextmap [name]` | Set the next map directly (no vote) |
| `!forcevote` | Force-start a map vote |
| `!forcertv` | Start an RTV-style vote (map changes when it ends) |
| `!finishvote` | End an active vote early |
| `!endwarmup` | End the current warmup |
| `!votedebug` | Show plugin state / diagnostics |

### `!omitmap` examples
```
!omitmap motel night     → hides "Motel at Night", "Night Motel v2", ...
!omitmap aim             → hides every map with "aim" in the name
```
Matching is **case-insensitive**, **ignores word order**, and **omits all matches**.
Maps added via `!addmap` are simply removed from `custom_maps.json`; collection maps are filtered by saved pattern (and the pattern applies to maps added later too).

---

## 🖥️ Console Commands

Run from the **server console**:

| Command | Description |
|---|---|
| `css_addmap <id/url>` | Add a workshop map to the pool |
| `css_omitmap <words>` | Omit maps by keyword |
| `css_setnextmap <name>` | Set the next map |
| `css_forcemap <name>` | Force a map change |
| `css_forcertv` | Start an RTV-style vote |
| `css_dumpmaps` | Dump all loaded map names + IDs to console |

---

## ⚙️ Configuration

Config lives at `addons/counterstrikesharp/configs/plugins/CS2SimpleVote/CS2SimpleVote.json`.

| Key | Default | Description |
|---|---|---|
| `steam_api_key` | `"YOUR_STEAM_API_KEY_HERE"` | Steam Web API key (required for collection + `!addmap`) |
| `collection_id` | `"123456789"` | Workshop collection ID to load maps from |
| `vote_on_round` | `10` | Round number that triggers the scheduled vote (`0` disables) |
| `enable_rtv` | `true` | Enable Rock the Vote |
| `rtv_ratio` | `0.60` | Fraction of players needed to trigger RTV (0–1] |
| `rtv_change_delay` | `5.0` | Seconds before an RTV winner loads |
| `enable_nominate` | `true` | Enable nominations |
| `nominate_per_page` | `6` | Maps shown per nomination page |
| `vote_options_count` | `8` | Number of maps in a vote (2–10) |
| `vote_open_for_rounds` | `1` | How many rounds a scheduled vote stays open |
| `show_midvote_progress` | `true` | Show running tallies during a vote |
| `vote_reminder_enabled` | `true` | Periodically remind players to vote |
| `vote_reminder_interval` | `30.0` | Seconds between vote reminders |
| `postmap_change_delay` | `10.0` | Seconds before an end-of-match winner loads |
| `omit_recent_maps` | `true` | Keep recently played maps out of the pool |
| `recent_maps_count` | `5` | How many recent maps to remember/exclude |
| `enable_map_message` | `true` | Periodically announce the current map |
| `map_message_interval` | `300.0` | Seconds between current-map messages |
| `server_name` | `"My CS2 Server"` | Name shown in the current-map message |
| `collection_refresh_minutes` | `30.0` | Minutes between collection refreshes (`0` = once at load, min 1) |
| `admins` | `[]` | List of admin SteamID64s |

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
| `custom_maps.json` | Maps added via `!addmap` (`[{ "Id", "Name" }]`) |
| `omitted_maps.json` | Saved omit patterns (`["motel night", "aim"]`) |
| `recent_maps.json` | Recently played map history |
| `map_cache.json` | Cached collection so maps are available before the first fetch |
| `logs/YYYY-MM-DD/events.log` | Per-day event log (votes, RTVs, admin actions, map changes) |

---

## 🚀 Installation

1. Install [CounterStrikeSharp](https://docs.cssharp.dev/) on your server.
2. Drop the compiled plugin into `addons/counterstrikesharp/plugins/CS2SimpleVote/`.
3. Start the server once to generate the config, then set your `steam_api_key`, `collection_id`, and `admins`.
4. Reload the plugin or restart — done.

> A [Steam Web API key](https://steamcommunity.com/dev/apikey) is required to resolve the collection and look up maps for `!addmap`.
