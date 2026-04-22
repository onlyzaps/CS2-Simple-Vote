# CS2SimpleVote

A lightweight, robust, and feature-rich map voting plugin for Counter-Strike 2, built on the **CounterStrikeSharp** framework. It provides a seamless experience for players to nominate and vote for the next map from a Steam Workshop collection, with powerful administrative controls.

---

## 🚀 Key Features

*   ✅ **Automated Voting**: Trigger a map vote automatically at a configurable round number.
*   🔥 **Rock The Vote (RTV)**: Allow players to initiate a vote to change the map immediately. When the RTV vote resolves, the winning map loads right away (after a short configurable delay) — no waiting for end-of-match.
*   🗳️ **Nomination System**: 
    *   Players can nominate specific maps from the collection.
    *   **Search Support**: Use `!nominate <term>` to filter maps by name.
    *   **Auto-Selection**: If a search term matches only one map, it is nominated instantly.
    *   **Re-nomination**: Players can change their nomination at any time.
*   📜 **Recent Map History**: Prevents recently played maps from appearing in the automated vote pool.
*   🛠️ **Workshop Integration**: Fetches and caches maps from a specified Steam Workshop Collection via the modern `IPublishedFileService/GetDetails` endpoint.
*   🧩 **Nested Collection Support**: Collections that contain other collections are fully supported. The loader performs a recursive BFS through every nested collection and includes all maps — both directly attached to the root and from any depth of nested collections — in a single flat map pool.
*   📢 **Interactive HUD**: Displays a "VOTE NOW!" center-screen alert (with countdown timer for force votes) for players who haven't voted yet.
*   💬 **Smart Announcements**: Customizable server name and recurring messages showing the current map.
*   📊 **Mid-Vote Progress**: Optionally displays live vote tallies when a vote ends.
*   🛡️ **Admin Controls**: Force map changes, set the next map, force or finish votes, and end warmup — all from chat or the server console.
*   🖥️ **Console Commands**: Server operators can use `css_setnextmap` and `css_forcemap` with partial name matching directly from the server console.

---

## 📂 Configuration & Data

The configuration file is generated at `.../configs/plugins/CS2SimpleVote/CS2SimpleVote.json`.

### Options

| Option | Type | Default | Description |
| :--- | :---: | :---: | :--- |
| `steam_api_key` | `string` | `""` | Your Steam Web API key (required for Workshop fetching). |
| `collection_id` | `string` | `"123456789"` | The Steam Workshop Collection ID used as the map pool. |
| `vote_round` | `int` | `10` | The round number when the automated map vote starts. |
| `vote_open_for_rounds`| `int` | `1` | Number of rounds a scheduled vote remains open before closing. |
| `enable_rtv` | `bool` | `true` | Enables or disables the `!rtv` command. |
| `rtv_percentage` | `float` | `0.60` | The percentage of human players required to trigger an RTV (0.0 - 1.0). |
| `rtv_change_delay` | `float` | `5.0` | Seconds to wait before switching maps after a successful RTV. |
| `enable_nominate` | `bool` | `true` | Enables or disables the `!nominate` command. |
| `nominate_per_page` | `int` | `6` | Number of maps to display per page in selection menus. |
| `vote_options_count` | `int` | `8` | Total number of maps that appear in a single vote (max 10). |
| `vote_reminder_enabled`| `bool` | `true` | Whether to send chat reminders to players who haven't voted. |
| `vote_reminder_interval`| `float` | `30.0` | How often (in seconds) to send vote reminders. |
| `show_midvote_progress`| `bool` | `true` | Show a breakdown of vote counts when the vote ends. |
| `enable_recent_maps` | `bool` | `true` | Enables filtering to prevent recent maps from being auto-picked for votes. |
| `recent_maps_count` | `int` | `5` | How many previous maps to remember and exclude from the vote pool. |
| `server_name` | `string` | `"My CS2 Server"` | The server name displayed in map broadcast messages. |
| `show_map_message` | `bool` | `true` | Enables a recurring chat message showing the current map. |
| `map_message_interval`| `float` | `300.0` | Interval in seconds between map info broadcasts. |
| `admins` | `List<ulong>` | `[]` | List of SteamID64s allowed to use admin commands. |

### Admin Configuration Example
To grant admin access, add your SteamID64 (decimal format, starts with 7) to the `admins` array:
```json
"admins": [
    76561198012345678,
    76561197960287930
]
```

---

## ⌨️ Player Commands
*Chat commands can be used with or without the `!` prefix.*

### `!help`
Lists all available commands in chat. Admins see additional admin-only commands.

