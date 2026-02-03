# CS2SimpleVote

A lightweight, robust, and feature-rich map voting plugin for Counter-Strike 2, built on the **CounterStrikeSharp** framework. It provides a seamless experience for players to nominate and vote for the next map from a Steam Workshop collection.

---

## 🚀 Key Features

*   ✅ **Automated Voting**: Trigger a map vote automatically at a specific round in the match.
*   🔥 **Rock The Vote (RTV)**: Allow players to initiate a vote to change the map immediately.
*   🗳️ **Nomination System**: 
    *   Players can nominate specific maps from the collection.
    *   **Search Support**: Use `!nominate <term>` to filter maps by name.
*   📜 **Recent Map History**: Prevents recently played maps from appearing in the automated vote pool for a set number of rounds.
*   🛠️ **Workshop Integration**: Automatically fetches and caches maps from a specified Steam Workshop Collection.
*   📢 **Interactive HUD**: Displays a "VOTE NOW!" alert in the center of the screen for players who haven't voted yet.
*   💬 **Smart Announcements**: Customizable server name and recurring messages showing the current map.

---

## 📂 Data Files

The plugin generates and maintains two JSON files in its configuration directory:

1.  **`map_cache.json`**:
    *   **Purpose**: Stores a local copy of map titles and IDs fetched from the Steam Workshop.
    *   **Benefit**: Ensures the plugin works instantly on startup even if the Steam API is slow or temporarily unreachable.
2.  **`recent_maps.json`**:
    *   **Purpose**: Tracks the Steam IDs of the most recently played maps.
    *   **Benefit**: Used by the logic to exclude these maps from automated vote options, ensuring map variety.

---

## ⚙️ Configuration Options

The configuration file is located at `.../configs/plugins/CS2SimpleVote/CS2SimpleVote.json`.

| Option | Type | Default | Description |
| :--- | :---: | :---: | :--- |
| `steam_api_key` | `string` | `""` | Your Steam Web API key (Required for Workshop fetching). |
| `collection_id` | `string` | `"123456789"` | The Steam Workshop Collection ID used as the map pool. |
| `vote_round` | `int` | `10` | The round number when the automated map vote starts. |
| `enable_rtv` | `bool` | `true` | Enables or disables the `!rtv` command. |
| `rtv_percentage` | `float` | `0.60` | The percentage of human players required to trigger an RTV (0.0 - 1.0). |
| `rtv_change_delay` | `float` | `5.0` | Seconds to wait before switching maps after a successful RTV. |
| `enable_nominate` | `bool` | `true` | Enables or disables the `!nominate` command. |
| `nominate_per_page` | `int` | `6` | Number of maps to display per page in the nomination menu. |
| `vote_options_count` | `int` | `8` | Total number of maps that appear in a single vote (max 10). |
| `vote_open_for_rounds`| `int` | `1` | Number of rounds a scheduled vote remains open before closing. |
| `vote_reminder_enabled`| `bool` | `true` | Whether to send chat reminders to players who haven't voted. |
| `vote_reminder_interval`| `float` | `30.0` | How often (in seconds) to send vote reminders. |
| `enable_recent_maps` | `bool` | `true` | Enables filtering to prevent recent maps from being auto-picked for votes. |
| `recent_maps_count` | `int` | `5` | How many previous maps to remember and exclude from the vote pool. |
| `server_name` | `string` | `"CS2 Server"` | The server name displayed in map broadcast messages. |
| `show_map_message` | `bool` | `true` | Enables a recurring chat message showing the current map. |
| `map_message_interval`| `float` | `300.0` | Interval in seconds between map info broadcasts. |

---

## ⌨️ Player Commands

*   `!rtv` / `rtv`: Add your vote to change the current map.
*   `!nominate` / `nominate`: Open the menu to nominate a map.
*   `!nominate <term>`: Search the collection for maps matching the term.
*   `!revote` / `revote`: Re-open the current vote menu if you want to change your mind.
*   `!nextmap` / `nextmap`: Displays the result of the vote once it has finished.

---

## 🛠 Installation

1.  Install [CounterStrikeSharp](https://github.com/rooneydirects/CounterStrikeSharp).
2.  Place the `CS2SimpleVote.dll` in the `game/csgo/addons/counterstrikesharp/plugins/CS2SimpleVote/` folder.
3.  Configure your `steam_api_key` and `collection_id` in the generated config file.
4.  Restart your server or load the plugin.
