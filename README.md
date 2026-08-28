# CS2SimpleVote

A lightweight, Workshop-collection–driven map voting plugin for Counter-Strike 2 ([CounterStrikeSharp](https://docs.cssharp.dev/)). RTV, nominations, scheduled votes, and live map management — no database, no fuss.

<p align="left">
  <img alt="version" src="https://img.shields.io/badge/version-1.7.8-blue">
  <img alt="platform" src="https://img.shields.io/badge/CS2-CounterStrikeSharp-orange">
</p>

---

## ✨ Features

- **Three map sources, one pool** — the Steam Workshop collection (`collection_maps.json`), manually added workshop maps (`workshop_maps.json`), and official stock maps (`stock_maps.json`) all merge into the same vote pool.
- **Enable/disable per map** — every map is one readable line with an `enabled` flag. Flip it to omit or include the map; that IS the omit system.
- **Realtime file edits** — all three files are re-read before every vote, nomination, and menu. Edit a flag mid-map and the very next vote honors it.
- **Auto-synced membership** — the collection file tracks the Steam collection (adds/removals/titles); the stock file tracks the server engine's own map list, re-synced automatically whenever the game build changes (seasonal rotations land by themselves). Only your `enabled` flags are yours to manage.
- **Rock the Vote (RTV)** — player-driven votes with a configurable ratio; threshold re-checks when players leave so votes never get stuck.
- **Nominations** — paged, searchable map nominations with per-player tracking.
- **Match-aware scheduled votes** — the automatic vote opens `vote_rounds_before_end` rounds before the match can end, computed from `mp_maxrounds` and `mp_match_can_clinch`. No hardcoded round numbers to keep in sync with your server config.
- **Extend option** — optionally adds `[0] Extend Current Map` to every vote (`0` or `!0` to cast); if it wins, the next map is the current one.
- **Round-based or timed votes** — scheduled votes stay open for N rounds, or flip one flag to run them on a seconds timer instead (timed votes are never interrupted by round changes).
- **Center vote panel** — optional display-only panel in the center of the screen: a yellow "type a number in chat to vote" header, each numbered option with its live tally (`1: Map Name (3)`), and a countdown footer that shifts green → yellow → red as time runs out. Styled like CS2MenuManager's CenterHtmlMenu but implemented in-house with the native `PrintToCenterHtml` — no dependency, no entities, no keys to learn. Players vote in chat as usual (the chat option list is suppressed while the panel is on); it stays up for the whole vote and disappears the moment it ends.
- **CounterStrikeSharp admin support** — vote admin access via `@css/generic` / `@css/root`; the manual SteamID list still works.
- **Recent-map exclusion** — the last `recent_maps_count` played maps are kept out of votes entirely: the random pool *and* nominations. Works for workshop and stock maps alike.
- **Self-documenting config** — `CS2SimpleVote.json` is generated sectioned by feature with instructions above every setting, plus a pristine `CS2SimpleVote.example.json` for reference. Edits apply on the next map change, no restart.
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
| `!help` | List available commands (admins get a picker: user or admin commands) |

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
| `!endvote` | End an active vote early |
| `!changenow` | Change to the queued next map immediately |
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
| `css_syncstockmaps` | Force a re-scan of the engine's stock maps (logs the resolved maps folder) |

---

## ⚙️ Configuration

Config lives at `addons/counterstrikesharp/configs/plugins/CS2SimpleVote/CS2SimpleVote.json`.

The plugin **generates it for you**, grouped into the sections below with instructions above every setting (comments are safe — the loader skips them). A pristine copy of the defaults, `CS2SimpleVote.example.json`, is regenerated next to it on every load for reference. Hand edits apply on the next map change; your values are always preserved when the plugin refreshes the file layout.

### Steam Workshop Collection

| Key | Default | Description |
|---|---|---|
| `steam_api_key` | `"YOUR_STEAM_API_KEY_HERE"` | [Steam Web API key](https://steamcommunity.com/dev/apikey) (required for the collection + `!addmap`) |
| `collection_id` | `"123456789"` | Workshop collection ID to load maps from |
| `collection_refresh_minutes` | `30` | Minutes between collection re-syncs (`0` = once at load, min 1) |

### Admins

| Key | Default | Description |
|---|---|---|
| `use_css_admins` | `true` | Use CounterStrikeSharp's admin system: `@css/generic` or `@css/root` grants vote admin access |
| `admins` | `[]` | Manual SteamID64 list — still honored alongside CSS admins |

Grant permissions in `addons/counterstrikesharp/configs/admins.json`: players with `@css/generic` or `@css/root` may use every vote admin command (`!forcemap`, `!omitmap`, ...).

### Scheduled Vote Trigger

| Key | Default | Description |
|---|---|---|
| `vote_rounds_before_end` | `3` | The automatic vote opens this many rounds before the match can end (`0` disables) |

The trigger round is computed from the server's own match rules — nothing to keep in sync by hand:

- **`mp_match_can_clinch 1`** (default): a team can end the match at `mp_maxrounds / 2 + 1`, so the vote schedules against that earliest possible end. `mp_maxrounds 24` → clinch at round 13 → vote opens at round **10**.
- **`mp_match_can_clinch 0`**: every round plays, so the full `mp_maxrounds` applies (the window doubles). `mp_maxrounds 24` → vote opens at round **21**.
- Requires `mp_maxrounds > 0`; cvars are re-read every round start, so live changes count.

### Vote Style — `enable_timed_vote` (switches between the two sections below)

| Key | Default | Description |
|---|---|---|
| `enable_timed_vote` | `true` | `true` = timed vote (fixed seconds, never interrupted by round changes) · `false` = round-based vote |

RTV votes are always timed (30 seconds) regardless of this setting.

#### Timed Vote

| Key | Default | Description |
|---|---|---|
| `timed_vote_seconds` | `60` | How long a timed vote stays open (10–600) |

#### Round-Based Vote

| Key | Default | Description |
|---|---|---|
| `vote_open_for_rounds` | `3` | How many rounds the vote stays open |
| `show_midvote_progress` | `false` | Print running tallies in chat at round ends (round-based votes only — does nothing for timed votes) |

### Vote Options

| Key | Default | Description |
|---|---|---|
| `enable_extend_vote` | `false` | Add `[0] Extend Current Map` to votes (`0` / `!0` to cast); winning sets the next map to the current one |
| `vote_options_count` | `5` | Number of maps offered per vote (2–10) |

### Vote HUD

| Key | Default | Description |
|---|---|---|
| `enable_vote_hud` | `false` | Center vote panel; replaces the `VOTE NOW!` prompt and suppresses chat reminders |

A display-only panel in the center of the screen, styled like [CS2MenuManager](https://github.com/schwarper/CS2MenuManager)'s CenterHtmlMenu but implemented in-house with the native `PrintToCenterHtml` — **no dependency, no entities, no keys to learn**:

```
Type a number to vote          ← yellow header
1: Dust II (3)
2: Mirage (1)
0: Extend Current Map (0)      ← when enable_extend_vote is on
42s remaining                  ← timed votes: green → yellow → red
```

Players vote by **typing the option number in chat** as usual — the panel updates live as votes come in, stays up for the whole vote, and disappears the moment the vote ends. Updates are **flash-free**: the cached panel is fed to the display every tick (the same approach CounterStrikeSharp's own CenterHtmlMenu uses), which keeps it rock solid — identical re-sends and content swaps are both seamless, so nothing fades or pulses between updates. Option rows are padded between the map name and the tally so they all render at the same width — which makes the **numbers line up in a straight column on the left and the vote counts on the right**, and a map name too long for the panel never word-wraps — it **scrolls marquee-style** through its slot (4 steps per second), pausing for ~3 seconds at the start of the name each cycle so it can be read, while the number and tally stay fixed. Column widths are computed from **real font metrics read out of the game's own font file** at load (auto-detected under `csgo/panorama/fonts`; override with `hud_font_file`), so the numbers line up instead of drifting with the map name. The countdown footer turns **green** above 50% time left, **yellow** down to 25%, then **red**. While the panel is enabled, the option list is no longer printed into chat (chat still announces the vote and the winner).

### Vote Reminders

| Key | Default | Description |
|---|---|---|
| `vote_reminder_enabled` | `true` | Chat reminder (with the option list) for players who haven't voted — ignored while `enable_vote_hud` is on |
| `vote_reminder_interval` | `30` | Seconds between reminders |

### Rock the Vote (RTV)

| Key | Default | Description |
|---|---|---|
| `enable_rtv` | `true` | Enable Rock the Vote |
| `rtv_ratio` | `0.6` | Fraction of connected players required to trigger (0–1] |
| `rtv_change_delay` | `5` | Seconds before the winning map loads |

### Nominations

| Key | Default | Description |
|---|---|---|
| `enable_nominate` | `true` | Enable nominations |
| `nominate_per_page` | `8` | Maps shown per nomination page |

### Recent Map Exclusion

| Key | Default | Description |
|---|---|---|
| `omit_recent_maps` | `true` | Keep recently played maps out of votes and nominations |
| `recent_maps_count` | `10` | How many recent maps to remember/exclude |

### Current Map Message

| Key | Default | Description |
|---|---|---|
| `enable_map_message` | `true` | Periodic chat message announcing the current map |
| `show_server_name_in_map_message` | `true` | `true`: "You're playing X on `server_name`!" · `false`: "You're playing X!" |
| `map_message_interval` | `300` | Seconds between messages |
| `server_name` | `"My CS2 Server"` | Name shown when enabled |

### Map Change

| Key | Default | Description |
|---|---|---|
| `postmap_change_delay` | `10` | Seconds after match end before the winning map loads (max 15) |

### Managed by the plugin (do not edit)

| Key | Default | Description |
|---|---|---|
| `last_synced_build` | `0` | Game build (`steam.inf`) the stock map list was last synced against |
| `ConfigVersion` | `1` | Used by CounterStrikeSharp |

---

## 📂 Data Files

Generated next to the config, all hand-editable:

Everything below is generated automatically on first launch, next to the config, and is hand-editable:

| File | Purpose |
|---|---|
| `CS2SimpleVote.json` | Main config — sectioned with instructions above every setting |
| `CS2SimpleVote.example.json` | Pristine defaults for reference (regenerated every load; edits are ignored) |
| `collection_maps.json` | Steam collection maps — membership/titles auto-synced, you own the `enabled` flags |
| `workshop_maps.json` | Manually added workshop maps — yours entirely (`!addmap` writes here) |
| `stock_maps.json` | Official stock maps — auto-synced from the server engine, you own the `enabled` flags |
| `recent_maps.json` | Recently played map history |
| `logs/YYYY-MM-DD/events.log` | Per-day event log (votes, RTVs, admin actions, map changes) |

All three map files share the same readable one-entry-per-line format and can be **edited live** — changes are re-read before every vote, nomination, and admin menu, no reload or map change required.

### Collection maps (`collection_maps.json`)

Starts as a commented template showing the syntax, then fills itself in from your Steam Workshop collection on the first fetch and stays in sync every refresh (`collection_refresh_minutes`): new collection maps appear **enabled**, removed maps are pruned, titles follow Steam. Set `"enabled": false` on a line to omit that map from votes without touching the collection.

```json
[
  { "id": "3070321328", "title": "Dust2 Remake", "enabled": true },
  { "id": "3121217565", "title": "Motel at Night", "enabled": false }
]
```

### Workshop maps (`workshop_maps.json`)

Your personal additions outside the collection — same format as above, generated as a commented template with a blank entry to copy. Add lines by hand or via `!addmap`; a line with just an `"id"` works immediately (the title auto-fills from Steam on the next refresh). Deleting the file clears your manual list; nothing here is ever pruned automatically.

### Stock maps (`stock_maps.json`)

Generated straight from the server engine — map names from the `.vpk` files in the game's `maps/` folder (vanity and other non-playable vpks excluded), display titles from the server's own localization (`SFUI_Map_*`). Nothing to curate by hand:

- **First launch** writes every installed stock map, sorted by prefix (`ar_`, `cs_`, `de_`, ...) then alphabetically, all `"enabled": false`. The game folder is resolved by asking the engine itself (`Server.GameDirectory`, with a plugin-relative fallback) and the chosen path is logged; if the file ever fails to appear, run `css_syncstockmaps` from the server console to force a re-scan and see exactly which maps folder was used.
- **Valve updates land by themselves**: the engine is re-scanned at plugin launch whenever the game build (`steam.inf`) differs from `last_synced_build` in the main config — new maps appear disabled, removed maps are deleted, and your `enabled` edits are always preserved. No scan runs when the build hasn't changed.
- Enabled stock maps behave exactly like workshop maps everywhere: votes, RTV, nominations, `!forcemap` / `!setnextmap` (searchable by title *or* map name, e.g. `!nominate de_dust2`), omitting, and the recent-map exclusion.

```json
[
  { "map": "ar_baggage", "title": "Baggage", "enabled": false },
  { "map": "cs_office", "title": "Office", "enabled": true },
  { "map": "de_dust2", "title": "Dust II", "enabled": true }
]
```

> **Upgrading from an older build?** Everything migrates automatically on first load: `custom_maps.json` becomes `workshop_maps.json`, omit patterns from `omitted_maps.json` are applied as `"enabled": false` flags, `map_cache.json` seeds `collection_maps.json` (old files kept as `.bak`), and a flat `CS2SimpleVote.json` is rewritten into the sectioned layout with your values preserved. The removed `vote_on_round` key is superseded by `vote_rounds_before_end`.

---

## 🚀 Installation

1. Install an up-to-date [CounterStrikeSharp](https://docs.cssharp.dev/) on your server — the plugin targets the current API (**v1.0.370+ / .NET 10 runtime**).
2. Drop the compiled plugin into `addons/counterstrikesharp/plugins/CS2SimpleVote/`.
3. Start the server once to generate the config, then set your `steam_api_key`, `collection_id`, and `admins`.
4. Reload the plugin or restart — done.

> A [Steam Web API key](https://steamcommunity.com/dev/apikey) is required to resolve the collection and look up maps for `!addmap`.