### `!rtv`
Add your vote to change the current map. Once the configured percentage of players have voted, a map vote starts immediately. When the RTV vote resolves, the winning map loads right away after `rtv_change_delay` seconds — it does *not* wait for the match to end.
```text
PlayerName wants to change the map! (1/5)
```

### `!nominate [name]` / `!nom [name]`
Open the nomination menu, or search for a map by partial name. If only one map matches, it is nominated instantly. Players can change their nomination by nominating again.
```text
Page 1/2. Type number to select (or 'cancel'):
[1] de_dust2
[2] de_mirage
[3] cs_office
[4] de_nuke
[5] de_inferno
[6] de_vertigo
[0] Next Page
```

### `!nominatelist`
Shows the maps currently nominated for the next vote and who nominated them.
```text
--- Nominated Maps (2/8) ---
 - PlayerOne - de_dust2
 - PlayerTwo - de_mirage
```

### `!revote`
Re-displays the current vote options so you can change your vote during an active vote.

### `!nextmap`
Displays the next map once voting has finished or an admin has set it.
```text
The next map will be: de_dust2
```

### `!lastmap`
Shows the last played map before the current one.
```text
The last played map was: de_mirage
```

### `!recentmaps [count]`
Shows recently played maps (excluding the current map). Optionally specify a count to limit results.
```text
-----------------------
Last 5 Recent Maps
-----------------------
1. de_mirage
2. de_inferno
3. cs_office
4. de_nuke
5. de_vertigo
```

### `!maplist`
Prints the full alphabetical list of every map loaded from the Workshop collection (including maps pulled in from nested collections) to your personal client console. A chat message confirms how many maps were sent. Open the console with `~` to view the list.
```text
All 42 maps were sent to your console. Press ~ to view.
```

---

## 🛡️ Admin Chat Commands
*Requires your SteamID to be in the `admins` configuration list. Used with or without the `!` prefix in chat.*

### `!forcemap [name]`
Forcefully changes the map immediately. Use with a search term to filter maps.
*   **One match**: Changes map immediately.
*   **Multiple matches**: Opens a selection menu.
```text
Admin AdminName forced map change to de_dust2.
```

### `!setnextmap [name]`
Sets the next map to be played at the end of the current match (does not change immediately). Use with a search term to filter maps.
*   **One match**: Sets the next map immediately.
*   **Multiple matches**: Opens a selection menu.
```text
AdminName has set the next map to de_dust2.
```

### `!forcevote`
Manually starts the map vote process.
*   **If no vote has occurred**: Starts a standard map vote that closes at the end of the round.
*   **If a vote has already finished**: Triggers a 30-second revote with a countdown timer.
```text
Admin AdminName initiated a map vote.
--- Vote for the Next Map! ---
```

### `!finishvote`
Ends an active vote early and tallies the results immediately.
```text
Admin AdminName ended the vote early.
```

### `!endwarmup`
Ends the current warmup period.
```text
Admin AdminName ended the warmup.
```

### `!votedebug`
Displays detailed debug information about the plugin state, including maps loaded, API status, vote state, and active vote tallies. Also dumps full state to the server console as JSON.

---

## 🖥️ Server Console Commands

These commands can be run from the server console (RCON) or by admins in-game. They use partial name matching — the best match is selected automatically without prompting.

### `css_setnextmap <partial name>`
Sets the next map by partial name match. Prioritizes: exact match → starts-with (shortest) → contains (shortest).
```text
css_setnextmap dust
[CS2SimpleVote] Next map set to: de_dust2 (ID: 3070321328)
```

### `css_forcemap <partial name>`
Forces an immediate map change by partial name match. Uses the same matching logic as `css_setnextmap`.
```text
css_forcemap mirage
[CS2SimpleVote] Forcing map change to: de_mirage (ID: 3070253400)
```

### `css_dumpmaps`
Dumps all available map names and their Workshop IDs to the server console. Server console only.
```text
--- CS2SimpleVote: 12 Available Maps (Collection: 3393498542) ---
  cs_office  (ID: 3070581293)
  de_dust2   (ID: 3070321328)
  de_mirage  (ID: 3070253400)
  ...
--- End (12 maps loaded) ---
```

---

## 🛠 Installation

1.  Install [CounterStrikeSharp](https://github.com/rooneydirects/CounterStrikeSharp).
2.  Place the `CS2SimpleVote.dll` in the `game/csgo/addons/counterstrikesharp/plugins/CS2SimpleVote/` folder.
3.  Configure your `steam_api_key`, `collection_id`, and `admins` in the generated config file.
4.  Restart your server or load the plugin.

