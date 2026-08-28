using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CS2SimpleVote;

// --- Configuration ---
// Property order mirrors the sectioned layout the plugin writes to disk (see
// RenderSectionedConfig, which is the single source of truth for the on-disk
// file: sections, comments, and key order).
public class VoteConfig : BasePluginConfig
{
    // Steam Workshop Collection
    [JsonPropertyName("steam_api_key")] public string SteamApiKey { get; set; } = "YOUR_STEAM_API_KEY_HERE";
    [JsonPropertyName("collection_id")] public string CollectionId { get; set; } = "123456789";
    [JsonPropertyName("collection_refresh_minutes")] public float CollectionRefreshMinutes { get; set; } = 30.0f;

    // Admins
    [JsonPropertyName("use_css_admins")] public bool UseCssAdmins { get; set; } = true;
    [JsonPropertyName("admins")] public List<ulong> Admins { get; set; } = new();

    // Scheduled Vote Trigger — the automatic vote starts this many rounds before
    // the match can end. The end round is derived from mp_maxrounds; when
    // mp_match_can_clinch is on, the earliest possible end (maxrounds/2 + 1) is
    // used, when off the full mp_maxrounds is used. 0 disables the scheduled vote.
    [JsonPropertyName("vote_rounds_before_end")] public int VoteRoundsBeforeEnd { get; set; } = 3;

    // Vote Style — switches between the Timed Vote and Round-Based Vote sections.
    [JsonPropertyName("enable_timed_vote")] public bool EnableTimedVote { get; set; } = true;

    // Timed Vote
    [JsonPropertyName("timed_vote_seconds")] public float TimedVoteSeconds { get; set; } = 60.0f;

    // Round-Based Vote
    [JsonPropertyName("vote_open_for_rounds")] public int VoteOpenForRounds { get; set; } = 3;
    [JsonPropertyName("show_midvote_progress")] public bool ShowMidVoteProgress { get; set; } = false;

    // Vote Options
    [JsonPropertyName("enable_extend_vote")] public bool EnableExtendVote { get; set; } = false;
    [JsonPropertyName("vote_options_count")] public int VoteOptionsCount { get; set; } = 5;

    // Vote HUD (display-only center panel with options + live tallies; replaces
    // VOTE NOW! and suppresses chat reminders while enabled)
    [JsonPropertyName("enable_vote_hud")] public bool EnableVoteHud { get; set; } = false;

    // Vote Reminders
    [JsonPropertyName("vote_reminder_enabled")] public bool EnableReminders { get; set; } = true;
    [JsonPropertyName("vote_reminder_interval")] public float ReminderIntervalSeconds { get; set; } = 30.0f;

    // Rock the Vote
    [JsonPropertyName("enable_rtv")] public bool EnableRtv { get; set; } = true;
    [JsonPropertyName("rtv_ratio")] public float RtvRatio { get; set; } = 0.6f;
    [JsonPropertyName("rtv_change_delay")] public float RtvDelaySeconds { get; set; } = 5.0f;

    // Nominations
    [JsonPropertyName("enable_nominate")] public bool EnableNominate { get; set; } = true;
    [JsonPropertyName("nominate_per_page")] public int NominatePerPage { get; set; } = 8;

    // Recent Map Exclusion
    [JsonPropertyName("omit_recent_maps")] public bool OmitRecentMaps { get; set; } = true;
    [JsonPropertyName("recent_maps_count")] public int RecentMapsCount { get; set; } = 10;

    // Current Map Message
    [JsonPropertyName("enable_map_message")] public bool EnableMapMessage { get; set; } = true;
    [JsonPropertyName("show_server_name_in_map_message")] public bool ShowServerNameInMapMessage { get; set; } = true;
    [JsonPropertyName("map_message_interval")] public float CurrentMapMessageInterval { get; set; } = 300.0f;
    [JsonPropertyName("server_name")] public string ServerName { get; set; } = "My CS2 Server";

    // Map Change
    [JsonPropertyName("postmap_change_delay")] public float PostMapChangeDelay { get; set; } = 10.0f;

    // Managed by the plugin: the game build (from steam.inf) the stock map list
    // was last synced against. Stock maps re-sync from the engine only when this
    // differs from the running build.
    [JsonPropertyName("last_synced_build")] public int LastSyncedBuild { get; set; } = 0;
}

public class MapItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

// One line of stock_maps.json. "map" is the engine map name (changelevel argument),
// "title" is the display name resolved from the server's own localization files.
public class StockMapEntry
{
    [JsonPropertyName("map")] public string Map { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = false;
}

// One line of collection_maps.json / workshop_maps.json. "id" is the workshop ID,
// "enabled" is what admins flip to omit/include a map. Enabled defaults to true so a
// hand-added workshop_maps.json line without an "enabled" field just works.
public class TrackedMapEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
}

// --- Main Plugin ---
public class CS2SimpleVote : BasePlugin, IPluginConfig<VoteConfig>
{
    public override string ModuleName => "CS2SimpleVote";
    public override string ModuleVersion => "1.8.0";

    private const string ColorDefault = "\x01";
    private const string ColorGreen = "\x04";
    private const string ColorRed = "\x07";

    public VoteConfig Config { get; set; } = new();

    // Data Sources
    private List<MapItem> _availableMaps = new();
    private List<MapItem> _recentMaps = new();
    private HttpClient _httpClient = new();
    private CounterStrikeSharp.API.Modules.Timers.Timer? _reminderTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _mapInfoTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _centerMessageTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _voteEndTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _mapChangeTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _collectionRefreshTimer;

    // State: Voting
    private bool _voteInProgress;
    private bool _voteFinished;
    private bool _isScheduledVote;
    private int _currentVoteRoundDuration;
    private bool _isForceVote;
    private bool _isRtvVote;
    private string? _previousWinningMapId;
    private string? _previousWinningMapName;
    private bool _matchEnded;
    private bool _nextMapSetByAdmin;
    private string? _nextMapName;
    private string? _pendingMapId;
    private readonly HashSet<int> _rtvVoters = new();
    private readonly Dictionary<int, string> _activeVoteOptions = new();
    private readonly Dictionary<int, int> _playerVotes = new();

    // Vote option key 0 maps to this sentinel when enable_extend_vote is on. Winning
    // it sets the next map to the current map. Never a real workshop ID or map name.
    private const string ExtendOptionId = "@extend";


    // State: vote timing / center HUD.
    // _voteIsTimed: this vote ends on a timer (RTV, revote, or enable_timed_vote)
    // rather than on round ends. _voteEndsAtUtc is the absolute deadline — remaining
    // time is always derived from it, so a 0.5s render tick can't drift the countdown.
    private bool _voteIsTimed;
    private DateTime _voteEndsAtUtc = DateTime.MinValue;

    // State: current map identity.
    // _expectedMapId/Name are set right before EVERY plugin-initiated map change
    // (host_workshop_map). They survive the map transition (the plugin instance is
    // not recreated between maps) and are consumed exactly once in OnMapStart.
    // This is the authoritative way to know which workshop item is now playing,
    // because the engine map name usually contains neither the workshop ID nor
    // the workshop title.
    private string? _expectedMapId;
    private string? _expectedMapName;
    private string? _currentMapId;

    // State: the three map sources. Each is backed by a hand-editable, one-entry-per-
    // line JSON file where "enabled" is the include/omit switch:
    //   _collectionMaps  <- collection_maps.json  (membership + titles synced from the
    //                       Steam Workshop collection; the file only owns the flags)
    //   _workshopMaps    <- workshop_maps.json    (manually added workshop maps —
    //                       !addmap or hand edits; entirely file-owned)
    //   _stockMaps       <- stock_maps.json       (official maps synced from the
    //                       engine's maps/ folder; the file only owns the flags)
    // _availableMaps is a derived view: enabled entries of all three, deduped by ID.
    // RefreshMapPools() re-reads the files and rebuilds it, and runs before every
    // vote/menu so hand edits apply in realtime. All three lists (and every file
    // read/write for them) live on the game thread ONLY — the Steam fetch worker
    // hands its results over via Server.NextFrame, never touching them directly.
    private List<TrackedMapEntry> _collectionMaps = new();
    private List<TrackedMapEntry> _workshopMaps = new();
    private List<StockMapEntry> _stockMaps = new();
    private Dictionary<string, string> _engineMapTitles = new(StringComparer.OrdinalIgnoreCase);
    private bool _stockSyncWarned = false;
    private DateTime _lastPoolsRefresh = DateTime.MinValue;

    // Omit word-patterns from the pre-1.5 layout, kept in memory for this session so
    // maps first seen after migration still start out disabled when they match.
    private List<string> _legacyOmitPatterns = new();

    private static readonly JsonSerializerOptions TolerantJson = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // State: Nomination
    private readonly List<MapItem> _nominatedMaps = new();
    private readonly HashSet<ulong> _hasNominatedSteamIds = new();
    private readonly Dictionary<ulong, MapItem> _nominationOwner = new();
    private readonly Dictionary<ulong, string> _nominationNames = new();
    private readonly Dictionary<int, List<MapItem>> _nominatingPlayers = new();
    private readonly Dictionary<int, int> _playerNominationPage = new();

    private CommandInfo.CommandListenerCallback? _playerChatDelegate;

    // State: Help picker (admins choose between user/admin command lists)
    private readonly HashSet<int> _helpMenuPlayers = new();

    // State: Forcemap
    private readonly Dictionary<int, List<MapItem>> _forcemapPlayers = new();
    private readonly Dictionary<int, int> _playerForcemapPage = new();

    // State: SetNextMap
    private readonly Dictionary<int, List<MapItem>> _setnextmapPlayers = new();
    private readonly Dictionary<int, int> _playerSetNextMapPage = new();

    // Logger (lightweight, single-line, folder-per-day)
    private string _logBaseDir = "";
    private readonly object _logLock = new();

    // File Paths
    private string _historyFilePath = "";
    private string _collectionMapsFilePath = "";
    private string _workshopMapsFilePath = "";
    private string _stockMapsFilePath = "";
    private string _configFilePath = "";
    private string _engineMapsDir = "";
    private string _engineLocalizationPath = "";
    private string _engineSteamInfPath = "";

    // Cancellation for background task
    private CancellationTokenSource _cts = new();

    // Flag to prevent execution after unload
    private bool _unloaded = false;
    private bool _hasLoadedCollectionMaps = false;
    private bool _isApiLoading = false;

    // Overlap guard for the background collection fetch. Read and written ONLY on the
    // game thread (set true before launching the worker in LaunchCollectionFetch,
    // reset to false inside a Server.NextFrame callback when the worker finishes).
    // Because it never changes on the worker thread, no lock/Interlocked is needed.
    private bool _collectionFetchRunning = false;

    public void OnConfigParsed(VoteConfig config)
    {
        Config = config;
        ValidateConfig();
    }

    // Shared validation/clamping so OnConfigParsed and the on-map-change reload apply
    // identical rules.
    private void ValidateConfig()
    {
        Config.VoteOptionsCount = Math.Clamp(Config.VoteOptionsCount, 2, 10);
        if (Config.NominatePerPage < 1) Config.NominatePerPage = 8;
        // rtv_ratio is a fraction of connected players (0–1]. Clamp bad values so a
        // config typo like 60 (instead of 0.60) can't make RTV impossible.
        if (Config.RtvRatio <= 0f) Config.RtvRatio = 0.60f;
        if (Config.RtvRatio > 1f) Config.RtvRatio = 1f;
        if (Config.RecentMapsCount < 0) Config.RecentMapsCount = 0;
        if (Config.VoteRoundsBeforeEnd < 0) Config.VoteRoundsBeforeEnd = 0;
        if (Config.LastSyncedBuild < 0) Config.LastSyncedBuild = 0;
        Config.TimedVoteSeconds = Math.Clamp(Config.TimedVoteSeconds, 10.0f, 600.0f);
        // Enforce a sane floor so a mistyped tiny value can't hammer the Steam API.
        if (Config.CollectionRefreshMinutes > 0 && Config.CollectionRefreshMinutes < 1.0f)
            Config.CollectionRefreshMinutes = 1.0f;
    }

    // Re-reads CS2SimpleVote.json from disk and applies it live. Called on every map
    // start so hand edits take effect on the next map without a plugin reload. Runs on
    // the game thread; a bad/partial file is caught and the previous config is kept.
    private void ReloadConfigFromDisk()
    {
        if (string.IsNullOrEmpty(_configFilePath) || !File.Exists(_configFilePath)) return;

        VoteConfig? parsed;
        try
        {
            string json = File.ReadAllText(_configFilePath);
            parsed = JsonSerializer.Deserialize<VoteConfig>(json, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Config reload skipped (invalid JSON): {ex.Message}");
            return;
        }
        if (parsed == null) return;

        float oldRefresh = Config.CollectionRefreshMinutes;
        string oldCollectionId = Config.CollectionId;
        string oldApiKey = Config.SteamApiKey;

        Config = parsed;
        ValidateConfig();

        // If the collection or API key changed, refetch immediately so the new pool is
        // live this map instead of waiting for the next scheduled refresh.
        if (!string.Equals(oldCollectionId, Config.CollectionId, StringComparison.Ordinal) ||
            !string.Equals(oldApiKey, Config.SteamApiKey, StringComparison.Ordinal))
        {
            LaunchCollectionFetch(isInitial: false);
        }

        // Rebuild the background refresh timer if the interval changed (including
        // enabling/disabling it via a 0).
        if (Math.Abs(oldRefresh - Config.CollectionRefreshMinutes) > 0.001f)
        {
            _collectionRefreshTimer?.Kill();
            _collectionRefreshTimer = null;
            if (Config.CollectionRefreshMinutes > 0)
            {
                float interval = Config.CollectionRefreshMinutes * 60.0f;
                _collectionRefreshTimer = AddTimer(interval, () =>
                {
                    if (_unloaded) return;
                    if (_voteInProgress) return;
                    LaunchCollectionFetch(isInitial: false);
                }, TimerFlags.REPEAT);
            }
        }

        Console.WriteLine("[CS2SimpleVote] Config reloaded from disk.");
    }

    // --- Sectioned config generation ---
    // The plugin owns the layout of CS2SimpleVote.json: keys grouped into feature
    // sections with instructions above each one. CounterStrikeSharp deserializes
    // with comment-skipping enabled (its own generated configs start with a //
    // header), so the comments are safe. The live file is (re)written whenever it
    // is missing, predates the sectioned layout, lacks a newly added key, or the
    // tracked game build changed — always from the CURRENT parsed Config, so hand
    // edits are never lost. A pristine defaults copy is kept alongside it as
    // CS2SimpleVote.example.json.

    private const string ConfigMarker = "CS2SimpleVote Configuration";

    private static string RenderSectionedConfig(VoteConfig c)
    {
        static string J(object v) => JsonSerializer.Serialize(v);
        var sb = new StringBuilder();
        void Section(string title, params string[] lines)
        {
            sb.AppendLine();
            sb.AppendLine("    // ----------------------------------------------------------------");
            sb.AppendLine($"    // {title}");
            sb.AppendLine("    // ----------------------------------------------------------------");
            foreach (var l in lines) sb.AppendLine($"    // {l}");
        }
        void Key(string name, object value) => sb.AppendLine($"    \"{name}\": {J(value)},");

        sb.AppendLine("// ================================================================");
        sb.AppendLine($"//  {ConfigMarker}");
        sb.AppendLine("// ================================================================");
        sb.AppendLine("//  - Edits apply automatically on the next map change (no reload).");
        sb.AppendLine("//  - Comments are safe to keep; the loader ignores them.");
        sb.AppendLine("//  - CS2SimpleVote.example.json holds the pristine defaults and is");
        sb.AppendLine("//    regenerated every load (edits there are ignored).");
        sb.AppendLine("{");

        Section("Steam Workshop Collection",
            "The map pool is pulled from this Workshop collection and re-synced",
            "every collection_refresh_minutes (0 = fetch once at load; minimum 1).",
            "A Steam Web API key is required: https://steamcommunity.com/dev/apikey");
        Key("steam_api_key", c.SteamApiKey);
        Key("collection_id", c.CollectionId);
        Key("collection_refresh_minutes", c.CollectionRefreshMinutes);

        Section("Admins",
            "use_css_admins: use CounterStrikeSharp's admin system for vote admin",
            "access — players with @css/generic or @css/root may use the admin",
            "commands. The manual \"admins\" SteamID64 list below keeps working",
            "either way.",
            "Example: \"admins\": [76561198000000000, 76561198000000001]");
        Key("use_css_admins", c.UseCssAdmins);
        Key("admins", c.Admins);

        Section("Scheduled Vote Trigger",
            "The automatic map vote opens vote_rounds_before_end rounds before the",
            "match can end. The end round comes from mp_maxrounds: with",
            "mp_match_can_clinch enabled the earliest possible end (maxrounds/2 + 1)",
            "is used; with clinching disabled every round plays, so the full",
            "mp_maxrounds is used instead. 0 disables the scheduled vote (RTV and",
            "admin votes still work). Requires mp_maxrounds > 0.");
        Key("vote_rounds_before_end", c.VoteRoundsBeforeEnd);

        Section("Vote Style (switches between the two sections below)",
            "true  = Timed Vote: the vote runs for a fixed number of seconds and",
            "        is never interrupted by round changes.",
            "false = Round-Based Vote: the vote stays open across round ends.",
            "RTV votes are always timed (30 seconds) regardless of this setting.");
        Key("enable_timed_vote", c.EnableTimedVote);

        Section("Timed Vote",
            "How long a timed vote stays open, in seconds (10-600).");
        Key("timed_vote_seconds", c.TimedVoteSeconds);

        Section("Round-Based Vote",
            "vote_open_for_rounds: how many rounds the vote stays open.",
            "show_midvote_progress: print running tallies in chat at round ends.",
            "Round-based votes only - it does nothing for timed votes.");
        Key("vote_open_for_rounds", c.VoteOpenForRounds);
        Key("show_midvote_progress", c.ShowMidVoteProgress);

        Section("Vote Options",
            "enable_extend_vote: adds \"[0] Extend Current Map\" to every vote",
            "(players type 0 or !0). If it wins, the next map is the current one.",
            "vote_options_count: number of maps offered per vote (2-10).");
        Key("enable_extend_vote", c.EnableExtendVote);
        Key("vote_options_count", c.VoteOptionsCount);

        Section("Vote HUD",
            "Display-only center-screen vote panel: a yellow \"type a number in",
            "chat to vote\" header, each numbered option with its live tally",
            "(\"1: Map Name (3)\"), and for timed votes a countdown footer that",
            "shifts green -> yellow -> red as time runs out. Rendered natively —",
            "no extra plugins. Shown for the whole vote, hidden the moment it",
            "ends. While enabled it replaces the plain \"VOTE NOW!\" prompt, the",
            "chat option list, and chat vote reminders.");
        Key("enable_vote_hud", c.EnableVoteHud);

        Section("Vote Reminders",
            "Chat reminder (with the option list) for players who haven't voted,",
            "every vote_reminder_interval seconds. Ignored while enable_vote_hud",
            "is on - the HUD replaces it.");
        Key("vote_reminder_enabled", c.EnableReminders);
        Key("vote_reminder_interval", c.ReminderIntervalSeconds);

        Section("Rock the Vote (RTV)",
            "rtv_ratio: fraction of connected players required to trigger (0-1].",
            "rtv_change_delay: seconds before the winning map loads.");
        Key("enable_rtv", c.EnableRtv);
        Key("rtv_ratio", c.RtvRatio);
        Key("rtv_change_delay", c.RtvDelaySeconds);

        Section("Nominations",
            "Players nominate maps into the next vote with !nominate.");
        Key("enable_nominate", c.EnableNominate);
        Key("nominate_per_page", c.NominatePerPage);

        Section("Recent Map Exclusion",
            "Keeps the last recent_maps_count played maps out of votes and",
            "nominations entirely.");
        Key("omit_recent_maps", c.OmitRecentMaps);
        Key("recent_maps_count", c.RecentMapsCount);

        Section("Current Map Message",
            "Periodic chat message announcing the current map. When",
            "show_server_name_in_map_message is true it reads",
            "\"You're playing <map> on <server_name>!\", otherwise just",
            "\"You're playing <map>!\".");
        Key("enable_map_message", c.EnableMapMessage);
        Key("show_server_name_in_map_message", c.ShowServerNameInMapMessage);
        Key("map_message_interval", c.CurrentMapMessageInterval);
        Key("server_name", c.ServerName);

        Section("Map Change",
            "Seconds after the match ends before the winning map loads (max 15).");
        Key("postmap_change_delay", c.PostMapChangeDelay);

        Section("Managed by the plugin - do not edit",
            "last_synced_build: the game build (steam.inf) the stock map list was",
            "last synced against. ConfigVersion: used by CounterStrikeSharp.");
        Key("last_synced_build", c.LastSyncedBuild);
        sb.AppendLine($"    \"ConfigVersion\": {c.Version}");

        sb.Append('}');
        return sb.ToString();
    }

    // Writes the live sectioned config (preserving current values) when needed, and
    // always keeps CS2SimpleVote.example.json in sync with the plugin's defaults.
    private void GenerateConfigFiles(bool force)
    {
        try
        {
            string text = File.Exists(_configFilePath) ? File.ReadAllText(_configFilePath) : "";
            string rendered = RenderSectionedConfig(Config);

            // Keys come from the renderer itself, so this can never drift from the
            // real schema: a key the file lacks (new in this version) forces a
            // rewrite that adds it with its current (default) value.
            bool missingKey = false;
            using (var doc = JsonDocument.Parse(rendered, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }))
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (!text.Contains($"\"{prop.Name}\"")) { missingKey = true; break; }

            if (force || missingKey || !text.Contains(ConfigMarker))
            {
                File.WriteAllText(_configFilePath, rendered);
                Console.WriteLine("[CS2SimpleVote] Wrote sectioned CS2SimpleVote.json (existing values preserved).");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Could not write sectioned config: {ex.Message}"); }

        try
        {
            string examplePath = Path.Combine(Path.GetDirectoryName(_configFilePath) ?? "", "CS2SimpleVote.example.json");
            string example = RenderSectionedConfig(new VoteConfig());
            string old = File.Exists(examplePath) ? File.ReadAllText(examplePath) : "";
            if (!string.Equals(old, example, StringComparison.Ordinal))
                File.WriteAllText(examplePath, example);
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Could not write example config: {ex.Message}"); }
    }

    public override void Load(bool hotReload)
    {
        _unloaded = false;

        // Reset all vote/match state on hot reload to prevent stale flags
        // (ResetState normally only runs on OnMapStart, which doesn't fire on reload)
        if (hotReload)
        {
            ResetState();
        }

        // Construct the path to the config folder manually:
        // ModuleDirectory is ".../plugins/CS2SimpleVote"
        // We want ".../configs/plugins/CS2SimpleVote"
        string configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "../../configs/plugins/CS2SimpleVote"));

        // If for some reason the folder structure is non-standard and that doesn't exist, fallback to ModuleDirectory
        if (!Directory.Exists(configDir))
        {
            // Try to create it, if fail, use plugin folder
            try { Directory.CreateDirectory(configDir); }
            catch { configDir = ModuleDirectory; }
        }

        _historyFilePath = Path.Combine(configDir, "recent_maps.json");
        _collectionMapsFilePath = Path.Combine(configDir, "collection_maps.json");
        _workshopMapsFilePath = Path.Combine(configDir, "workshop_maps.json");
        _stockMapsFilePath = Path.Combine(configDir, "stock_maps.json");
        _configFilePath = Path.Combine(configDir, "CS2SimpleVote.json");

        // Engine folders for the stock map sync. The engine itself is asked first
        // (Server.GameDirectory) so the maps folder is found regardless of where
        // the plugin dll physically lives (symlinks, non-standard layouts); the
        // ModuleDirectory-relative walk is only a fallback. Whichever candidate
        // actually contains a maps/ folder wins, and the choice is logged so a
        // missing stock_maps.json is diagnosable from the console.
        ResolveEngineDirs();
        _logBaseDir = Path.Combine(configDir, "logs");
        try { Directory.CreateDirectory(_logBaseDir); } catch { /* non-fatal */ }

        // Clear existing memory state before loading
        _recentMaps.Clear();

        // 1. Load Data Immediately (Sync)
        LoadMapHistory();
        MigrateLegacyFiles(configDir);

        // Stock maps re-sync from the engine only when the game build changed
        // (steam.inf), so Valve's seasonal adds/removals land exactly once per
        // game update: new maps appear disabled, removed maps are deleted.
        int build = ReadEngineBuild();
        bool buildChanged = build == 0 || build != Config.LastSyncedBuild;
        if (buildChanged)
        {
            LoadEngineMapTitles();
            SyncStockMapsConfig();
            if (build > 0 && build != Config.LastSyncedBuild)
            {
                Log("STOCK", $"Game build changed ({Config.LastSyncedBuild} -> {build}) — stock map list re-synced from the engine.");
                Config.LastSyncedBuild = build;
            }
        }

        // (Re)generate the sectioned config and the pristine example file. Forced
        // when a new build number must be persisted into last_synced_build.
        GenerateConfigFiles(force: buildChanged && build > 0);

        RefreshMapPools(force: true);

        // 3. Start Background Update (initial load), then schedule periodic refresh.
        LaunchCollectionFetch(isInitial: true);

        // Long-lived refresh timer. Deliberately NOT flagged STOP_ON_MAPCHANGE — it must
        // survive map changes and run for the whole plugin lifetime. It is created once
        // here and killed only in Unload, so it is never touched by ResetState/OnMapStart.
        if (Config.CollectionRefreshMinutes > 0)
        {
            float interval = Config.CollectionRefreshMinutes * 60.0f;
            _collectionRefreshTimer = AddTimer(interval, () =>
            {
                if (_unloaded) return;
                // Defer a refresh while a vote is live so the option/name lookups a vote
                // relies on aren't swapped mid-flight. It'll refresh on the next tick.
                if (_voteInProgress) return;
                LaunchCollectionFetch(isInitial: false);
            }, TimerFlags.REPEAT);
        }

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnTick>(OnVoteHudTick);

        // HookMode.Pre so returning HookResult.Handled suppresses the chat message —
        // plugin commands and vote/menu input are processed but never broadcast.
        _playerChatDelegate = OnPlayerChat;
        AddCommandListener("say", _playerChatDelegate, HookMode.Pre);
        AddCommandListener("say_team", _playerChatDelegate, HookMode.Pre);

        AddCommand("css_dumpmaps", "Dump all available map names to console", (caller, cmdInfo) =>
        {
            if (caller != null) { cmdInfo.ReplyToCommand("This command can only be used from the server console."); return; }
            RefreshMapPools(force: true);
            if (_availableMaps.Count == 0) { cmdInfo.ReplyToCommand("[CS2SimpleVote] No maps loaded yet. API may still be fetching."); return; }
            cmdInfo.ReplyToCommand($"--- CS2SimpleVote: {_availableMaps.Count} Available Maps (Collection: {Config.CollectionId}) ---");
            foreach (var map in _availableMaps.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
            {
                cmdInfo.ReplyToCommand($"  {map.Name}  (ID: {map.Id})");
            }
            cmdInfo.ReplyToCommand($"--- End ({_availableMaps.Count} maps loaded) ---");
        });

        AddCommand("css_setnextmap", "Set the next map by partial name match", (caller, cmdInfo) =>
        {
            if (caller != null)
            {
                if (!IsVoteAdmin(caller))
                {
                    cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                    return;
                }
            }
            string searchTerm = cmdInfo.GetArg(1);
            if (string.IsNullOrEmpty(searchTerm)) { cmdInfo.ReplyToCommand("[CS2SimpleVote] Usage: css_setnextmap <partial map name>"); return; }
            RefreshMapPools(force: true);

            var match = FindBestMapMatch(searchTerm);
            if (match == null) { cmdInfo.ReplyToCommand($"[CS2SimpleVote] No map found matching: {searchTerm}"); return; }

            _pendingMapId = match.Id;
            _nextMapName = match.Name;
            _nextMapSetByAdmin = true;
            _voteFinished = true;

            Log("ADMIN", $"{PlayerTag(caller)} ran css_setnextmap -> {match.Name} ({match.Id})");
            cmdInfo.ReplyToCommand($"[CS2SimpleVote] Next map set to: {match.Name} (ID: {match.Id})");
            Server.PrintToChatAll($" {ColorDefault}The next map has been set to {ColorGreen}{match.Name}{ColorDefault}.");
        });

        AddCommand("css_forcemap", "Force change to a map by partial name match", (caller, cmdInfo) =>
        {
            if (caller != null)
            {
                if (!IsVoteAdmin(caller))
                {
                    cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                    return;
                }
            }
            string searchTerm = cmdInfo.GetArg(1);
            if (string.IsNullOrEmpty(searchTerm)) { cmdInfo.ReplyToCommand("[CS2SimpleVote] Usage: css_forcemap <partial map name>"); return; }
            RefreshMapPools(force: true);

            var match = FindBestMapMatch(searchTerm);
            if (match == null) { cmdInfo.ReplyToCommand($"[CS2SimpleVote] No map found matching: {searchTerm}"); return; }

            Log("ADMIN", $"{PlayerTag(caller)} ran css_forcemap -> {match.Name} ({match.Id})");
            Log("MAPCHANGE", $"Forcing map change to {match.Name} ({match.Id})");
            cmdInfo.ReplyToCommand($"[CS2SimpleVote] Forcing map change to: {match.Name} (ID: {match.Id})");
            Server.PrintToChatAll($" {ColorDefault}Map is being changed to {ColorGreen}{match.Name}{ColorDefault}.");
            _expectedMapId = match.Id;
            _expectedMapName = match.Name;
            ChangeMap(match.Id);
        });

        AddCommand("css_forcertv", "Start an RTV-style map vote (map changes as soon as the vote ends)", (caller, cmdInfo) =>
        {
            if (caller != null && !IsVoteAdmin(caller))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            if (_matchEnded) { cmdInfo.ReplyToCommand("[CS2SimpleVote] Cannot start a vote after match end."); return; }
            if (_voteInProgress) { cmdInfo.ReplyToCommand("[CS2SimpleVote] A vote is already in progress."); return; }
            RefreshMapPools(force: true);
            if (_availableMaps.Count == 0) { cmdInfo.ReplyToCommand("[CS2SimpleVote] No maps loaded yet."); return; }

            Log("ADMIN", $"{PlayerTag(caller)} ran css_forcertv");
            Server.PrintToChatAll($" {ColorDefault}An {ColorGreen}RTV vote{ColorDefault} has been started! The map will change when the vote ends.");
            StartMapVote(isRtv: true);
        });

        AddCommand("css_addmap", "Add or re-enable a workshop map by ID (workshop_maps.json)", (caller, cmdInfo) =>
        {
            if (caller != null && !IsVoteAdmin(caller))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            AttemptAddMap(caller, cmdInfo.GetArg(1));
        });

        AddCommand("css_omitmap", "Disable maps matching the given words (removed from votes and nominations)", (caller, cmdInfo) =>
        {
            if (caller != null && !IsVoteAdmin(caller))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            AttemptOmitMap(caller, cmdInfo.ArgString);
        });

        AddCommand("css_unomitmap", "Re-enable maps matching the given words", (caller, cmdInfo) =>
        {
            if (caller != null && !IsVoteAdmin(caller))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            AttemptUnomitMap(caller, cmdInfo.ArgString);
        });

        AddCommand("css_addlist", "List manually added workshop maps (workshop_maps.json)", (caller, cmdInfo) =>
        {
            if (caller != null && !IsVoteAdmin(caller))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            if (caller != null) { PrintAddList(caller); return; }

            RefreshMapPools(force: true);
            if (_workshopMaps.Count == 0) { cmdInfo.ReplyToCommand("[CS2SimpleVote] No manually added workshop maps."); return; }
            cmdInfo.ReplyToCommand($"--- CS2SimpleVote: {_workshopMaps.Count} workshop_maps.json entr(ies) ---");
            foreach (var e in _workshopMaps.OrderBy(TitleOf, StringComparer.OrdinalIgnoreCase))
                cmdInfo.ReplyToCommand($"  {TitleOf(e)}  (ID: {e.Id}){(e.Enabled ? "" : "  [disabled]")}");
            cmdInfo.ReplyToCommand($"--- End ({_workshopMaps.Count} entr(ies)) ---");
        });

        AddCommand("css_syncstockmaps", "Force a re-scan of the engine's stock maps into stock_maps.json", (caller, cmdInfo) =>
        {
            if (caller != null && !IsVoteAdmin(caller))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            _stockSyncWarned = false;          // re-print the folder warning if it still applies
            ResolveEngineDirs();               // re-poll the engine in case paths changed
            LoadEngineMapTitles();
            SyncStockMapsConfig();
            RefreshMapPools(force: true);
            cmdInfo.ReplyToCommand($"[CS2SimpleVote] Stock map sync complete: {_stockMaps.Count} stock map(s) tracked ({_stockMaps.Count(e => e.Enabled)} enabled). Maps dir: {_engineMapsDir}");
        });
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;

        // 1. Cancel background tasks (FetchCollectionMaps) first
        _cts.Cancel();

        // 2. Kill all timers to prevent any further callbacks
        _reminderTimer?.Kill();
        _reminderTimer = null;
        _mapInfoTimer?.Kill();
        _mapInfoTimer = null;
        _centerMessageTimer?.Kill();
        _centerMessageTimer = null;
        _voteEndTimer?.Kill();
        _voteEndTimer = null;
        _mapChangeTimer?.Kill();
        _mapChangeTimer = null;
        _collectionRefreshTimer?.Kill();
        _collectionRefreshTimer = null;

        // 3. Clear collections to release references
        _availableMaps.Clear();
        _recentMaps.Clear();
        _collectionMaps.Clear();
        _workshopMaps.Clear();
        _stockMaps.Clear();
        _legacyOmitPatterns.Clear();
        _engineMapTitles.Clear();
        _rtvVoters.Clear();
        _activeVoteOptions.Clear();
        _playerVotes.Clear();
        _nominatedMaps.Clear();
        _hasNominatedSteamIds.Clear();
        _nominationOwner.Clear();
        _nominationNames.Clear();
        _nominatingPlayers.Clear();
        _playerNominationPage.Clear();
        _forcemapPlayers.Clear();
        _playerForcemapPage.Clear();
        _setnextmapPlayers.Clear();
        _playerSetNextMapPage.Clear();

        // 6. Remove listeners and handlers
        DeregisterEventHandler<EventRoundStart>(OnRoundStart);
        DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
        DeregisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        RemoveListener<Listeners.OnTick>(OnVoteHudTick);
        _voteCenterHtmlCache = "";

        if (_playerChatDelegate != null)
        {
            RemoveCommandListener("say", _playerChatDelegate, HookMode.Pre);
            RemoveCommandListener("say_team", _playerChatDelegate, HookMode.Pre);
            _playerChatDelegate = null;
        }

        // 7. Dispose managed resources and recreate for potential hot reload
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        try { _httpClient.Dispose(); } catch { }
        _httpClient = new HttpClient();
    }

    private void OnMapStart(string mapName)
    {
        ResetState();
        Server.ExecuteCommand("mp_endmatch_votenextmap 0");

        // Re-read the main config so manual edits to CS2SimpleVote.json take effect
        // on the next map without a reload/restart.
        ReloadConfigFromDisk();

        // Re-read every hand-editable map file so manual edits to collection_maps.json
        // / workshop_maps.json / stock_maps.json are picked up without a reload.
        // (The stock ENGINE scan itself only runs at plugin launch on a build change.)
        RefreshMapPools(force: true);

        // Always resolve which workshop item we're now playing (consumes _expectedMapId),
        // and record it into the recent-maps history if that feature is enabled.
        ResolveCurrentMapAndUpdateHistory(mapName);

        if (Config.EnableMapMessage && Config.CurrentMapMessageInterval > 0)
        {
            _mapInfoTimer = AddTimer(Config.CurrentMapMessageInterval, () =>
            {
                if (_unloaded) return;
                // Prefer the resolved workshop title; fall back to the engine map name
                string displayMapName = GetMapName(_currentMapId ?? mapName);
                Server.PrintToChatAll(Config.ShowServerNameInMapMessage
                    ? $" {ColorDefault}You're playing {ColorGreen}{displayMapName}{ColorDefault} on {ColorGreen}{Config.ServerName}{ColorDefault}!"
                    : $" {ColorDefault}You're playing {ColorGreen}{displayMapName}{ColorDefault}!");
            }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        }
    }

    private void ResetState()
    {
        _matchEnded = false;
        _nextMapSetByAdmin = false;
        _voteInProgress = false;
        _voteFinished = false;
        _isScheduledVote = false;
        _isForceVote = false;
        _isRtvVote = false;
        _currentVoteRoundDuration = 0;
        _nextMapName = null;
        _pendingMapId = null;
        _previousWinningMapId = null;
        _previousWinningMapName = null;
        _voteIsTimed = false;
        _voteEndsAtUtc = DateTime.MinValue;
        _voteTotalSeconds = 0f;
        _voteCenterHtmlCache = "";

        _rtvVoters.Clear();
        _playerVotes.Clear();
        _activeVoteOptions.Clear();
        _nominatedMaps.Clear();
        _hasNominatedSteamIds.Clear();
        _nominationOwner.Clear();
        _nominationNames.Clear();
        _nominatingPlayers.Clear();
        _playerNominationPage.Clear();
        _forcemapPlayers.Clear();
        _playerForcemapPage.Clear();
        _setnextmapPlayers.Clear();
        _playerSetNextMapPage.Clear();
        _helpMenuPlayers.Clear();

        _reminderTimer?.Kill();
        _reminderTimer = null;

        _mapInfoTimer?.Kill();
        _mapInfoTimer = null;

        _centerMessageTimer?.Kill();
        _centerMessageTimer = null;

        _voteEndTimer?.Kill();
        _voteEndTimer = null;

        _mapChangeTimer?.Kill();
        _mapChangeTimer = null;

    }

    // --- File Persistence ---

    private void LoadMapHistory()
    {
        if (!File.Exists(_historyFilePath)) return;

        string json = File.ReadAllText(_historyFilePath);

        // Try loading the new MapItem format first
        try
        {
            var loaded = JsonSerializer.Deserialize<List<MapItem>>(json);
            if (loaded != null && loaded.Count > 0 && !string.IsNullOrEmpty(loaded[0].Id))
            {
                _recentMaps = loaded;
                return;
            }
        }
        catch { /* Not MapItem format, try legacy */ }

        // Migrate legacy List<string> format
        try
        {
            var legacyIds = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            _recentMaps = new List<MapItem>();
            foreach (var raw in legacyIds)
            {
                string id = raw;
                // Extract numeric ID from engine paths like "workshop/123456/de_map"
                var segments = raw.Split('/');
                if (segments.Length >= 2 && segments[0].Equals("workshop", StringComparison.OrdinalIgnoreCase)
                    && segments[1].Length > 0 && segments[1].All(char.IsDigit))
                {
                    id = segments[1];
                }
                _recentMaps.Add(new MapItem { Id = id, Name = id });
            }
            Console.WriteLine($"[CS2SimpleVote] Migrated {_recentMaps.Count} legacy recent map entries.");
        }
        catch { _recentMaps = new List<MapItem>(); }
    }

    private void SaveMapHistory()
    {
        // Small file, written once per map change. A synchronous write keeps history
        // updates strictly ordered — offloaded Task.Run writes could land out of
        // order and persist stale history.
        try { File.WriteAllText(_historyFilePath, JsonSerializer.Serialize(_recentMaps)); }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save history: {ex.Message}"); }
    }

    // --- Tracked map files (collection_maps.json / workshop_maps.json) ---
    // Both files share the same one-entry-per-line format:
    //   { "id": "3070321328", "title": "Dust2 Remake", "enabled": true },
    // collection_maps.json: membership and titles are synced from the Steam collection
    // (adds appear enabled, removals are pruned); ONLY the "enabled" flags belong to
    // the file. workshop_maps.json: fully hand-owned — add a line with just an "id"
    // and the title fills in on the next collection refresh.
    //
    // Threading contract: these files are read AND written exclusively on the game
    // thread, synchronously. Writes only happen when content actually changed, so the
    // common refresh path is read-only. This is what makes realtime hand-edits safe:
    // there is no async writer that could clobber an edit or read stale state.

    private static string TitleOf(TrackedMapEntry e) => string.IsNullOrWhiteSpace(e.Title) ? e.Id : e.Title;

    private List<TrackedMapEntry> LoadTrackedFile(string path, string label, List<TrackedMapEntry> fallback)
    {
        try
        {
            if (!File.Exists(path)) return new List<TrackedMapEntry>();
            var loaded = JsonSerializer.Deserialize<List<TrackedMapEntry>>(File.ReadAllText(path), TolerantJson);
            return loaded?.Where(e => !string.IsNullOrWhiteSpace(e.Id)).ToList() ?? new List<TrackedMapEntry>();
        }
        catch (Exception ex)
        {
            // A malformed file (probably mid-edit) keeps the previous in-memory list
            // and is never overwritten — the next refresh retries the read.
            Console.WriteLine($"[CS2SimpleVote] Failed to load {label}: {ex.Message} — keeping previous list.");
            return fallback;
        }
    }

    private static string RenderTrackedJson(IEnumerable<TrackedMapEntry> entries)
    {
        var sorted = entries
            .OrderBy(TitleOf, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sb = new StringBuilder();
        sb.AppendLine("[");
        for (int i = 0; i < sorted.Count; i++)
        {
            var e = sorted[i];
            sb.Append("  { \"id\": ").Append(JsonSerializer.Serialize(e.Id))
              .Append(", \"title\": ").Append(JsonSerializer.Serialize(e.Title))
              .Append(", \"enabled\": ").Append(e.Enabled ? "true" : "false").Append(" }");
            sb.AppendLine(i < sorted.Count - 1 ? "," : "");
        }
        sb.Append(']');
        return sb.ToString();
    }

    private void WriteTrackedFile(string path, List<TrackedMapEntry> entries, string label)
    {
        try
        {
            string text = RenderTrackedJson(entries);
            string old = File.Exists(path) ? File.ReadAllText(path) : "";
            if (string.Equals(old, text, StringComparison.Ordinal)) return;
            File.WriteAllText(path, text);
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save {label}: {ex.Message}"); }
    }

    // Builds the map pool from the three sources, deduped by ID (collection wins,
    // then workshop, then stock).
    private List<MapItem> BuildPool(bool enabledOnly)
    {
        var list = new List<MapItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _collectionMaps)
            if ((!enabledOnly || e.Enabled) && seen.Add(e.Id)) list.Add(new MapItem { Id = e.Id, Name = TitleOf(e) });
        foreach (var e in _workshopMaps)
            if ((!enabledOnly || e.Enabled) && seen.Add(e.Id)) list.Add(new MapItem { Id = e.Id, Name = TitleOf(e) });
        foreach (var e in _stockMaps)
            if ((!enabledOnly || e.Enabled) && seen.Add(e.Map)) list.Add(new MapItem { Id = e.Map, Name = string.IsNullOrWhiteSpace(e.Title) ? e.Map : e.Title });
        return list;
    }

    // Single atomic reference swap — code already holding the previous list keeps a
    // valid snapshot; new reads see the new pool.
    private void RebuildAvailableMaps() => _availableMaps = BuildPool(enabledOnly: true);

    // Every map the plugin knows about, including disabled ones. Admin commands
    // (!forcemap / !setnextmap) and name lookups deliberately see everything.
    private List<MapItem> AllKnownMaps() => BuildPool(enabledOnly: false);

    // Re-reads every hand-editable map file and rebuilds the live pool. Runs before
    // every vote and menu so realtime edits are always honored. The throttle bounds
    // disk reads on player-spammable paths (!nominate etc.); vote starts and admin
    // mutations pass force: true and always see the latest files.
    // First-launch templates: an empty list plus commented syntax so hand-editing
    // needs no guesswork. Comments are skipped by the tolerant reader, and both
    // files are replaced with real data the first time the plugin writes them.
    private const string CollectionMapsTemplate =
        "[\n" +
        "  // collection_maps.json — auto-synced from your Steam Workshop collection.\n" +
        "  // This file fills itself in on the first successful fetch. You only manage\n" +
        "  // the \"enabled\" flags (false = omitted from votes); membership and titles\n" +
        "  // always follow the collection, so hand-added lines here get pruned.\n" +
        "  // Syntax:\n" +
        "  //   { \"id\": \"3070321328\", \"title\": \"Dust2 Remake\", \"enabled\": true }\n" +
        "]";

    private const string WorkshopMapsTemplate =
        "[\n" +
        "  // workshop_maps.json — YOUR manually added workshop maps, one per line\n" +
        "  // (!addmap writes here too). \"title\" may be left \"\" — it auto-fills from\n" +
        "  // Steam on the next collection refresh. \"enabled\": false omits the map.\n" +
        "  // Syntax (uncomment and fill in the id to add a map):\n" +
        "  //   { \"id\": \"\", \"title\": \"\", \"enabled\": true }\n" +
        "]";

    private void WriteTemplateIfMissing(string path, string template, string label)
    {
        try
        {
            if (File.Exists(path)) return;
            File.WriteAllText(path, template);
            Console.WriteLine($"[CS2SimpleVote] Generated {label} template.");
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Could not generate {label}: {ex.Message}"); }
    }

    private void RefreshMapPools(bool force = false)
    {
        if (!force && (DateTime.UtcNow - _lastPoolsRefresh).TotalSeconds < 2.0) return;
        _lastPoolsRefresh = DateTime.UtcNow;

        // Stock maps: the engine scan runs only at plugin launch (and only when the
        // game build changed) — here we just re-read the file so hand-edited
        // "enabled" flags apply in realtime. A deleted file is regenerated from the
        // engine on the spot.
        if (!File.Exists(_stockMapsFilePath))
        {
            if (_engineMapTitles.Count == 0) LoadEngineMapTitles();
            SyncStockMapsConfig();
        }
        else
        {
            _stockMaps = LoadStockMapsFile();
        }

        // collection_maps.json is engine-owned (membership comes from Steam), so if
        // the user deletes it, regenerate it from memory instead of dropping the
        // whole collection pool until the next fetch cycle. workshop_maps.json is
        // fully hand-owned — deleting it genuinely means "clear my manual list",
        // and it comes back as the commented template.
        if (!File.Exists(_collectionMapsFilePath))
        {
            if (_collectionMaps.Count > 0)
                WriteTrackedFile(_collectionMapsFilePath, _collectionMaps, "collection_maps.json");
            else
                WriteTemplateIfMissing(_collectionMapsFilePath, CollectionMapsTemplate, "collection_maps.json");
        }
        _collectionMaps = LoadTrackedFile(_collectionMapsFilePath, "collection_maps.json", _collectionMaps);

        WriteTemplateIfMissing(_workshopMapsFilePath, WorkshopMapsTemplate, "workshop_maps.json");
        _workshopMaps = LoadTrackedFile(_workshopMapsFilePath, "workshop_maps.json", _workshopMaps);
        RebuildAvailableMaps();
    }

    // Reconciles collection_maps.json with a completed Steam fetch (game thread, via
    // Server.NextFrame). The fetch owns membership and titles; the file — re-read here
    // so flag edits made while the fetch was in flight are honored — owns "enabled".
    // New maps default to enabled, unless a migrated legacy omit pattern matches them.
    private void SyncCollectionMapsFromFetch(List<MapItem> fetched, bool isInitial)
    {
        var fileEntries = LoadTrackedFile(_collectionMapsFilePath, "collection_maps.json", _collectionMaps);
        var flagsById = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in fileEntries) flagsById[e.Id] = e.Enabled;

        var synced = fetched.Select(f => new TrackedMapEntry
        {
            Id = f.Id,
            Title = f.Name,
            Enabled = flagsById.TryGetValue(f.Id, out var en)
                ? en
                : !_legacyOmitPatterns.Any(p => MapMatchesPattern(f.Name, p))
        }).ToList();

        var fetchedIds = new HashSet<string>(fetched.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);
        int added = synced.Count(e => !flagsById.ContainsKey(e.Id));
        int removed = fileEntries.Count(e => !fetchedIds.Contains(e.Id));

        _collectionMaps = synced;
        WriteTrackedFile(_collectionMapsFilePath, synced, "collection_maps.json");

        string change = (added == 0 && removed == 0) ? "no changes" : $"+{added} added, -{removed} removed";
        Log("FETCH", $"{(isInitial ? "Initial fetch" : "Refresh")} complete: {synced.Count} collection maps ({change}).");
        Console.WriteLine($"[CS2SimpleVote] Collection {(isInitial ? "loaded" : "refreshed")}: {synced.Count} maps ({change}).");
    }

    // Fills in titles for hand-added workshop_maps.json entries that were left blank.
    // Called on the game thread after a fetch that resolved them.
    private void BackfillWorkshopTitles(Dictionary<string, string> titles)
    {
        if (titles.Count == 0) return;
        _workshopMaps = LoadTrackedFile(_workshopMapsFilePath, "workshop_maps.json", _workshopMaps);
        bool changed = false;
        foreach (var e in _workshopMaps)
        {
            if (string.IsNullOrWhiteSpace(e.Title) && titles.TryGetValue(e.Id, out var t) && !string.IsNullOrWhiteSpace(t))
            {
                e.Title = t;
                changed = true;
            }
        }
        if (changed)
        {
            WriteTrackedFile(_workshopMapsFilePath, _workshopMaps, "workshop_maps.json");
            Log("FETCH", "Resolved titles for manually added workshop maps.");
        }
    }

    // One-time migration from the pre-1.5 layout. custom_maps.json becomes
    // workshop_maps.json, omit word-patterns are applied as disabled flags (and kept
    // in memory so collection maps first seen later this session start disabled when
    // they match), map_cache.json seeds collection_maps.json. Originals become .bak.
    private void MigrateLegacyFiles(string configDir)
    {
        string customPath = Path.Combine(configDir, "custom_maps.json");
        string omittedPath = Path.Combine(configDir, "omitted_maps.json");
        string cachePath = Path.Combine(configDir, "map_cache.json");

        try
        {
            if (File.Exists(omittedPath))
            {
                var patterns = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(omittedPath), TolerantJson) ?? new List<string>();
                _legacyOmitPatterns = patterns.Select(NormalizePattern).Where(p => p.Length > 0).Distinct().ToList();
                File.Move(omittedPath, omittedPath + ".bak", true);
                if (_legacyOmitPatterns.Count > 0)
                    Console.WriteLine($"[CS2SimpleVote] Migrated {_legacyOmitPatterns.Count} omit pattern(s) — matching maps will be written as \"enabled\": false.");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] omitted_maps.json migration failed: {ex.Message}"); }

        try
        {
            if (!File.Exists(_workshopMapsFilePath) && File.Exists(customPath))
            {
                var custom = JsonSerializer.Deserialize<List<MapItem>>(File.ReadAllText(customPath), TolerantJson) ?? new List<MapItem>();
                var entries = custom
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .Select(m => new TrackedMapEntry
                    {
                        Id = m.Id,
                        Title = m.Name,
                        Enabled = !_legacyOmitPatterns.Any(p => MapMatchesPattern(m.Name, p))
                    })
                    .ToList();
                WriteTrackedFile(_workshopMapsFilePath, entries, "workshop_maps.json");
                File.Move(customPath, customPath + ".bak", true);
                Console.WriteLine($"[CS2SimpleVote] Migrated {entries.Count} custom map(s) to workshop_maps.json.");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] custom_maps.json migration failed: {ex.Message}"); }

        try
        {
            if (!File.Exists(_collectionMapsFilePath) && File.Exists(cachePath))
            {
                var cached = JsonSerializer.Deserialize<List<MapItem>>(File.ReadAllText(cachePath), TolerantJson) ?? new List<MapItem>();
                var entries = cached
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .Select(m => new TrackedMapEntry
                    {
                        Id = m.Id,
                        Title = m.Name,
                        Enabled = !_legacyOmitPatterns.Any(p => MapMatchesPattern(m.Name, p))
                    })
                    .ToList();
                WriteTrackedFile(_collectionMapsFilePath, entries, "collection_maps.json");
                Console.WriteLine($"[CS2SimpleVote] Seeded collection_maps.json from map_cache.json ({entries.Count} maps).");
            }
            if (File.Exists(cachePath)) File.Delete(cachePath);
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] map_cache.json migration failed: {ex.Message}"); }
    }

    // --- Stock Maps (stock_maps.json) ---
    // The stock map pool is generated straight from the server engine, not curated by
    // hand: map names come from the .vpk files in the game's maps/ folder, display
    // titles from the SFUI_Map_* entries in resource/csgo_english.txt. The engine
    // sync runs at plugin launch, and only when the game build (steam.inf) differs
    // from last_synced_build in the main config — so a Valve update lands exactly
    // once: new maps are written disabled, removed maps are deleted, and hand-edited
    // "enabled" flags are always preserved. Between syncs the file itself is the
    // source of truth and is re-read live. One entry per line:
    //   { "map": "de_dust2", "title": "Dust II", "enabled": false },
    // sorted by prefix (ar_, cs_, de_, ...) then alphabetically.

    private static readonly Regex SfuiMapTitleRegex = new("^\\s*\"SFUI_Map_([^\"]+)\"\\s+\"(.+)\"", RegexOptions.Compiled);

    // Polls the engine for the game directory and locates maps/, resource/ and
    // steam.inf from it. Candidates, in order: Server.GameDirectory both as the
    // ".../game" root (append csgo) and as ".../game/csgo" directly, then the
    // plugin-relative walk (ModuleDirectory is ".../csgo/addons/counterstrikesharp/
    // plugins/CS2SimpleVote", so four levels up is csgo). First candidate that
    // actually contains a maps/ folder wins.
    private void ResolveEngineDirs()
    {
        var candidates = new List<string>();
        try
        {
            string gd = Server.GameDirectory;
            if (!string.IsNullOrWhiteSpace(gd))
            {
                candidates.Add(Path.Combine(gd, "csgo"));
                candidates.Add(gd);
            }
        }
        catch { /* engine not available (e.g. tests) — fall through */ }
        try { candidates.Add(Path.GetFullPath(Path.Combine(ModuleDirectory, "../../../.."))); } catch { }

        string? csgoDir = candidates.FirstOrDefault(c =>
        {
            try { return Directory.Exists(Path.Combine(c, "maps")); }
            catch { return false; }
        });

        if (csgoDir == null)
        {
            csgoDir = candidates.LastOrDefault() ?? "";
            Console.WriteLine($"[CS2SimpleVote] WARNING: no maps/ folder found in any engine dir candidate: {string.Join(" | ", candidates)} — stock map sync will be inactive until css_syncstockmaps finds one.");
        }
        else
        {
            Console.WriteLine($"[CS2SimpleVote] Engine dir resolved: {csgoDir}");
        }

        _engineMapsDir = Path.Combine(csgoDir, "maps");
        _engineLocalizationPath = Path.Combine(csgoDir, "resource", "csgo_english.txt");
        _engineSteamInfPath = Path.Combine(csgoDir, "steam.inf");
    }

    // Reads the game build number from the engine's steam.inf (ServerVersion=NNNNN).
    // Returns 0 when unavailable — the caller then falls back to syncing stock maps
    // every launch rather than never.
    private int ReadEngineBuild()
    {
        try
        {
            if (string.IsNullOrEmpty(_engineSteamInfPath) || !File.Exists(_engineSteamInfPath)) return 0;
            foreach (var line in File.ReadLines(_engineSteamInfPath))
            {
                if ((line.StartsWith("ServerVersion=", StringComparison.OrdinalIgnoreCase) ||
                     line.StartsWith("ClientVersion=", StringComparison.OrdinalIgnoreCase)) &&
                    int.TryParse(line[(line.IndexOf('=') + 1)..].Trim(), out int v) && v > 0)
                {
                    return v;
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Could not read steam.inf: {ex.Message}"); }
        return 0;
    }

    // Workshop items have purely numeric IDs; stock maps use the engine map name as
    // their Id. This is what routes map changes, omit matching, and current-map checks.
    private static bool IsWorkshopId(string id) => id.Length > 0 && id.All(char.IsDigit);

    // Stock maps (id = engine map name) load via changelevel; workshop items via
    // host_workshop_map. Every plugin-initiated map change must go through here.
    private void ChangeMap(string id)
        => Server.ExecuteCommand(IsWorkshopId(id) ? $"host_workshop_map {id}" : $"changelevel {id}");

    // Parses the engine's own localization file once per plugin load. Runs at Load
    // (not per map start) because the file is several MB.
    private void LoadEngineMapTitles()
    {
        _engineMapTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_engineLocalizationPath)) return;
            // Valve localization files have shipped as both UTF-8 and UTF-16 over time;
            // BOM detection handles either.
            using var reader = new StreamReader(_engineLocalizationPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var m = SfuiMapTitleRegex.Match(line);
                if (m.Success) _engineMapTitles[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Could not parse engine map titles: {ex.Message}");
        }
    }

    // Lists the stock maps the engine can actually load right now (maps/*.vpk).
    // Returns null when the folder isn't where a standard install puts it, so the
    // caller can fall back to the existing config file instead of wiping it.
    private List<string>? ScanEngineStockMaps()
    {
        try
        {
            if (string.IsNullOrEmpty(_engineMapsDir) || !Directory.Exists(_engineMapsDir))
            {
                if (!_stockSyncWarned)
                {
                    _stockSyncWarned = true;
                    Console.WriteLine($"[CS2SimpleVote] Engine maps folder not found at '{_engineMapsDir}' — stock map auto-sync disabled; stock_maps.json will be used as-is.");
                }
                return null;
            }

            var names = new List<string>();
            foreach (var file in Directory.EnumerateFiles(_engineMapsDir, "*.vpk", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                // Non-playable vpks that live alongside real maps.
                if (name.EndsWith("_vanity", StringComparison.OrdinalIgnoreCase)) continue;      // main-menu scenes
                if (name.StartsWith("lobby_", StringComparison.OrdinalIgnoreCase)) continue;     // lobby/veto scenes
                if (name.StartsWith("workshop_preview", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("graphics_settings", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(name);
            }
            return names;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Failed to scan engine maps folder: {ex.Message}");
            return null;
        }
    }

    // "de_dust2" -> "Dust2", "ar_pool_day" -> "Pool Day". Fallback for maps the
    // localization file has no title for.
    private static string PrettifyMapName(string mapName)
    {
        var parts = mapName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        int start = parts.Length > 1 && parts[0].Length <= 4 ? 1 : 0; // drop mode prefixes (de/cs/ar/dz/...)
        string s = string.Join(' ', parts.Skip(start).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        return s.Length > 0 ? s : mapName;
    }

    private static string MapPrefix(string mapName)
    {
        int i = mapName.IndexOf('_');
        return i > 0 ? mapName[..i] : mapName;
    }

    private List<StockMapEntry> LoadStockMapsFile()
    {
        try
        {
            if (!File.Exists(_stockMapsFilePath)) return new List<StockMapEntry>();
            var loaded = JsonSerializer.Deserialize<List<StockMapEntry>>(File.ReadAllText(_stockMapsFilePath), TolerantJson);
            return loaded?.Where(e => !string.IsNullOrWhiteSpace(e.Map)).ToList() ?? new List<StockMapEntry>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Failed to load stock_maps.json: {ex.Message} — keeping previous list.");
            return _stockMaps; // keep last known good list rather than dropping everything
        }
    }

    // Writes the config as one entry per line so it's trivially hand-editable.
    // Only touches the disk when the rendered content actually changed.
    private void WriteStockMapsFile(List<StockMapEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[");
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            sb.Append("  { \"map\": ").Append(JsonSerializer.Serialize(e.Map))
              .Append(", \"title\": ").Append(JsonSerializer.Serialize(e.Title))
              .Append(", \"enabled\": ").Append(e.Enabled ? "true" : "false").Append(" }");
            sb.AppendLine(i < entries.Count - 1 ? "," : "");
        }
        sb.Append(']');
        string text = sb.ToString();

        try
        {
            string old = File.Exists(_stockMapsFilePath) ? File.ReadAllText(_stockMapsFilePath) : "";
            if (string.Equals(old, text, StringComparison.Ordinal)) return;
            File.WriteAllText(_stockMapsFilePath, text);
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save stock_maps.json: {ex.Message}"); }
    }

    // Reconciles stock_maps.json with what the engine actually ships right now.
    // Engine is the source of truth for which maps exist and what they're titled;
    // the config file is only the source of truth for the "enabled" flags.
    private void SyncStockMapsConfig()
    {
        var existing = LoadStockMapsFile();

        var engineMaps = ScanEngineStockMaps();
        if (engineMaps == null)
        {
            // No engine folder to sync against — honor the file as-is.
            _stockMaps = existing;
            return;
        }

        var byName = existing
            .GroupBy(e => e.Map, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var synced = engineMaps
            .Select(name => new StockMapEntry
            {
                Map = name,
                Title = _engineMapTitles.TryGetValue(name, out var t) && !string.IsNullOrWhiteSpace(t) ? t : PrettifyMapName(name),
                Enabled = byName.TryGetValue(name, out var prev) && prev.Enabled
            })
            .OrderBy(e => MapPrefix(e.Map), StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Map, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var syncedNames = new HashSet<string>(synced.Select(e => e.Map), StringComparer.OrdinalIgnoreCase);
        int added = synced.Count(e => !byName.ContainsKey(e.Map));
        int removed = existing.Count(e => !syncedNames.Contains(e.Map));

        _stockMaps = synced;
        WriteStockMapsFile(synced);

        if (added > 0 || removed > 0)
        {
            Log("STOCK", $"Stock map sync: +{added} added, -{removed} removed ({synced.Count} total, {synced.Count(e => e.Enabled)} enabled).");
            Console.WriteLine($"[CS2SimpleVote] Stock maps synced: +{added} added, -{removed} removed ({synced.Count} total).");
        }
    }

    // --- Omit matching helpers ---
    // "Omitted" now simply means "enabled": false in the map's file. These helpers
    // implement the word matching !omitmap/!unomitmap use to find which entries to
    // toggle: a map matches when its title (or, for stock maps, its engine name)
    // contains EVERY word of the pattern, case-insensitively and in any order.

    // Lowercase, collapse whitespace: "  Motel   NIGHT " -> "motel night"
    private static string NormalizePattern(string s)
        => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();

    private static bool MapMatchesPattern(string mapName, string pattern)
    {
        var words = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        return words.All(w => mapName.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // Tracked (workshop-ID) entries match on title, or on an exact ID so
    // "!omitmap 3070321328" targets one specific map.
    private static bool TrackedMatchesPattern(TrackedMapEntry e, string pattern)
        => MapMatchesPattern(TitleOf(e), pattern) || pattern.Equals(e.Id, StringComparison.OrdinalIgnoreCase);

    // Stock entries match on title or engine map name ("!omitmap de_dust2").
    private static bool StockMatchesPattern(StockMapEntry e, string pattern)
        => MapMatchesPattern(e.Title, pattern) || MapMatchesPattern(e.Map, pattern);

    private void ResolveCurrentMapAndUpdateHistory(string currentMapName)
    {
        string? idToAdd = null;
        string? nameToAdd = null;
        // Full set including disabled entries — the current map must resolve and be
        // recorded in history even if it was just omitted.
        var knownMaps = AllKnownMaps();

        // 1) Authoritative source: the workshop ID this plugin itself passed to
        //    host_workshop_map right before the transition. This is what makes the
        //    recent-maps exclusion reliable — the engine map name for workshop maps
        //    (e.g. "de_dust2_remake") usually contains NEITHER the workshop ID nor
        //    the workshop title, so name-based matching alone can never work.
        if (!string.IsNullOrEmpty(_expectedMapId))
        {
            idToAdd = _expectedMapId;
            nameToAdd = knownMaps.FirstOrDefault(m => m.Id == _expectedMapId)?.Name
                        ?? _expectedMapName
                        ?? _expectedMapId;
        }
        _expectedMapId = null;
        _expectedMapName = null;

        // 2) Fallback (map changed outside the plugin): ID embedded in the engine path,
        //    then normalized title comparison ("De_Dust2 [24/7]" vs "de_dust2").
        if (idToAdd == null)
        {
            // Same numeric-vs-stock split as IsCurrentMap: Contains for workshop IDs
            // embedded in engine paths, exact name equality for stock maps.
            var mapItem = knownMaps.FirstOrDefault(m => !string.IsNullOrEmpty(m.Id) &&
                (IsWorkshopId(m.Id)
                    ? currentMapName.Contains(m.Id, StringComparison.OrdinalIgnoreCase)
                    : currentMapName.Split('/').Last().Equals(m.Id, StringComparison.OrdinalIgnoreCase)));

            if (mapItem == null)
            {
                string cleanName = NormalizeMapName(currentMapName.Split('/').Last());
                if (cleanName.Length >= 3)
                {
                    mapItem = knownMaps.FirstOrDefault(m =>
                    {
                        string n = NormalizeMapName(m.Name);
                        if (n.Length < 3) return false;
                        return n == cleanName
                            || (cleanName.Length >= 5 && n.Contains(cleanName))
                            || (n.Length >= 5 && cleanName.Contains(n));
                    });
                }
            }

            if (mapItem != null)
            {
                idToAdd = mapItem.Id;
                nameToAdd = mapItem.Name;
            }
        }

        // 3) Last resort: extract numeric workshop ID from paths like
        //    "workshop/3070321328/de_dust2", otherwise store the raw engine name.
        if (idToAdd == null)
        {
            var segments = currentMapName.Split('/');
            if (segments.Length >= 2 && segments[0].Equals("workshop", StringComparison.OrdinalIgnoreCase)
                && segments[1].Length > 0 && segments[1].All(char.IsDigit))
            {
                idToAdd = segments[1];
            }
            else
            {
                idToAdd = currentMapName;
            }
            nameToAdd = knownMaps.FirstOrDefault(m => m.Id == idToAdd)?.Name ?? idToAdd;
        }

        _currentMapId = idToAdd;

        if (!Config.OmitRecentMaps) return;

        _recentMaps.RemoveAll(m => m.Id.Equals(idToAdd, StringComparison.OrdinalIgnoreCase));
        _recentMaps.Add(new MapItem { Id = idToAdd, Name = nameToAdd ?? idToAdd });
        // The list always ends with the CURRENT map, so keep count+1 entries: the
        // current map plus the configured number of previous maps. Trimming to
        // exactly RecentMapsCount silently reduced the exclusion window by one.
        while (_recentMaps.Count > Config.RecentMapsCount + 1) _recentMaps.RemoveAt(0);

        // Backfill names for any legacy entries now that the map lists may be populated
        for (int i = 0; i < _recentMaps.Count; i++)
        {
            if (_recentMaps[i].Name == _recentMaps[i].Id || string.IsNullOrEmpty(_recentMaps[i].Name))
            {
                var known = knownMaps.FirstOrDefault(m => m.Id == _recentMaps[i].Id);
                if (known != null) _recentMaps[i].Name = known.Name;
            }
        }

        SaveMapHistory();
    }

    // Lowercased, letters and digits only: "De_Dust2 [24/7]" -> "dedust2247"
    private static string NormalizeMapName(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // Loose equality between a history entry and a collection map. Handles proper
    // workshop-ID entries, plus legacy entries where Id holds the raw engine name.
    private static bool IsSameMap(MapItem a, MapItem b)
    {
        if (!string.IsNullOrEmpty(a.Id) && a.Id.Equals(b.Id, StringComparison.OrdinalIgnoreCase)) return true;

        string an = NormalizeMapName(a.Name);
        string bn = NormalizeMapName(b.Name);
        if (an.Length >= 3 && an == bn) return true;

        // History entries recorded from engine-initiated map changes store the raw
        // engine map name in Id (e.g. "de_dust2"), which must still match both stock
        // maps (Id = engine name) and workshop titles. Compare Id-to-Id and Id-to-Name
        // in both directions so no combination slips through the recent-map filter.
        string ain = NormalizeMapName(a.Id);
        string bin = NormalizeMapName(b.Id);
        if (ain.Length >= 3 && ain == bin) return true;
        if (an.Length >= 3 && an == bin) return true;
        if (ain.Length >= 4 && bn.Length >= 4 && (ain == bn || ain.Contains(bn) || bn.Contains(ain))) return true;

        return false;
    }

    private bool IsRecentMap(MapItem map) => _recentMaps.Any(r => IsSameMap(r, map));

    // --- Steam API ---

    // Game-thread-only entry point. Decides whether to launch a background fetch,
    // sets the overlap guard, and dispatches the worker. Never call FetchCollectionMaps
    // directly from elsewhere — always go through here so the guard stays consistent.
    private void LaunchCollectionFetch(bool isInitial)
    {
        if (_unloaded) return;
        if (string.IsNullOrEmpty(Config.SteamApiKey) || string.IsNullOrEmpty(Config.CollectionId)) return;

        // Skip if a fetch is already in flight. This is the only writer of the guard
        // besides the completion callback, and both run on the game thread, so the
        // check-then-set is atomic with respect to other launches.
        if (_collectionFetchRunning) return;

        _collectionFetchRunning = true;
        _isApiLoading = true;
        var token = _cts.Token;

        // Snapshot (on the game thread) any hand-added workshop_maps.json entries
        // still missing a title, so the worker can resolve them in the same cycle.
        // The worker only ever sees this local copy — never the live list.
        var titleLookupIds = _workshopMaps
            .Where(e => string.IsNullOrWhiteSpace(e.Title) && IsWorkshopId(e.Id))
            .Select(e => e.Id)
            .Distinct()
            .ToList();

        Log("FETCH", isInitial ? "Initial collection fetch started." : "Background collection refresh started.");
        Task.Run(() => FetchCollectionMaps(isInitial, titleLookupIds, token));
    }

    private async Task FetchCollectionMaps(bool isInitial, List<string> titleLookupIds, CancellationToken token = default)
    {
        try
        {
            // Uses the modern IPublishedFileService/GetDetails endpoint with includechildren=true.
            // This returns, for each ID, its title, file_type, and (when it's a collection) its
            // children[] with file_type set. One endpoint handles both items and collections, so
            // the whole traversal is a BFS over GetDetails.
            //   file_type == 2  -> collection; enqueue each child
            //   file_type != 2  -> workshop item (map); take title
            // Items that come back with result != 1 (deleted/banned/private) are skipped.
            var maps = new List<MapItem>();
            var seenMaps = new HashSet<string>();
            var pending = new Queue<string>();
            var visited = new HashSet<string>();

            pending.Enqueue(Config.CollectionId);
            visited.Add(Config.CollectionId);
            bool rootResolved = false;
            int rootResultCode = 0;

            while (pending.Count > 0)
            {
                // GetDetails accepts many IDs per call; Steam caps it around a few hundred but
                // 100 per batch keeps URL length reasonable and matches legacy behavior.
                var batch = new List<string>();
                while (pending.Count > 0 && batch.Count < 100)
                    batch.Add(pending.Dequeue());

                var queryParts = new List<string> {
                    $"key={Uri.EscapeDataString(Config.SteamApiKey)}",
                    "includechildren=true"
                };
                for (int i = 0; i < batch.Count; i++)
                    queryParts.Add($"publishedfileids%5B{i}%5D={batch[i]}");
                var url = $"https://api.steampowered.com/IPublishedFileService/GetDetails/v1/?{string.Join("&", queryParts)}";

                var httpRes = await _httpClient.GetAsync(url, token);
                if (!httpRes.IsSuccessStatusCode)
                {
                    string err = await httpRes.Content.ReadAsStringAsync(token);
                    throw new Exception($"IPublishedFileService/GetDetails HTTP {(int)httpRes.StatusCode}: {err}");
                }
                string json = await httpRes.Content.ReadAsStringAsync(token);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("response", out var respEl) ||
                    !respEl.TryGetProperty("publishedfiledetails", out var detailsArr))
                {
                    if (batch.Contains(Config.CollectionId))
                        throw new Exception("Steam API returned no publishedfiledetails for the root collection. Check Collection ID.");
                    continue;
                }

                foreach (var item in detailsArr.EnumerateArray())
                {
                    string? id = item.TryGetProperty("publishedfileid", out var idp) ? idp.GetString() : null;
                    if (string.IsNullOrEmpty(id)) continue;

                    int result = item.TryGetProperty("result", out var resp) ? resp.GetInt32() : 0;
                    int fileType = item.TryGetProperty("file_type", out var ftp) ? ftp.GetInt32() : 0;
                    bool isRoot = id == Config.CollectionId;

                    if (isRoot)
                    {
                        rootResolved = true;
                        rootResultCode = result;
                    }

                    if (result != 1)
                    {
                        if (isRoot)
                            throw new Exception($"Root collection is inaccessible. Steam result code: {result}. Ensure the Steam API Key is valid and the collection is public.");
                        Console.WriteLine($"[CS2SimpleVote] Skipping {id}: Steam result code {result} (likely deleted/private/banned).");
                        continue;
                    }

                    if (fileType == 2)
                    {
                        // Collection: enqueue each unvisited child.
                        if (item.TryGetProperty("children", out var childrenArr) && childrenArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var child in childrenArr.EnumerateArray())
                            {
                                string? cid = child.TryGetProperty("publishedfileid", out var cidp) ? cidp.GetString() : null;
                                if (string.IsNullOrEmpty(cid)) continue;
                                if (visited.Add(cid))
                                    pending.Enqueue(cid);
                            }
                        }
                    }
                    else
                    {
                        // Workshop item (map).
                        string? title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                        if (string.IsNullOrEmpty(title))
                        {
                            Console.WriteLine($"[CS2SimpleVote] Item {id} returned without a title — skipping.");
                            continue;
                        }
                        if (seenMaps.Add(id))
                            maps.Add(new MapItem { Id = id, Name = title });
                    }
                }
            }

            if (!rootResolved)
                throw new Exception("Root collection was not returned by Steam API. Check Collection ID.");

            if (maps.Count == 0)
                throw new Exception($"No maps resolved from collection {Config.CollectionId} (root result code: {rootResultCode}).");

            // Resolve titles for hand-added workshop_maps.json entries in the same
            // cycle. A failure here must not sink the collection refresh.
            var extraTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (titleLookupIds.Count > 0)
            {
                try { extraTitles = await FetchTitlesBatch(titleLookupIds, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Workshop title lookup failed: {ex.Message}"); }
            }

            // Everything that touches plugin state — the collection_maps.json sync,
            // title backfill, and pool rebuild — is marshalled onto the game thread.
            // The worker only ever handled its own local lists.
            Server.NextFrame(() =>
            {
                if (_unloaded) { _collectionFetchRunning = false; _isApiLoading = false; return; }

                SyncCollectionMapsFromFetch(maps, isInitial);
                BackfillWorkshopTitles(extraTitles);
                RebuildAvailableMaps();

                _hasLoadedCollectionMaps = true;
                _isApiLoading = false;
                _collectionFetchRunning = false;
            });
        }
        catch (OperationCanceledException)
        {
            Server.NextFrame(() => { _collectionFetchRunning = false; _isApiLoading = false; });
        }
        catch (ObjectDisposedException)
        {
            // Plugin unloaded while fetching
            Server.NextFrame(() => { _collectionFetchRunning = false; _isApiLoading = false; });
        }
        catch (Exception ex)
        {
            Log("FETCH", $"{(isInitial ? "Initial fetch" : "Refresh")} failed: {ex.Message}");
            Console.WriteLine($"[CS2SimpleVote] Error API: {ex.Message}");
            Server.NextFrame(() => { _collectionFetchRunning = false; _isApiLoading = false; });
        }
    }

    // --- Helpers ---

    // Search-term matching used by nominate/forcemap/setnextmap. Matches the display
    // title always, and the engine map name for stock maps (so "de_dust2" finds the
    // map even though its title is "Dust II").
    private static bool MatchesSearch(MapItem m, string searchTerm)
        => m.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
           || (!IsWorkshopId(m.Id) && m.Id.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));

    // Admin-facing matcher — searches ALL known maps, including disabled ones.
    private MapItem? FindBestMapMatch(string searchTerm)
    {
        if (string.IsNullOrEmpty(searchTerm)) return null;
        var pool = AllKnownMaps();
        if (pool.Count == 0) return null;

        // Exact match (case-insensitive) — title, or engine name for stock maps
        var exact = pool.FirstOrDefault(m => m.Name.Equals(searchTerm, StringComparison.OrdinalIgnoreCase)
            || (!IsWorkshopId(m.Id) && m.Id.Equals(searchTerm, StringComparison.OrdinalIgnoreCase)));
        if (exact != null) return exact;

        // Starts with - pick shortest name (most specific)
        var startsWith = pool
            .Where(m => m.Name.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase)
                || (!IsWorkshopId(m.Id) && m.Id.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.Name.Length)
            .FirstOrDefault();
        if (startsWith != null) return startsWith;

        // Contains - pick shortest name (most specific)
        var contains = pool
            .Where(m => MatchesSearch(m, searchTerm))
            .OrderBy(m => m.Name.Length)
            .FirstOrDefault();
        if (contains != null) return contains;

        return null;
    }

    // --- Admin gating ---
    // Vote admin = may use admin commands (!forcemap, !omitmap, ...).
    // With use_css_admins on, CounterStrikeSharp's admin system is the source of
    // truth (@css/generic or @css/root); the manual "admins" SteamID list keeps
    // working either way so nothing breaks when switching over. Console is always
    // an admin.
    private bool IsVoteAdmin(CCSPlayerController? p)
    {
        if (p == null) return true; // server console
        if (!p.IsValid) return false;
        if (Config.Admins.Contains(p.SteamID)) return true;
        if (Config.UseCssAdmins)
        {
            try
            {
                if (AdminManager.PlayerHasPermissions(p, "@css/root")) return true;
                if (AdminManager.PlayerHasPermissions(p, "@css/generic")) return true;
            }
            catch { /* admin system unavailable — fall through to manual list */ }
        }
        return false;
    }

    private bool IsValidPlayer(CCSPlayerController? player) => player != null && player.IsValid && !player.IsBot && !player.IsHLTV;
    private bool IsWarmup()
    {
        try { return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod ?? false; }
        catch { return false; }
    }
    private bool IsCurrentMap(MapItem map)
    {
        // _currentMapId is resolved on every OnMapStart (workshop ID when known)
        if (!string.IsNullOrEmpty(_currentMapId) && map.Id.Equals(_currentMapId, StringComparison.OrdinalIgnoreCase)) return true;
        // Numeric workshop IDs can safely use Contains (engine paths embed them);
        // stock map names must match the engine map name exactly, otherwise playing
        // e.g. de_dust2 would wrongly flag a hypothetical de_dust as current.
        if (!string.IsNullOrEmpty(map.Id))
        {
            if (IsWorkshopId(map.Id))
            {
                if (Server.MapName.Contains(map.Id, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (Server.MapName.Split('/').Last().Equals(map.Id, StringComparison.OrdinalIgnoreCase)) return true;
        }
        string a = NormalizeMapName(Server.MapName.Split('/').Last());
        string b = NormalizeMapName(map.Name);
        return a.Length >= 3 && a == b;
    }
    private IEnumerable<CCSPlayerController> GetHumanPlayers() => Utilities.GetPlayers().Where(IsValidPlayer);

    // --- Command Handlers (all handled via OnPlayerChat listener) ---

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        if (_unloaded) return HookResult.Continue;
        if (!IsValidPlayer(player)) return HookResult.Continue;
        var p = player!;
        string msg = info.GetArg(1).Trim();
        string cleanMsg = (msg.StartsWith("!") || msg.StartsWith("/")) ? msg[1..] : msg;

        // Parse command and potential arguments
        string[] inputs = cleanMsg.Split(' ', 2);
        string cmd = inputs[0];
        string? args = inputs.Length > 1 ? inputs[1].Trim() : null;

        // Menu input (numbers / "cancel") is consumed by the open menu and hidden;
        // anything else falls through and shows in chat normally.
        if (_helpMenuPlayers.Contains(p.Slot)) return HandleHelpInput(p, cleanMsg);
        if (_nominatingPlayers.ContainsKey(p.Slot)) return HandleNominationInput(p, cleanMsg);
        if (_forcemapPlayers.ContainsKey(p.Slot)) return HandleForcemapInput(p, cleanMsg);
        if (_setnextmapPlayers.ContainsKey(p.Slot)) return HandleSetNextMapInput(p, cleanMsg);

        // All recognized commands return HookResult.Handled so the message is never
        // broadcast to other players (the listener runs in HookMode.Pre).
        if (cmd.Equals("rtv", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptRtv(p)); return HookResult.Handled; }
        if (cmd.Equals("nominatelist", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintNominationList(p)); return HookResult.Handled; }
        if (cmd.Equals("help", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintHelp(p)); return HookResult.Handled; }
        if (cmd.Equals("forcevote", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptForceVote(p)); return HookResult.Handled; }
        if (cmd.Equals("forcertv", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptForceRtv(p)); return HookResult.Handled; }
        if (cmd.Equals("endvote", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptEndVote(p)); return HookResult.Handled; }
        if (cmd.Equals("changenow", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptChangeNow(p)); return HookResult.Handled; }
        if (cmd.Equals("endwarmup", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptEndWarmup(p)); return HookResult.Handled; }
        if (cmd.Equals("votedebug", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptVoteDebug(p)); return HookResult.Handled; }
        if (cmd.Equals("revote", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptRevote(p)); return HookResult.Handled; }
        if (cmd.Equals("nextmap", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintNextMap(p)); return HookResult.Handled; }
        if (cmd.Equals("lastmap", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintLastMap(p)); return HookResult.Handled; }
        if (cmd.Equals("recentmaps", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintRecentMaps(p, args)); return HookResult.Handled; }
        if (cmd.Equals("maplist", StringComparison.OrdinalIgnoreCase) || cmd.Equals("maps", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintMapListToConsole(p)); return HookResult.Handled; }

        if (cmd.Equals("nominate", StringComparison.OrdinalIgnoreCase) || cmd.Equals("nom", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptNominate(p, args));
            return HookResult.Handled;
        }

        if (cmd.Equals("forcemap", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptForcemap(p, args));
            return HookResult.Handled;
        }

        if (cmd.Equals("setnextmap", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptSetNextMap(p, args));
            return HookResult.Handled;
        }

        if (cmd.Equals("addmap", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptAddMapFromChat(p, args));
            return HookResult.Handled;
        }

        if (cmd.Equals("omitmap", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptOmitMapFromChat(p, args));
            return HookResult.Handled;
        }

        if (cmd.Equals("unomitmap", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptUnomitMapFromChat(p, args));
            return HookResult.Handled;
        }

        if (cmd.Equals("omitlist", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => PrintOmitList(p));
            return HookResult.Handled;
        }

        if (cmd.Equals("addlist", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => PrintAddList(p));
            return HookResult.Handled;
        }


        if (_voteInProgress) return HandleVoteInput(p, cleanMsg);

        return HookResult.Continue;
    }

    // --- Logic ---
    private void AttemptRevote(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        if (!_voteInProgress) { player!.PrintToChat($" {ColorDefault}There is no vote currently in progress."); return; }
        if (Config.EnableVoteHud)
        {
            // The panel is already on screen — no need to spam the list into chat.
            player!.PrintToChat($" {ColorDefault}The options are on your screen — type the {ColorGreen}number{ColorDefault} in chat to recast your vote.");
            return;
        }
        player!.PrintToChat($" {ColorDefault}Redisplaying vote options. You may recast your vote.");
        PrintVoteOptionsToPlayer(player);
    }

    private void AttemptVoteDebug(CCSPlayerController? player)
    {
        if (player != null && !IsValidPlayer(player)) return;
        
        bool isConsole = player == null;
        if (!isConsole && !IsVoteAdmin(player))
        {
            player!.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        string loadedStatus = _hasLoadedCollectionMaps ? $"{ColorGreen}Loaded{ColorDefault}" : "Not Loaded";
        string apiStatus = _isApiLoading ? "Loading..." : (_hasLoadedCollectionMaps ? $"{ColorGreen}Finished{ColorDefault}" : "Failed/Not Started");
        string lastMapDisplay = _recentMaps.Count > 1 ? _recentMaps[_recentMaps.Count - 2].Name : "None";
        
        var debugInfo = new List<string>
        {
            $" {ColorDefault}--- {ColorGreen}Vote Debug Info {ColorDefault}---",
            $" {ColorDefault}Plugin Status: {ColorGreen}Active",
            $" {ColorDefault}Maps Loaded: {loadedStatus} ({_availableMaps.Count} maps)",
            $" {ColorDefault}Steam API Status: {apiStatus}",
            $" {ColorDefault}Vote In Progress: {(_voteInProgress ? "Yes" : "No")}",
            $" {ColorDefault}Vote Finished: {(_voteFinished ? "Yes" : "No")}",
            $" {ColorDefault}Match Ended: {(_matchEnded ? "Yes" : "No")}",
            $" {ColorDefault}RTV Voters: {_rtvVoters.Count}",
            $" {ColorDefault}Nominated Maps: {_nominatedMaps.Count}",
            $" {ColorDefault}Last Map: {ColorGreen}{lastMapDisplay}",
            $" {ColorDefault}Collection: {_collectionMaps.Count(e => e.Enabled)}/{_collectionMaps.Count} | Workshop: {_workshopMaps.Count(e => e.Enabled)}/{_workshopMaps.Count} | Stock: {_stockMaps.Count(e => e.Enabled)}/{_stockMaps.Count} (enabled/total)",
            $" {ColorDefault}Target Collection ID: {Config.CollectionId}"
        };

        if (_activeVoteOptions.Count > 0)
        {
            debugInfo.Add($" {ColorDefault}--- {ColorGreen}Active Vote Data {ColorDefault}---");
            foreach (var kvp in OrderedVoteOptions())
            {
                int votes = _playerVotes.Values.Count(v => v == kvp.Key);
                debugInfo.Add($" {ColorDefault}Option [{kvp.Key}] {ColorGreen}{OptionName(kvp.Value)}{ColorDefault}: {votes} votes");
            }
        }

        if (isConsole)
        {
            foreach (var line in debugInfo)
            {
                Console.WriteLine(line.Replace(ColorDefault, "").Replace(ColorGreen, ""));
            }
        }
        else
        {
            foreach (var line in debugInfo)
            {
                player!.PrintToChat(line);
            }
        }

        // Snapshot state for thread-safe dumping to avoid lagging the game server
        var dumpState = new
        {
            State = new {
                VoteInProgress = _voteInProgress,
                VoteFinished = _voteFinished,
                IsScheduledVote = _isScheduledVote,
                CurrentVoteRoundDuration = _currentVoteRoundDuration,
                IsForceVote = _isForceVote,
                PreviousWinningMapId = _previousWinningMapId,
                PreviousWinningMapName = _previousWinningMapName,
                MatchEnded = _matchEnded,
                VoteIsTimed = _voteIsTimed,
                VoteSecondsRemaining = VoteSecondsRemaining(),
                NextMapName = _nextMapName,
                PendingMapId = _pendingMapId
            },
            Collections = new {
                RtvVoters = _rtvVoters.ToList(),
                ActiveVoteOptions = _activeVoteOptions.ToDictionary(k => k.Key.ToString(), v => v.Value),
                PlayerVotes = _playerVotes.ToDictionary(k => k.Key.ToString(), v => v.Value),
                NominatedMaps = _nominatedMaps.Select(m => new { m.Id, m.Name }).ToList(),
                RecentMaps = _recentMaps.Select(m => new { m.Id, m.Name }).ToList()
            }
        };

        // Offload large JSON serialization and console I/O to a background thread
        Task.Run(() => 
        {
            try 
            {
                string json = System.Text.Json.JsonSerializer.Serialize(dumpState, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                Console.WriteLine("\n[CS2SimpleVote] --- FULL MEMORY DUMP ---");
                Console.WriteLine(json);
                Console.WriteLine("[CS2SimpleVote] --- END DUMP ---\n");
            } 
            catch (Exception ex) 
            {
                Console.WriteLine($"\n[CS2SimpleVote] Error creating memory dump: {ex.Message}\n");
            }
        });
    }

    // !help: regular players get the user commands directly; admins get a picker
    // (same chat-menu format as a multi-match nomination) choosing between the
    // user and admin lists. Admin commands render in red, user commands in green.
    private void PrintHelp(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!IsVoteAdmin(p))
        {
            PrintUserHelp(p);
            return;
        }

        _helpMenuPlayers.Add(p.Slot);
        p.PrintToChat($" {ColorDefault}Page 1/1. Type number to select (or 'cancel'):");
        p.PrintToChat($" {ColorGreen}[1] {ColorDefault}User Commands");
        p.PrintToChat($" {ColorRed}[2] {ColorDefault}Admin Commands");
    }

    private HookResult HandleHelpInput(CCSPlayerController player, string input)
    {
        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _helpMenuPlayers.Remove(player.Slot);
            player.PrintToChat($" {ColorDefault}Help cancelled.");
            return HookResult.Handled;
        }
        if (input == "1" || input == "2")
        {
            _helpMenuPlayers.Remove(player.Slot);
            if (input == "1") PrintUserHelp(player);
            else PrintAdminHelp(player);
            return HookResult.Handled;
        }
        return HookResult.Continue;
    }

    private void PrintUserHelp(CCSPlayerController p)
    {
        p.PrintToChat($" {ColorDefault}---{ColorGreen} CS2SimpleVote Commands {ColorDefault}---");
        p.PrintToChat($" {ColorGreen}!help {ColorDefault}- List available commands");
        p.PrintToChat($" {ColorGreen}!lastmap {ColorDefault}- Show last played map");
        p.PrintToChat($" {ColorGreen}!maplist {ColorDefault}/ {ColorGreen}!maps {ColorDefault}- Print the full map list to your console");
        p.PrintToChat($" {ColorGreen}!nextmap {ColorDefault}- Show next map");
        p.PrintToChat($" {ColorGreen}!nominate [name] {ColorDefault}- Nominate a map");
        p.PrintToChat($" {ColorGreen}!nominatelist {ColorDefault}- List nominated maps");
        p.PrintToChat($" {ColorGreen}!recentmaps {ColorDefault}- Show recently played maps");
        p.PrintToChat($" {ColorGreen}!revote {ColorDefault}- Recast vote");
        p.PrintToChat($" {ColorGreen}!rtv {ColorDefault}- Rock the Vote");
    }

    private void PrintAdminHelp(CCSPlayerController p)
    {
        p.PrintToChat($" {ColorDefault}---{ColorRed} CS2SimpleVote Admin Commands {ColorDefault}---");
        p.PrintToChat($" {ColorRed}!addmap [workshop ID] {ColorDefault}- Add/re-enable a workshop map (workshop_maps.json)");
        p.PrintToChat($" {ColorRed}!addlist {ColorDefault}- List manually added workshop maps");
        p.PrintToChat($" {ColorRed}!changenow {ColorDefault}- Change to the queued next map immediately");
        p.PrintToChat($" {ColorRed}!endvote {ColorDefault}- End an active vote early");
        p.PrintToChat($" {ColorRed}!endwarmup {ColorDefault}- End the current warmup");
        p.PrintToChat($" {ColorRed}!forcemap [name] {ColorDefault}- Force change map");
        p.PrintToChat($" {ColorRed}!forcertv {ColorDefault}- Start an RTV vote, map changes at vote end");
        p.PrintToChat($" {ColorRed}!forcevote {ColorDefault}- Force start map vote");
        p.PrintToChat($" {ColorRed}!omitmap [words] {ColorDefault}- Disable matching maps (removed from votes)");
        p.PrintToChat($" {ColorRed}!omitlist {ColorDefault}- List omitted (disabled) maps");
        p.PrintToChat($" {ColorRed}!setnextmap [name] {ColorDefault}- Set the next map directly");
        p.PrintToChat($" {ColorRed}!unomitmap [words] {ColorDefault}- Re-enable matching maps");
        p.PrintToChat($" {ColorRed}!votedebug {ColorDefault}- Show debug info");
    }

    private void PrintNominationList(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        if (_nominatedMaps.Count == 0) { player!.PrintToChat($" {ColorDefault}No maps currently nominated."); return; }

        player!.PrintToChat($" {ColorDefault}--- {ColorGreen}Nominated Maps ({_nominatedMaps.Count}/{Config.VoteOptionsCount}) {ColorDefault}---");
        foreach (var map in _nominatedMaps)
        {
            var owner = _nominationOwner.FirstOrDefault(x => x.Value.Id == map.Id);
            string nominator = (owner.Value != null && _nominationNames.TryGetValue(owner.Key, out var name)) ? name : "Unknown";
            player.PrintToChat($" {ColorGreen} - {nominator} {ColorDefault}- {ColorGreen}{map.Name}");
        }
    }

    private void PrintNextMap(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        // Reply to the asker only — the command itself is hidden from other players,
        // so a broadcast answer would just be unexplained chat noise for everyone else.
        if (string.IsNullOrEmpty(_nextMapName)) { player!.PrintToChat($" {ColorDefault}The next map has not been decided yet."); return; }
        player!.PrintToChat($" {ColorDefault}The next map will be: {ColorGreen}{_nextMapName}");
    }

    private void PrintLastMap(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        if (_recentMaps.Count > 1) 
        {
            // The current map is usually pushed to the end of _recentMaps upon OnMapStart.
            // Meaning, the "last" map before the current one is at count - 2.
            var lastMap = _recentMaps[_recentMaps.Count - 2];
            player!.PrintToChat($" {ColorDefault}The last played map was: {ColorGreen}{lastMap.Name}");
        }
        else 
        {
            player!.PrintToChat($" {ColorDefault}No previous map data found.");
        }
    }

    private void PrintRecentMaps(CCSPlayerController? player, string? arg = null)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (_recentMaps.Count == 0 || (_recentMaps.Count == 1 && IsCurrentMap(_recentMaps[0])))
        {
            p.PrintToChat($" {ColorDefault}No recent maps data available yet.");
            return;
        }

        int maxDisplayCount = Config.RecentMapsCount;
        if (!string.IsNullOrEmpty(arg) && int.TryParse(arg, out int parsedLimit))
        {
            if (parsedLimit > 0 && parsedLimit <= Config.RecentMapsCount)
            {
                maxDisplayCount = parsedLimit;
            }
            else
            {
                p.PrintToChat($" {ColorDefault}Please enter a number between 1 and {Config.RecentMapsCount}.");
                return;
            }
        }
        
        string titleText = $"Last {maxDisplayCount} Recent Maps";
        string dashes = new string('-', titleText.Length);
        
        p.PrintToChat($" {ColorDefault}{dashes}");
        p.PrintToChat($" {ColorGreen}{titleText}");
        p.PrintToChat($" {ColorDefault}{dashes}");
        
        var reversed = _recentMaps.AsEnumerable().Reverse().ToList();

        // Print up to recent configurations limit. Skipping index 0 if it's the current map.
        int displayCount = 1;
        for(int i = 0; i < reversed.Count; i++)
        {
            if (displayCount > maxDisplayCount) break;

            // Usually index 0 in the reversed list is the current active map because it gets appended to the end of the history.
            // Let's filter out current map to show only purely *past* maps
            if (IsCurrentMap(reversed[i])) continue;

            p.PrintToChat($" {ColorGreen}{displayCount}. {ColorDefault}{reversed[i].Name}");
            displayCount++;
        }
    }

    private void PrintMapListToConsole(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        RefreshMapPools();

        if (_availableMaps.Count == 0)
        {
            p.PrintToChat($" {ColorDefault}No maps loaded yet. Try again once the Workshop collection has finished fetching.");
            return;
        }

        var sorted = _availableMaps.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
        p.PrintToConsole($"--- CS2SimpleVote: {sorted.Count} Available Maps (Collection: {Config.CollectionId}) ---");
        foreach (var map in sorted)
        {
            p.PrintToConsole($"  {map.Name}  (ID: {map.Id})");
        }
        p.PrintToConsole($"--- End ({sorted.Count} maps) ---");

        p.PrintToChat($" {ColorDefault}All {ColorGreen}{sorted.Count}{ColorDefault} maps were sent to your console. Press {ColorGreen}~{ColorDefault} to view.");
    }

    private void AttemptRtv(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (IsWarmup()) { p.PrintToChat($" {ColorDefault}RTV is disabled during warmup."); return; }
        if (!Config.EnableRtv) { p.PrintToChat($" {ColorDefault}RTV is currently disabled."); return; }
        if (_voteInProgress) { p.PrintToChat($" {ColorDefault}A map vote is already in progress."); return; }
        if (_voteFinished) { p.PrintToChat($" {ColorDefault}The next map has already been decided."); return; }
        if (!_rtvVoters.Add(p.Slot)) { p.PrintToChat($" {ColorDefault}You have already rocked the vote."); return; }

        int currentPlayers = GetHumanPlayers().Count();
        int votesNeeded = Math.Max(1, (int)Math.Ceiling(currentPlayers * Config.RtvRatio));
        Log("RTV", $"{PlayerTag(p)} rocked the vote ({_rtvVoters.Count}/{votesNeeded})");
        Server.PrintToChatAll($" {ColorDefault}{ColorGreen}{p.PlayerName}{ColorDefault} wants to change the map! ({_rtvVoters.Count}/{votesNeeded})");

        if (_rtvVoters.Count >= votesNeeded) { Log("RTV", $"Threshold reached ({_rtvVoters.Count}/{votesNeeded}) — starting vote"); Server.PrintToChatAll($" {ColorDefault}RTV Threshold reached! Starting vote..."); StartMapVote(isRtv: true); }
    }

    private void AttemptNominate(CCSPlayerController? player, string? searchTerm = null)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!Config.EnableNominate) { p.PrintToChat($" {ColorDefault}Nominations are currently disabled."); return; }
        if (_voteInProgress) { p.PrintToChat($" {ColorDefault}A map vote is in progress — type the {ColorGreen}number{ColorDefault} in chat to vote!"); return; }
        if (_voteFinished) { p.PrintToChat($" {ColorDefault}Voting has already finished — the next map is decided."); return; }
        
        bool isRenomination = _hasNominatedSteamIds.Contains(p.SteamID);
        if (!isRenomination && _nominatedMaps.Count >= Config.VoteOptionsCount) { p.PrintToChat($" {ColorDefault}The nomination list is full!"); return; }

        // Pick up realtime file edits (throttled — this path is player-spammable).
        RefreshMapPools();

        // Recently played maps are excluded here too — this was the main way recent
        // maps kept leaking back into votes: nominations skipped the recent filter
        // and nominated maps go straight into every vote's option list. Disabled
        // (omitted) maps are excluded implicitly: _availableMaps only holds enabled.
        var validMaps = _availableMaps
            .Where(m => !_nominatedMaps.Any(n => n.Id == m.Id))
            .Where(m => !IsCurrentMap(m))
            .Where(m => !Config.OmitRecentMaps || !IsRecentMap(m))
            .ToList();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            validMaps = validMaps.Where(m => MatchesSearch(m, searchTerm)).ToList();
        }

        if (validMaps.Count == 0)
        {
            p.PrintToChat(string.IsNullOrEmpty(searchTerm) ? $" {ColorDefault}No maps available to nominate." : $" {ColorDefault}No maps found matching: {ColorGreen}{searchTerm}");
            return;
        }

        // If there is only one match and a search term was used, nominate it immediately
        if (validMaps.Count == 1 && !string.IsNullOrEmpty(searchTerm))
        {
            var selectedMap = validMaps[0];
            if (_nominatedMaps.Any(m => m.Id == selectedMap.Id))
            {
                p.PrintToChat($" {ColorDefault}That map is already nominated.");
            }
            else
            {
                ProcessNomination(p, selectedMap);
            }
            return;
        }

        _nominatingPlayers[p.Slot] = validMaps;
        _playerNominationPage[p.Slot] = 0;
        DisplayNominationMenu(p);
    }

    private void DisplayNominationMenu(CCSPlayerController player)
    {
        if (!_nominatingPlayers.TryGetValue(player.Slot, out var maps)) return;
        int page = _playerNominationPage.GetValueOrDefault(player.Slot, 0);
        int totalPages = (int)Math.Ceiling((double)maps.Count / Config.NominatePerPage);
        if (page >= totalPages) page = 0;
        _playerNominationPage[player.Slot] = page;

        int startIndex = page * Config.NominatePerPage;
        int endIndex = Math.Min(startIndex + Config.NominatePerPage, maps.Count);
        player.PrintToChat($" {ColorDefault}Page {page + 1}/{totalPages}. Type number to select (or 'cancel'):");
        for (int i = startIndex; i < endIndex; i++) { int displayNum = (i - startIndex) + 1; player.PrintToChat($" {ColorGreen}[{displayNum}] {ColorDefault}{maps[i].Name}"); }
        if (totalPages > 1) player.PrintToChat($" {ColorGreen}[0] {ColorDefault}Next Page");
    }

    private HookResult HandleNominationInput(CCSPlayerController player, string input)
    {
        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase)) { CloseNominationMenu(player); player.PrintToChat($" {ColorDefault}Nomination cancelled."); return HookResult.Handled; }
        if (input == "0") { _playerNominationPage[player.Slot]++; DisplayNominationMenu(player); return HookResult.Handled; }
        if (int.TryParse(input, out int selection))
        {
            var maps = _nominatingPlayers[player.Slot];
            int page = _playerNominationPage[player.Slot];
            int realIndex = (page * Config.NominatePerPage) + (selection - 1);
            if (realIndex >= 0 && realIndex < maps.Count && realIndex >= (page * Config.NominatePerPage) && realIndex < ((page + 1) * Config.NominatePerPage))
            {
                var selectedMap = maps[realIndex];
                bool isRenomination = _hasNominatedSteamIds.Contains(player.SteamID);

                if (!isRenomination && _nominatedMaps.Count >= Config.VoteOptionsCount) player.PrintToChat($" {ColorDefault}Nomination list is full.");
                else if (_nominatedMaps.Any(m => m.Id == selectedMap.Id)) player.PrintToChat($" {ColorDefault}That map was just nominated by someone else.");
                else { ProcessNomination(player, selectedMap); }
                CloseNominationMenu(player);
                return HookResult.Handled;
            }
        }
        return HookResult.Continue;
    }

    private void ProcessNomination(CCSPlayerController player, MapItem map)
    {
        _nominationNames[player.SteamID] = player.PlayerName;
        if (_hasNominatedSteamIds.Contains(player.SteamID))
        {
            if (_nominationOwner.TryGetValue(player.SteamID, out var oldMap))
            {
                _nominatedMaps.RemoveAll(m => m.Id == oldMap.Id);
            }
            _nominatedMaps.Add(map);
            _nominationOwner[player.SteamID] = map;
            Log("NOMINATE", $"{PlayerTag(player)} changed nomination to {map.Name} ({map.Id})");
            Server.PrintToChatAll($" {ColorDefault}Player {ColorGreen}{player.PlayerName}{ColorDefault} changed their nomination to {ColorGreen}{map.Name}{ColorDefault}.");
        }
        else
        {
            _nominatedMaps.Add(map);
            _hasNominatedSteamIds.Add(player.SteamID);
            _nominationOwner[player.SteamID] = map;
            Log("NOMINATE", $"{PlayerTag(player)} nominated {map.Name} ({map.Id})");
            Server.PrintToChatAll($" {ColorDefault}Player {ColorGreen}{player.PlayerName}{ColorDefault} nominated {ColorGreen}{map.Name}{ColorDefault}.");
        }
    }

    private void CloseNominationMenu(CCSPlayerController player) { _nominatingPlayers.Remove(player.Slot); _playerNominationPage.Remove(player.Slot); }

    // --- Forcemap Logic ---
    private void AttemptForcemap(CCSPlayerController? player, string? searchTerm = null)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        // Admin commands deliberately see everything, including disabled maps.
        RefreshMapPools();
        var validMaps = AllKnownMaps();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            validMaps = validMaps.Where(m => MatchesSearch(m, searchTerm)).ToList();
        }

        if (validMaps.Count == 0)
        {
            p.PrintToChat(string.IsNullOrEmpty(searchTerm) ? $" {ColorDefault}No maps available." : $" {ColorDefault}No maps found matching: {ColorGreen}{searchTerm}");
            return;
        }

        // Immediate switch if only 1 match with filter
        if (validMaps.Count == 1 && !string.IsNullOrEmpty(searchTerm))
        {
            var map = validMaps[0];
            Log("ADMIN", $"{PlayerTag(p)} ran !forcemap '{searchTerm}' -> {map.Name} ({map.Id})");
            Log("MAPCHANGE", $"Forcing map change to {map.Name} ({map.Id}) by {PlayerTag(p)}");
            Server.PrintToChatAll($" {ColorDefault}Admin {ColorGreen}{p.PlayerName}{ColorDefault} forced map change to {ColorGreen}{map.Name}{ColorDefault}.");
            _expectedMapId = map.Id;
            _expectedMapName = map.Name;
            ChangeMap(map.Id);
            return;
        }

        _forcemapPlayers[p.Slot] = validMaps;
        _playerForcemapPage[p.Slot] = 0;
        DisplayForcemapMenu(p);
    }

    private void DisplayForcemapMenu(CCSPlayerController player)
    {
        if (!_forcemapPlayers.TryGetValue(player.Slot, out var maps)) return;
        int page = _playerForcemapPage.GetValueOrDefault(player.Slot, 0);
        int totalPages = (int)Math.Ceiling((double)maps.Count / Config.NominatePerPage);
        if (page >= totalPages) page = 0;
        _playerForcemapPage[player.Slot] = page;

        int startIndex = page * Config.NominatePerPage;
        int endIndex = Math.Min(startIndex + Config.NominatePerPage, maps.Count);
        player.PrintToChat($" {ColorDefault}[Forcemap] Page {page + 1}/{totalPages}. Type number to select (or 'cancel'):");
        for (int i = startIndex; i < endIndex; i++) { int displayNum = (i - startIndex) + 1; player.PrintToChat($" {ColorGreen}[{displayNum}] {ColorDefault}{maps[i].Name}"); }
        if (totalPages > 1) player.PrintToChat($" {ColorGreen}[0] {ColorDefault}Next Page");
    }

    private HookResult HandleForcemapInput(CCSPlayerController player, string input)
    {
        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase)) { CloseForcemapMenu(player); player.PrintToChat($" {ColorDefault}Forcemap cancelled."); return HookResult.Handled; }
        if (input == "0") { _playerForcemapPage[player.Slot]++; DisplayForcemapMenu(player); return HookResult.Handled; }
        if (int.TryParse(input, out int selection))
        {
            var maps = _forcemapPlayers[player.Slot];
            int page = _playerForcemapPage[player.Slot];
            int realIndex = (page * Config.NominatePerPage) + (selection - 1);
            if (realIndex >= 0 && realIndex < maps.Count && realIndex >= (page * Config.NominatePerPage) && realIndex < ((page + 1) * Config.NominatePerPage))
            {
                var selectedMap = maps[realIndex];
                Log("ADMIN", $"{PlayerTag(player)} selected forcemap -> {selectedMap.Name} ({selectedMap.Id})");
                Log("MAPCHANGE", $"Forcing map change to {selectedMap.Name} ({selectedMap.Id}) by {PlayerTag(player)}");
                Server.PrintToChatAll($" {ColorDefault} Admin {ColorGreen}{player.PlayerName}{ColorDefault} forced map change to {ColorGreen}{selectedMap.Name}{ColorDefault}.");
                _expectedMapId = selectedMap.Id;
                _expectedMapName = selectedMap.Name;
                ChangeMap(selectedMap.Id);
                CloseForcemapMenu(player);
                return HookResult.Handled;
            }
        }
        return HookResult.Continue;
    }
    private void CloseForcemapMenu(CCSPlayerController player) { _forcemapPlayers.Remove(player.Slot); _playerForcemapPage.Remove(player.Slot); }

    // --- SetNextMap Logic ---
    private void AttemptSetNextMap(CCSPlayerController? player, string? searchTerm = null)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        // Admin commands deliberately see everything, including disabled maps.
        RefreshMapPools();
        var validMaps = AllKnownMaps();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            validMaps = validMaps.Where(m => MatchesSearch(m, searchTerm)).ToList();
        }

        if (validMaps.Count == 0)
        {
            p.PrintToChat(string.IsNullOrEmpty(searchTerm) ? $" {ColorDefault}No maps available." : $" {ColorDefault}No maps found matching: {ColorGreen}{searchTerm}");
            return;
        }

        if (validMaps.Count == 1 && !string.IsNullOrEmpty(searchTerm))
        {
            ProcessSetNextMap(p, validMaps[0]);
            return;
        }

        _setnextmapPlayers[p.Slot] = validMaps;
        _playerSetNextMapPage[p.Slot] = 0;
        DisplaySetNextMapMenu(p);
    }

    private void DisplaySetNextMapMenu(CCSPlayerController player)
    {
        if (!_setnextmapPlayers.TryGetValue(player.Slot, out var maps)) return;
        int page = _playerSetNextMapPage.GetValueOrDefault(player.Slot, 0);
        int totalPages = (int)Math.Ceiling((double)maps.Count / Config.NominatePerPage);
        if (page >= totalPages) page = 0;
        _playerSetNextMapPage[player.Slot] = page;

        int startIndex = page * Config.NominatePerPage;
        int endIndex = Math.Min(startIndex + Config.NominatePerPage, maps.Count);
        player.PrintToChat($" {ColorDefault}[SetNextMap] Page {page + 1}/{totalPages}. Type number to select (or 'cancel'):");
        for (int i = startIndex; i < endIndex; i++) { int displayNum = (i - startIndex) + 1; player.PrintToChat($" {ColorGreen}[{displayNum}] {ColorDefault}{maps[i].Name}"); }
        if (totalPages > 1) player.PrintToChat($" {ColorGreen}[0] {ColorDefault}Next Page");
    }

    private HookResult HandleSetNextMapInput(CCSPlayerController player, string input)
    {
        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase)) { CloseSetNextMapMenu(player); player.PrintToChat($" {ColorDefault}SetNextMap cancelled."); return HookResult.Handled; }
        if (input == "0") { _playerSetNextMapPage[player.Slot]++; DisplaySetNextMapMenu(player); return HookResult.Handled; }
        if (int.TryParse(input, out int selection))
        {
            var maps = _setnextmapPlayers[player.Slot];
            int page = _playerSetNextMapPage[player.Slot];
            int realIndex = (page * Config.NominatePerPage) + (selection - 1);
            if (realIndex >= 0 && realIndex < maps.Count && realIndex >= (page * Config.NominatePerPage) && realIndex < ((page + 1) * Config.NominatePerPage))
            {
                ProcessSetNextMap(player, maps[realIndex]);
                CloseSetNextMapMenu(player);
                return HookResult.Handled;
            }
        }
        return HookResult.Continue;
    }

    private void ProcessSetNextMap(CCSPlayerController player, MapItem selectedMap)
    {
        _pendingMapId = selectedMap.Id;
        _nextMapName = selectedMap.Name;
        _nextMapSetByAdmin = true;
        _voteFinished = true;
        Log("ADMIN", $"{PlayerTag(player)} ran !setnextmap -> {selectedMap.Name} ({selectedMap.Id})");

        string rawMsg = $"{player.PlayerName} has set the next map to {selectedMap.Name}.";
        string dashes = new string('-', rawMsg.Length);

        Server.PrintToChatAll($" {ColorDefault}{dashes}");
        Server.PrintToChatAll($" {ColorGreen}{player.PlayerName} {ColorDefault}has set the next map to {ColorGreen}{selectedMap.Name}{ColorDefault}.");
        Server.PrintToChatAll($" {ColorDefault}{dashes}");
    }

    private void CloseSetNextMapMenu(CCSPlayerController player) { _setnextmapPlayers.Remove(player.Slot); _playerSetNextMapPage.Remove(player.Slot); }

    // --- AddMap Logic (custom_maps.json) ---

    private void AttemptAddMapFromChat(CCSPlayerController? player, string? arg)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }
        AttemptAddMap(p, arg);
    }

    // Shared by chat (!addmap) and console (css_addmap). Caller may be null (console).
    // Adds a workshop map to workshop_maps.json (enabled), or re-enables it if it is
    // already tracked anywhere but disabled.
    private void AttemptAddMap(CCSPlayerController? caller, string? arg)
    {
        void Reply(string text)
        {
            if (caller != null && caller.IsValid) caller.PrintToChat($" {ColorDefault}{text}");
            else Console.WriteLine($"[CS2SimpleVote] {text.Replace(ColorGreen, "").Replace(ColorDefault, "")}");
        }

        string id = ExtractWorkshopId(arg);
        if (string.IsNullOrEmpty(id))
        {
            Reply($"Usage: {ColorGreen}!addmap <workshop ID or workshop URL>{ColorDefault}");
            return;
        }

        RefreshMapPools(force: true);

        // Already tracked? Re-enable if needed instead of duplicating.
        var ws = _workshopMaps.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (ws != null)
        {
            if (ws.Enabled) { Reply($"{ColorGreen}{TitleOf(ws)}{ColorDefault} ({id}) is already in workshop_maps.json and enabled."); return; }
            ws.Enabled = true;
            WriteTrackedFile(_workshopMapsFilePath, _workshopMaps, "workshop_maps.json");
            RebuildAvailableMaps();
            Log("ADMIN", $"addmap: re-enabled {TitleOf(ws)} ({id}) in workshop_maps.json");
            Reply($"Re-enabled {ColorGreen}{TitleOf(ws)}{ColorDefault} ({id}).");
            return;
        }
        var col = _collectionMaps.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (col != null)
        {
            if (col.Enabled) { Reply($"{ColorGreen}{TitleOf(col)}{ColorDefault} ({id}) is already available via the collection."); return; }
            col.Enabled = true;
            WriteTrackedFile(_collectionMapsFilePath, _collectionMaps, "collection_maps.json");
            RebuildAvailableMaps();
            Log("ADMIN", $"addmap: re-enabled {TitleOf(col)} ({id}) in collection_maps.json");
            Reply($"Re-enabled {ColorGreen}{TitleOf(col)}{ColorDefault} ({id}).");
            return;
        }

        if (string.IsNullOrEmpty(Config.SteamApiKey) || Config.SteamApiKey == "YOUR_STEAM_API_KEY_HERE")
        {
            Reply("Cannot add map: no Steam API key configured (needed to look up the map name).");
            return;
        }

        Reply($"Looking up workshop item {ColorGreen}{id}{ColorDefault}...");

        int? callerSlot = caller?.Slot;
        var token = _cts.Token;
        Task.Run(async () =>
        {
            string? title = null;
            string? error = null;
            try { title = await FetchWorkshopTitle(id, token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { error = ex.Message; }

            Server.NextFrame(() =>
            {
                if (_unloaded) return;

                void LateReply(string text)
                {
                    var p = callerSlot.HasValue ? Utilities.GetPlayerFromSlot(callerSlot.Value) : null;
                    if (p != null && p.IsValid) p.PrintToChat($" {ColorDefault}{text}");
                    else Console.WriteLine($"[CS2SimpleVote] {text.Replace(ColorGreen, "").Replace(ColorDefault, "")}");
                }

                if (error != null || string.IsNullOrEmpty(title))
                {
                    LateReply($"Failed to add map {id}: {error ?? "no title returned (deleted/private item?)"}");
                    return;
                }

                // Re-check duplicates against the latest files: a collection refresh
                // or a hand edit may have landed while we were fetching.
                RefreshMapPools(force: true);
                if (_workshopMaps.Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ||
                    _collectionMaps.Any(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    LateReply($"{title} ({id}) is already tracked.");
                    return;
                }

                _workshopMaps.Add(new TrackedMapEntry { Id = id, Title = title, Enabled = true });
                WriteTrackedFile(_workshopMapsFilePath, _workshopMaps, "workshop_maps.json");
                RebuildAvailableMaps();

                Log("ADMIN", $"addmap: {title} ({id}) added to workshop_maps.json");
                LateReply($"Added {ColorGreen}{title}{ColorDefault} ({id}) to workshop_maps.json. It is now available for votes and nominations.");
            });
        });
    }

    // Accepts a bare numeric workshop ID or a full workshop URL
    // (e.g. https://steamcommunity.com/sharedfiles/filedetails/?id=123456789).
    private static string ExtractWorkshopId(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return "";
        string s = arg.Trim().Trim('"');

        int idx = s.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            s = s[(idx + 3)..];
            int amp = s.IndexOf('&');
            if (amp >= 0) s = s[..amp];
        }

        return (s.Length > 0 && s.Length <= 20 && s.All(char.IsDigit)) ? s : "";
    }

    // Looks up a single workshop item's title via IPublishedFileService/GetDetails.
    private async Task<string?> FetchWorkshopTitle(string id, CancellationToken token)
    {
        var url = $"https://api.steampowered.com/IPublishedFileService/GetDetails/v1/?key={Uri.EscapeDataString(Config.SteamApiKey)}&publishedfileids%5B0%5D={id}";
        var httpRes = await _httpClient.GetAsync(url, token);
        if (!httpRes.IsSuccessStatusCode)
            throw new Exception($"Steam API HTTP {(int)httpRes.StatusCode}");

        string json = await httpRes.Content.ReadAsStringAsync(token);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("response", out var respEl) ||
            !respEl.TryGetProperty("publishedfiledetails", out var detailsArr) ||
            detailsArr.GetArrayLength() == 0)
            throw new Exception("Steam API returned no details for this ID.");

        var item = detailsArr[0];
        int result = item.TryGetProperty("result", out var resp) ? resp.GetInt32() : 0;
        if (result != 1)
            throw new Exception($"Steam result code {result} (item may be deleted, private, or banned).");

        int fileType = item.TryGetProperty("file_type", out var ftp) ? ftp.GetInt32() : 0;
        if (fileType == 2)
            throw new Exception("That ID is a collection, not a map. Use the collection_id config for collections.");

        return item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
    }

    // Resolves titles for a batch of workshop IDs (worker thread; touches no plugin
    // state). Items that come back deleted/private/banned are simply skipped.
    private async Task<Dictionary<string, string>> FetchTitlesBatch(List<string> ids, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int offset = 0; offset < ids.Count; offset += 100)
        {
            var batch = ids.Skip(offset).Take(100).ToList();
            var queryParts = new List<string> { $"key={Uri.EscapeDataString(Config.SteamApiKey)}" };
            for (int i = 0; i < batch.Count; i++)
                queryParts.Add($"publishedfileids%5B{i}%5D={batch[i]}");
            var url = $"https://api.steampowered.com/IPublishedFileService/GetDetails/v1/?{string.Join("&", queryParts)}";

            var httpRes = await _httpClient.GetAsync(url, token);
            if (!httpRes.IsSuccessStatusCode)
                throw new Exception($"Steam API HTTP {(int)httpRes.StatusCode}");

            string json = await httpRes.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var respEl) ||
                !respEl.TryGetProperty("publishedfiledetails", out var detailsArr))
                continue;

            foreach (var item in detailsArr.EnumerateArray())
            {
                string? id = item.TryGetProperty("publishedfileid", out var idp) ? idp.GetString() : null;
                if (string.IsNullOrEmpty(id)) continue;
                int res = item.TryGetProperty("result", out var resp) ? resp.GetInt32() : 0;
                if (res != 1) continue;
                string? title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : null;
                if (!string.IsNullOrWhiteSpace(title)) result[id] = title;
            }
        }
        return result;
    }

    // --- OmitMap Logic (omitted_maps.json) ---

    private void AttemptOmitMapFromChat(CCSPlayerController? player, string? words)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }
        AttemptOmitMap(p, words);
    }

    // Shared by chat (!omitmap) and console (css_omitmap). Caller may be null (console).
    // Omitting means disabling: matching entries in collection_maps.json /
    // workshop_maps.json / stock_maps.json get "enabled": false, which removes them
    // from votes and nominations until re-enabled (!unomitmap, !addmap, or a hand edit).
    private void AttemptOmitMap(CCSPlayerController? caller, string? words)
        => ToggleMapsEnabled(caller, words, enable: false);

    // Shared toggle used by !omitmap (enable: false) and !unomitmap (enable: true).
    private void ToggleMapsEnabled(CCSPlayerController? caller, string? words, bool enable)
    {
        void Reply(string text)
        {
            if (caller != null && caller.IsValid) caller.PrintToChat($" {ColorDefault}{text}");
            else Console.WriteLine($"[CS2SimpleVote] {text.Replace(ColorGreen, "").Replace(ColorDefault, "")}");
        }

        string verb = enable ? "unomitmap" : "omitmap";
        string pattern = NormalizePattern(words ?? "");
        if (pattern.Length == 0)
        {
            Reply($"Usage: {ColorGreen}!{verb} <word(s)>{ColorDefault} — matches map titles, stock map names, or an exact workshop ID.");
            return;
        }

        // Latest files first, so the toggle applies on top of any realtime hand edits.
        RefreshMapPools(force: true);

        var changed = new List<string>();
        var alreadySet = new List<string>();

        bool collectionChanged = false, workshopChanged = false, stockChanged = false;
        foreach (var e in _collectionMaps.Where(e => TrackedMatchesPattern(e, pattern)))
        {
            if (e.Enabled == enable) { alreadySet.Add(TitleOf(e)); continue; }
            e.Enabled = enable; changed.Add(TitleOf(e)); collectionChanged = true;
        }
        foreach (var e in _workshopMaps.Where(e => TrackedMatchesPattern(e, pattern)))
        {
            if (e.Enabled == enable) { alreadySet.Add(TitleOf(e)); continue; }
            e.Enabled = enable; changed.Add(TitleOf(e)); workshopChanged = true;
        }
        foreach (var e in _stockMaps.Where(e => StockMatchesPattern(e, pattern)))
        {
            if (e.Enabled == enable) { alreadySet.Add(e.Title); continue; }
            e.Enabled = enable; changed.Add(e.Title); stockChanged = true;
        }

        if (collectionChanged) WriteTrackedFile(_collectionMapsFilePath, _collectionMaps, "collection_maps.json");
        if (workshopChanged) WriteTrackedFile(_workshopMapsFilePath, _workshopMaps, "workshop_maps.json");
        if (stockChanged) WriteStockMapsFile(_stockMaps);
        RebuildAvailableMaps();

        if (changed.Count == 0)
        {
            Reply(alreadySet.Count > 0
                ? $"All {ColorGreen}{alreadySet.Count}{ColorDefault} matching map(s) are already {(enable ? "enabled" : "omitted")}."
                : $"No maps match '{ColorGreen}{pattern}{ColorDefault}'.");
            return;
        }

        Log("ADMIN", $"{PlayerTag(caller)} {verb} '{pattern}': {(enable ? "enabled" : "disabled")} {changed.Count} map(s): {string.Join(", ", changed)}");
        const int maxNames = 8;
        string names = string.Join(", ", changed.Take(maxNames));
        if (changed.Count > maxNames) names += $", +{changed.Count - maxNames} more";
        Reply(enable
            ? $"Re-enabled {ColorGreen}{changed.Count}{ColorDefault} map(s): {ColorGreen}{names}"
            : $"Omitted (disabled) {ColorGreen}{changed.Count}{ColorDefault} map(s): {ColorGreen}{names}");

        if (!enable)
        {
            // Pull any now-disabled maps out of the pending nomination list, and free
            // their nominators to nominate again.
            int purged = PurgeNominations(m => !_availableMaps.Any(a => a.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)));
            if (purged > 0)
                Reply($"Removed {ColorGreen}{purged}{ColorDefault} pending nomination(s) that matched.");

            // A live vote's options are locked in — omission takes effect from the next vote.
            if (_voteInProgress)
                Reply("Note: a vote is currently in progress; its options are unchanged. Omission applies from the next vote.");
        }
    }

    private int PurgeNominations(Func<MapItem, bool> shouldRemove)
    {
        var removed = _nominatedMaps.Where(shouldRemove).ToList();
        foreach (var map in removed)
        {
            _nominatedMaps.RemoveAll(m => m.Id == map.Id);
            var owners = _nominationOwner.Where(kv => kv.Value.Id == map.Id).Select(kv => kv.Key).ToList();
            foreach (var steamId in owners)
            {
                _nominationOwner.Remove(steamId);
                _hasNominatedSteamIds.Remove(steamId);
            }
        }
        return removed.Count;
    }

    private void AttemptUnomitMapFromChat(CCSPlayerController? player, string? words)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }
        AttemptUnomitMap(p, words);
    }

    // Shared by chat (!unomitmap) and console (css_unomitmap). Caller may be null (console).
    private void AttemptUnomitMap(CCSPlayerController? caller, string? words)
        => ToggleMapsEnabled(caller, words, enable: true);

    private void PrintOmitList(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        RefreshMapPools(force: true);

        var lines = new List<string>();
        foreach (var e in _collectionMaps.Where(e => !e.Enabled))
            lines.Add($" {ColorDefault}[Collection] {ColorGreen}{TitleOf(e)}{ColorDefault} ({e.Id})");
        foreach (var e in _workshopMaps.Where(e => !e.Enabled))
            lines.Add($" {ColorDefault}[Workshop] {ColorGreen}{TitleOf(e)}{ColorDefault} ({e.Id})");
        foreach (var e in _stockMaps.Where(e => !e.Enabled))
            lines.Add($" {ColorDefault}[Stock] {ColorGreen}{e.Title}{ColorDefault} ({e.Map})");

        if (lines.Count == 0)
        {
            p.PrintToChat($" {ColorDefault}No maps are currently omitted (disabled).");
            return;
        }

        // Stock maps mostly start out disabled, so cap the chat spam and point at the
        // console list for the full picture.
        const int maxLines = 20;
        p.PrintToChat($" {ColorDefault}--- {ColorGreen}Omitted (Disabled) Maps ({lines.Count}) {ColorDefault}---");
        foreach (var line in lines.Take(maxLines)) p.PrintToChat(line);
        if (lines.Count > maxLines)
            p.PrintToChat($" {ColorDefault}...and {ColorGreen}{lines.Count - maxLines}{ColorDefault} more (see the json files for the full list).");
    }

    private void PrintAddList(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        RefreshMapPools(force: true);

        if (_workshopMaps.Count == 0)
        {
            p.PrintToChat($" {ColorDefault}No manually added workshop maps. Use {ColorGreen}!addmap <workshop ID>{ColorDefault} or edit workshop_maps.json.");
            return;
        }

        p.PrintToChat($" {ColorDefault}--- {ColorGreen}workshop_maps.json ({_workshopMaps.Count}) {ColorDefault}---");
        foreach (var e in _workshopMaps.OrderBy(TitleOf, StringComparer.OrdinalIgnoreCase))
        {
            string state = e.Enabled ? "" : " — disabled";
            p.PrintToChat($" {ColorGreen}{TitleOf(e)}{ColorDefault} (ID: {ColorGreen}{e.Id}{ColorDefault}){state}");
        }
    }

    // --- FinishVote Logic ---
    private void AttemptEndVote(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        if (!_voteInProgress)
        {
            p.PrintToChat($" {ColorDefault}There is no vote currently in progress.");
            return;
        }

        Log("ADMIN", $"{PlayerTag(p)} ran !endvote");
        Server.PrintToChatAll($" {ColorDefault}Admin {ColorGreen}{p.PlayerName}{ColorDefault} ended the vote early.");
        EndVote();
    }

    // --- ChangeNow Logic ---
    // Immediately switches to the map already queued as the next map (set by a
    // finished vote or !setnextmap), instead of waiting for the end of the match.
    private void AttemptChangeNow(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        if (_voteInProgress)
        {
            p.PrintToChat($" {ColorDefault}A vote is currently in progress — wait for it to finish (or use !endvote).");
            return;
        }

        if (string.IsNullOrEmpty(_pendingMapId))
        {
            p.PrintToChat($" {ColorDefault}No next map is queued yet.");
            return;
        }

        string mapId = _pendingMapId;
        string mapName = GetMapName(mapId);
        Log("ADMIN", $"{PlayerTag(p)} ran !changenow -> {mapName} ({mapId})");
        Log("MAPCHANGE", $"Changing now to queued next map {mapName} ({mapId})");
        Server.PrintToChatAll($" {ColorDefault}Admin {ColorGreen}{p.PlayerName}{ColorDefault} is changing the map to {ColorGreen}{mapName}{ColorDefault} now!");
        _mapChangeTimer?.Kill();
        _mapChangeTimer = null;
        _expectedMapId = mapId;
        _expectedMapName = mapName;
        ChangeMap(mapId);
    }

    // --- EndWarmup Logic ---
    private void AttemptEndWarmup(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        if (!IsWarmup())
        {
            p.PrintToChat($" {ColorDefault}The server is not currently in warmup.");
            return;
        }

        Log("ADMIN", $"{PlayerTag(p)} ran !endwarmup");
        Server.PrintToChatAll($" {ColorDefault}Admin {ColorGreen}{p.PlayerName}{ColorDefault} ended the warmup.");
        Server.ExecuteCommand("mp_warmup_end");
    }

    // --- ForceVote Logic ---
    private void AttemptForceVote(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault} You do not have permission to use this command.");
            return;
        }

        if (IsWarmup())
        {
            p.PrintToChat($" {ColorDefault} Cannot start vote during warmup.");
            return;
        }

        if (_matchEnded)
        {
            p.PrintToChat($" {ColorDefault} Cannot start vote after match end.");
            return;
        }

        if (_voteInProgress)
        {
            p.PrintToChat($" {ColorDefault}A vote is already in progress.");
            return;
        }

        Log("ADMIN", $"{PlayerTag(p)} ran !forcevote");
        Server.PrintToChatAll($" {ColorDefault} Admin {ColorGreen}{p.PlayerName}{ColorDefault} initiated a map vote.");
        StartMapVote(isRtv: false, isForceVote: true);
    }

    // --- ForceRtv Logic ---
    // Starts an RTV-style vote directly (30 second timer, map changes as soon as
    // the vote ends) without needing the player RTV threshold to be reached.
    private void AttemptForceRtv(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!IsVoteAdmin(p))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        if (IsWarmup())
        {
            p.PrintToChat($" {ColorDefault}Cannot start a vote during warmup.");
            return;
        }

        if (_matchEnded)
        {
            p.PrintToChat($" {ColorDefault}Cannot start a vote after match end.");
            return;
        }

        if (_voteInProgress)
        {
            p.PrintToChat($" {ColorDefault}A vote is already in progress.");
            return;
        }

        RefreshMapPools(force: true);
        if (_availableMaps.Count == 0)
        {
            p.PrintToChat($" {ColorDefault}No maps loaded yet.");
            return;
        }

        Log("ADMIN", $"{PlayerTag(p)} ran !forcertv");
        Server.PrintToChatAll($" {ColorDefault}Admin {ColorGreen}{p.PlayerName}{ColorDefault} initiated an {ColorGreen}RTV vote{ColorDefault}! The map will change when the vote ends.");
        StartMapVote(isRtv: true);
    }

    private void StartMapVote(bool isRtv, bool isForceVote = false)
    {
        // Re-read the map files right before options are built, so hand edits made
        // at any point up to this moment are reflected in this very vote.
        RefreshMapPools(force: true);

        // 1. If force vote happening AFTER a finished vote, we must backup the result
        if (isForceVote && _voteFinished)
        {
            _previousWinningMapId = _pendingMapId;
            _previousWinningMapName = _nextMapName;
        }
        else if (!isForceVote) // If normal RTV or Scheduled vote, clear previous just in case
        {
            _previousWinningMapId = null;
            _previousWinningMapName = null;
        }

        _voteInProgress = true; 
        bool isRevote = isForceVote && _previousWinningMapId != null;
        _isScheduledVote = (!isRtv && !isForceVote) || (isForceVote && !isRevote);
        _isForceVote = isForceVote;
        _isRtvVote = isRtv;

        _nextMapName = null;
        _pendingMapId = null;
        _currentVoteRoundDuration = 0;
        _playerVotes.Clear(); _activeVoteOptions.Clear(); _nominatingPlayers.Clear(); _playerNominationPage.Clear();

        // Nominations are re-checked against the freshly rebuilt pool AND the
        // recent-map list here, in case a map was disabled (file edit or !omitmap)
        // after it was nominated. This is the final gate — nothing disabled or
        // recent can enter the vote.
        var mapsToVote = _nominatedMaps
            .Where(m => _availableMaps.Any(a => a.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)))
            .Where(m => !Config.OmitRecentMaps || !IsRecentMap(m))
            .ToList();
        int slotsNeeded = Config.VoteOptionsCount - mapsToVote.Count;
        if (slotsNeeded > 0 && _availableMaps.Count > 0)
        {
            var potentialMaps = _availableMaps
                .Where(m => !mapsToVote.Any(n => n.Id == m.Id))
                .Where(m => !IsCurrentMap(m));

            if (Config.OmitRecentMaps)
            {
                // Strictly omit recently played maps from the random pool, even if that
                // leaves fewer options than VoteOptionsCount. IsSameMap compares by
                // workshop ID *and* normalized name so legacy/engine-name history
                // entries are matched too.
                potentialMaps = potentialMaps.Where(m => !IsRecentMap(m)).ToList();
            }

            mapsToVote.AddRange(potentialMaps.OrderBy(_ => Random.Shared.Next()).Take(slotsNeeded));
        }

        for (int i = 0; i < mapsToVote.Count; i++) _activeVoteOptions[i + 1] = mapsToVote[i].Id;

        // The extend option occupies key 0 so "0" / "!0" casts it. Only offered when
        // there is at least one real map option (an extend-only vote is pointless).
        if (Config.EnableExtendVote && mapsToVote.Count > 0)
            _activeVoteOptions[0] = ExtendOptionId;

        Server.PrintToChatAll($" {ColorDefault}--- {ColorGreen}Vote for the Next Map! {ColorDefault}---");

        // RTV votes and revotes are always 30s; scheduled/forced votes are timed only
        // when enable_timed_vote is on, otherwise they stay open across rounds.
        _voteIsTimed = isRtv || isRevote || Config.EnableTimedVote;
        if (_voteIsTimed)
        {
            float voteSeconds = (isRtv || isRevote) ? 30.0f : Config.TimedVoteSeconds;
            _voteTotalSeconds = voteSeconds;
            _voteEndsAtUtc = DateTime.UtcNow.AddSeconds(voteSeconds);
            Server.PrintToChatAll($" {ColorDefault}Vote ending in {ColorGreen}{voteSeconds:0}{ColorDefault} seconds!");
            _voteEndTimer = AddTimer(voteSeconds, () => EndVote(), TimerFlags.STOP_ON_MAPCHANGE);
        }
        else
        {
            Server.PrintToChatAll(Config.VoteOpenForRounds > 1
               ? $" {ColorDefault}Vote will remain open for {ColorGreen}{Config.VoteOpenForRounds}{ColorDefault} rounds."
               : $" {ColorDefault}Vote will remain open until the round ends.");
        }

        // The center panel replaces the chat option list entirely while enabled;
        // players still vote by typing the number in chat.
        if (Config.EnableVoteHud)
            RefreshVotePanel(force: true);
        else
            PrintVoteOptionsToAll();

        // Chat reminders are redundant while the center-screen HUD is enabled —
        // the panel already keeps the options in front of everyone.
        if (Config.EnableReminders && !Config.EnableVoteHud)
        {
            _reminderTimer = AddTimer(Config.ReminderIntervalSeconds, () => {
                if (_unloaded) return;
                try { foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot))) { p.PrintToChat($" {ColorDefault}Reminder: Please vote for the next map!"); PrintVoteOptionsToPlayer(p); } }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Reminder timer error: {ex.Message}"); }
            }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        }

        // 0.25s refresh tick: updates the vote panel (countdown, tallies, and one
        // marquee step for long names) in HUD mode, or re-sends the VOTE NOW prompt.
        _centerMessageTimer = AddTimer(0.25f, () => {
            if (_unloaded) return;
            try
            {
                if (Config.EnableVoteHud)
                {
                    _hudScrollTick++; // advances the long-name marquee one step
                    RefreshVotePanel();
                }
                else
                {
                    string msg = _voteIsTimed
                        ? $"VOTE NOW! Time Remaining: {VoteSecondsRemaining()}s"
                        : "VOTE NOW!";
                    foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot)))
                        p.PrintToCenter(msg);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Center message timer error: {ex.Message}"); }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult HandleVoteInput(CCSPlayerController player, string input)
    {
        if (int.TryParse(input, out int option) && _activeVoteOptions.ContainsKey(option))
        {
            _playerVotes[player.Slot] = option;
            string votedMapId = _activeVoteOptions[option];
            string votedMapName = OptionName(votedMapId);
            Log("VOTE", $"{PlayerTag(player)} voted for option {option}: {votedMapName} ({votedMapId})");
            player.PrintToChat($" {ColorDefault}You voted for: {ColorGreen}{votedMapName}{ColorDefault}");
            // Tallies changed — refresh the panel right away instead of waiting
            // for the next 0.5s tick.
            RefreshVotePanel();
            return HookResult.Handled;
        }
        return HookResult.Continue;
    }

    private void EndVote()
    {
        if (!_voteInProgress) return;
        _voteInProgress = false; _voteFinished = true; _reminderTimer?.Kill(); _reminderTimer = null;
        _centerMessageTimer?.Kill(); _centerMessageTimer = null;
        _voteEndTimer?.Kill(); _voteEndTimer = null;
        bool wasTimed = _voteIsTimed;
        _voteIsTimed = false;
        string winningMapId; int voteCount;

        // Special Logic: Force Vote with existing winner
        if (_isForceVote && _previousWinningMapId != null && _playerVotes.Count == 0)
        {
            // Revert to previous winner
            winningMapId = _previousWinningMapId;
            _nextMapName = _previousWinningMapName;
            voteCount = 0; // Or -1 to indicate override?
            Server.PrintToChatAll($" {ColorDefault}No votes cast! Keeping previously selected next map.");
        }
        else if (_playerVotes.Count == 0)
        {
            // Random fallback picks among real maps only — silently extending when
            // nobody voted would be a do-nothing outcome.
            var mapOptions = _activeVoteOptions.Where(kv => kv.Value != ExtendOptionId).ToList();
            if (mapOptions.Count == 0) return;
            var pick = mapOptions[Random.Shared.Next(mapOptions.Count)];
            winningMapId = pick.Value; _nextMapName = GetMapName(winningMapId); voteCount = 0;
            Server.PrintToChatAll($" {ColorDefault}No votes cast! Randomly selecting a map...");
        }
        else
        {
            // Random tie-break: without it, ties always favor whichever option
            // happens to group first, which biases repeated votes the same way.
            var grouped = _playerVotes.Values.GroupBy(v => v).ToList();
            int topCount = grouped.Max(g => g.Count());
            var tied = grouped.Where(g => g.Count() == topCount).ToList();
            var winner = tied[Random.Shared.Next(tied.Count)];
            winningMapId = _activeVoteOptions[winner.Key]; _nextMapName = OptionName(winningMapId); voteCount = winner.Count();
        }

        // Extend won: the next map IS the current map. Resolve it now so everything
        // downstream (!nextmap, the end-of-match change) points at the real map.
        bool extendWon = winningMapId == ExtendOptionId;
        if (extendWon)
        {
            winningMapId = _currentMapId ?? Server.MapName;
            _nextMapName = GetMapName(winningMapId);
        }

        // Clear flags
        _isForceVote = false;
        _previousWinningMapId = null;
        _previousWinningMapName = null;

        // show_midvote_progress belongs to the round-based vote system — it does
        // nothing at all for timed votes.
        if (voteCount > 0 && Config.ShowMidVoteProgress && !wasTimed)
        {
            PrintVoteProgress();
        }

        string winnerLabel = extendWon ? $"Extend Current Map ({_nextMapName})" : (_nextMapName ?? winningMapId);
        string rawMsg = $"Winner: {winnerLabel}" + (voteCount > 0 ? $" with {voteCount} votes!" : " (Random/Previous)");
        string dashes = new string('-', rawMsg.Length);

        Log("WINNER", $"Selected {winnerLabel} ({winningMapId}) with {voteCount} vote(s); total votes cast: {_playerVotes.Count}");
        Server.PrintToChatAll($" {ColorDefault}{dashes}");
        Server.PrintToChatAll($" {ColorDefault}Winner: {ColorGreen}{winnerLabel}{ColorDefault}" + (voteCount > 0 ? $" with {ColorGreen}{voteCount}{ColorDefault} votes!" : " (Random/Previous)"));
        Server.PrintToChatAll($" {ColorDefault}{dashes}");

        // The panel hides the moment the vote is over: stop the per-tick re-send
        // first, then blank the hint so it doesn't linger through its fade.
        _voteCenterHtmlCache = "";
        if (Config.EnableVoteHud)
            foreach (var p in GetHumanPlayers())
                try { p.PrintToCenterHtml(" "); } catch { }

        _nominatedMaps.Clear(); _hasNominatedSteamIds.Clear(); _nominationOwner.Clear(); _nominationNames.Clear();

        // Extend never changes the map now — not even for RTV votes (an immediate
        // change to the same map would just restart it). The current map is pended
        // as the next map and reloads at the end of the match.
        if (extendWon)
        {
            _isRtvVote = false;
            _pendingMapId = winningMapId;
            _nextMapSetByAdmin = false;
            Log("MAPCHANGE", $"Extend won — next map pended as current map {_nextMapName} ({winningMapId})");
            Server.PrintToChatAll($" {ColorDefault}The current map will be {ColorGreen}extended{ColorDefault} — it plays again after this match.");
            return;
        }

        // RTV wins change the map immediately (after the configured short delay).
        // Scheduled and force votes still pend the winner for end-of-match.
        if (_isRtvVote)
        {
            _isRtvVote = false;
            _pendingMapId = null;
            _nextMapSetByAdmin = false;
            float delay = Math.Max(0f, Config.RtvDelaySeconds);
            string mapIdToChange = winningMapId;
            string mapNameToChange = _nextMapName ?? GetMapName(winningMapId);
            Log("MAPCHANGE", $"RTV winner {mapNameToChange} ({mapIdToChange}) — changing map in {delay}s");
            Server.PrintToChatAll($" {ColorDefault}Changing map to {ColorGreen}{mapNameToChange}{ColorDefault} in {ColorGreen}{delay:0.#}{ColorDefault}s...");
            _mapChangeTimer = AddTimer(delay, () =>
            {
                if (_unloaded) return;
                _expectedMapId = mapIdToChange;
                _expectedMapName = mapNameToChange;
                try { ChangeMap(mapIdToChange); }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] RTV map change error: {ex.Message}"); }
            }, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        _pendingMapId = winningMapId; 
        Server.PrintToChatAll($" {ColorDefault}Map will change at the end of the match."); 
    }

    private void PrintVoteOptionsToAll() { foreach (var p in GetHumanPlayers()) PrintVoteOptionsToPlayer(p); }
    private void PrintVoteOptionsToPlayer(CCSPlayerController player) { player.PrintToChat($" {ColorDefault}Type the {ColorGreen}number{ColorDefault} to vote:"); foreach (var kvp in OrderedVoteOptions()) player.PrintToChat($" {ColorGreen}[{kvp.Key}] {ColorDefault}{OptionName(kvp.Value)}"); }

    // Map options first (1..N), the extend option (key 0) last — matching the order
    // players see everywhere (chat list, HUD, tallies).
    private IEnumerable<KeyValuePair<int, string>> OrderedVoteOptions()
        => _activeVoteOptions.OrderBy(kv => kv.Key == 0 ? int.MaxValue : kv.Key);

    // Display name for a vote option id (handles the extend sentinel).
    private string OptionName(string optionId)
        => optionId == ExtendOptionId ? "Extend Current Map" : GetMapName(optionId);

    private int VoteSecondsRemaining()
        => _voteIsTimed ? Math.Max(0, (int)Math.Ceiling((_voteEndsAtUtc - DateTime.UtcNow).TotalSeconds)) : 0;

    // --- Center vote panel (enable_vote_hud) ---
    // A display-only center-screen panel in the style of CS2MenuManager's
    // CenterHtmlMenu, implemented in-house with the native PrintToCenterHtml —
    // no dependency, no entities, no input of its own. A yellow header telling
    // players to vote in chat, the numbered options with live tallies, and (for
    // timed votes) a countdown footer that goes green -> yellow -> red as time
    // runs out. While the panel is on, the chat option list is suppressed.
    //
    // Transport: the cached html is re-sent EVERY TICK — exactly what CSS core's
    // own CenterHtmlMenu does. The display only stays up while it keeps being fed
    // (the duration parameter does not reliably keep it alive, so sending only on
    // change leaves visible gaps), while identical re-fires and content swaps are
    // both seamless — the panel sits rock solid and updates without flashing. The
    // string itself is rebuilt only on the 0.25s timer and when a vote is cast.
    //
    // Alignment: the panel centers every line individually, which scatters the
    // option numbers when map names differ in width. Each line is therefore
    // right-padded with width-estimated non-breaking spaces up to the widest line,
    // so centering leaves all left edges — and the numbers — in a straight column.

    private string _voteCenterHtmlCache = "";
    private float _voteTotalSeconds;
    private int _hudScrollTick;

    // The display panel word-wraps at a fixed width that cannot be widened from the
    // server, and a wrapped line wrecks both the layout and the number column. So
    // every line is kept under this width budget (in the estimator's units — tuned
    // against observed wrap points): map names that don't fit are shown through a
    // scrolling marquee window while the vote number and tally stay intact.
    private const float HudMaxLineUnits = 26f;

    // Rendered width of one pad character in the estimator's units — the
    // granularity of the alignment padding, and the knob to tune if the column
    // drifts.
    private const float NbspUnits = 0.5f;

    // Alt+255 (U+00A0), the blank glyph used for padding. Written as an escape so
    // no file-encoding step can mangle it.
    //
    // Placement is what actually matters: a run of blanks at the END of a line is
    // trimmed by the renderer no matter which blank character it is (that is why
    // both the literal and the &nbsp; entity silently vanished, leaving the short
    // rows centered and the numbers ragged). The pad is therefore emitted BETWEEN
    // the map name and the vote tally, where it is flanked by visible glyphs and
    // cannot be trimmed.
    private const char PadChar = '\u00A0';

    private static string HtmlEscape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Rough proportional-font glyph width in "average glyph" units. Used to size
    // the alignment padding and the marquee window, so close is good enough.
    private static float CharWidth(char c)
        => "iljtf!.,:;'|()[] 1".IndexOf(c) >= 0 ? 0.55f
         : "mwMW@".IndexOf(c) >= 0 ? 1.5f
         : char.IsUpper(c) || char.IsDigit(c) ? 1.15f : 1.0f;

    private static float EstimateHudWidth(string s)
    {
        float w = 0f;
        foreach (char c in s) w += CharWidth(c);
        return w;
    }

    // Returns the name unchanged when it fits the budget; otherwise a window into
    // "name • name • ..." that slides one character per rebuild tick, so long names
    // scroll continuously (one step per 0.25s tick) while the rest stays put.
    private string MarqueeName(string name, float budgetUnits)
    {
        if (EstimateHudWidth(name) <= budgetUnits) return name;
        string cycle = name + " • ";
        int start = _hudScrollTick % cycle.Length;
        var sb = new StringBuilder();
        float w = 0f;
        for (int i = 0; i < cycle.Length; i++)
        {
            char c = cycle[(start + i) % cycle.Length];
            float cw = CharWidth(c);
            if (w + cw > budgetUnits) break;
            w += cw;
            sb.Append(c);
        }
        return sb.ToString();
    }

    private string BuildVoteCenterHtml()
    {
        var sb = new StringBuilder();

        // Header and countdown are left CENTERED (like the reference menu's title):
        // they carry no padding, so nothing of theirs can be trimmed.
        sb.Append("<font color='#FFD700'><b>Type a number to vote</b></font>");

        foreach (var kvp in OrderedVoteOptions())
        {
            int votes = _playerVotes.Values.Count(v => v == kvp.Key);
            string prefix = $"{kvp.Key}: ";
            string tally = $" ({votes})";
            // The extra margin keeps a scrolled row visibly narrower than the shared
            // padded target — estimator error on a full-budget marquee row otherwise
            // pushes its real width past the others and skews the number column.
            float budget = HudMaxLineUnits - EstimateHudWidth(prefix) - EstimateHudWidth(tally) - 2.0f;
            string name = MarqueeName(OptionName(kvp.Value), budget);

            // Pad BETWEEN the name and the tally so every option row ends up the same
            // width: identical widths + per-line centering == aligned left edges, so
            // the numbers form a straight column (and the tallies align on the right).
            float used = EstimateHudWidth(prefix) + EstimateHudWidth(name) + EstimateHudWidth(tally);
            int pad = Math.Max(0, (int)Math.Round((HudMaxLineUnits - used) / NbspUnits));

            sb.Append($"<br><font color='#FF5722'>{kvp.Key}:</font> <font color='#EAD1AF'>{HtmlEscape(name)}</font>");
            sb.Append(PadChar, pad);
            sb.Append($" <font color='#B0B0B0'>({votes})</font>");
        }

        if (_voteIsTimed)
        {
            int remaining = VoteSecondsRemaining();
            float frac = _voteTotalSeconds > 0 ? remaining / _voteTotalSeconds : 1f;
            string color = frac > 0.5f ? "#4CAF50" : frac > 0.25f ? "#FFD700" : "#FF4444";
            sb.Append($"<br><font color='{color}'>{remaining}s remaining</font>");
        }

        return sb.ToString();
    }

    // Rebuilds the cached panel string; the per-tick listener does the sending.
    private void RefreshVotePanel(bool force = false)
    {
        if (!Config.EnableVoteHud || !_voteInProgress) return;
        _voteCenterHtmlCache = BuildVoteCenterHtml();
    }

    // Feeds the display every tick so it never expires; same string, no flash.
    private void OnVoteHudTick()
    {
        if (_unloaded || _voteCenterHtmlCache.Length == 0) return;
        try
        {
            foreach (var p in GetHumanPlayers())
                p.PrintToCenterHtml(_voteCenterHtmlCache);
        }
        catch { /* never let a render error hit the tick */ }
    }

    private string GetMapName(string mapId)
    {
        // Search ALL known maps so names stay resolvable even for a map that was
        // disabled mid-vote (its locked-in vote option must still display properly).
        var pool = AllKnownMaps();
        var map = pool.FirstOrDefault(m => m.Id == mapId);
        if (map != null) return map.Name;
        // Fallback: mapId might be a raw engine path like "workshop/123456/de_map" — check if any map's ID is contained within it
        map = pool.FirstOrDefault(m => !string.IsNullOrEmpty(m.Id) && mapId.Contains(m.Id, StringComparison.OrdinalIgnoreCase));
        if (map != null) return map.Name;
        // Last resort: the recent-map history may still know the title.
        return _recentMaps.FirstOrDefault(r => r.Id.Equals(mapId, StringComparison.OrdinalIgnoreCase))?.Name ?? mapId;
    }

    private void PrintVoteProgress()
    {
        if (_playerVotes.Count == 0) return;

        var voteCounts = _playerVotes.Values
            .GroupBy(v => v)
            .Select(g => new { OptionId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .ToList();

        Server.PrintToChatAll($" {ColorDefault}--- {ColorGreen}Vote Results {ColorDefault}---");
        foreach (var vote in voteCounts)
        {
            if (_activeVoteOptions.TryGetValue(vote.OptionId, out string? mapId))
            {
                Server.PrintToChatAll($" {ColorGreen}{vote.Count} {ColorDefault}votes - {ColorGreen}{OptionName(mapId)}");
            }
        }
    }

    // The round the scheduled vote opens on, derived from the match's own rules:
    // with clinching enabled a team can end the match at maxrounds/2 + 1, so the
    // vote schedules against that earliest possible end; with clinching disabled
    // every round is played, so the full mp_maxrounds applies (double the clinch
    // threshold, give or take the +1). Returns 0 when scheduling is impossible
    // (feature disabled, or no round limit to schedule against).
    internal static int ComputeVoteTriggerRound(int maxRounds, bool canClinch, int roundsBeforeEnd)
    {
        if (roundsBeforeEnd <= 0 || maxRounds <= 0) return 0;
        int effectiveEndRound = canClinch ? (maxRounds / 2 + 1) : maxRounds;
        return Math.Max(1, effectiveEndRound - roundsBeforeEnd);
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_unloaded) return HookResult.Continue;
        if (_voteFinished || _voteInProgress || _nextMapSetByAdmin) return HookResult.Continue;
        if (Config.VoteRoundsBeforeEnd <= 0) return HookResult.Continue; // 0 disables the scheduled vote entirely
        if (IsWarmup()) return HookResult.Continue;
        try
        {
            // Cvars are read every round start so live changes to mp_maxrounds /
            // mp_match_can_clinch are respected without a reload.
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 0;
            bool canClinch = ConVar.Find("mp_match_can_clinch")?.GetPrimitiveValue<bool>() ?? true;
            int triggerRound = ComputeVoteTriggerRound(maxRounds, canClinch, Config.VoteRoundsBeforeEnd);
            if (triggerRound <= 0) return HookResult.Continue; // no round limit — nothing to schedule against

            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
            // ">=" rather than "==" so a plugin loaded (or cvars changed) mid-match
            // still opens the vote at the first round start past the trigger.
            if (rules != null && rules.TotalRoundsPlayed + 1 >= triggerRound) StartMapVote(isRtv: false);
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] OnRoundStart error: {ex.Message}"); }
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_unloaded) return HookResult.Continue;
        // Timed votes end on their own timer — round ends don't advance or close
        // them, and their HUD (when enabled) is already up for the whole vote.
        if (_voteInProgress && _isScheduledVote && !_voteIsTimed)
        {
            _currentVoteRoundDuration++;
            if (_currentVoteRoundDuration >= Config.VoteOpenForRounds)
            {
                EndVote();
            }
            else
            {
                // Optionally announce progress
                int roundsLeft = Config.VoteOpenForRounds - _currentVoteRoundDuration;
                if (roundsLeft == 1)
                {
                    Server.PrintToChatAll($" {ColorDefault}Map Vote continuing! Vote will remain open until the round ends.");
                }
                else
                {
                    Server.PrintToChatAll($" {ColorDefault}Map Vote continuing! {ColorGreen}{roundsLeft}{ColorDefault} rounds remaining.");
                }

                if (Config.ShowMidVoteProgress)
                {
                    PrintVoteProgress();
                }
            }
        }
        return HookResult.Continue;
    }
    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (_unloaded) return HookResult.Continue;
        _matchEnded = true;

        if (_voteInProgress)
        {
            EndVote();
        }

        if (!string.IsNullOrEmpty(_pendingMapId))
        {
            string mapIdToChange = _pendingMapId;
            string mapNameToChange = GetMapName(mapIdToChange);
            Log("MAPCHANGE", $"End-of-match map change scheduled to {mapNameToChange} ({mapIdToChange})");
            Server.PrintToChatAll($" {ColorDefault} Changing map to {ColorGreen}{mapNameToChange}{ColorDefault}!");
            float postDelay = Math.Min(Math.Max(0f, Config.PostMapChangeDelay), 15.0f);
            _mapChangeTimer = AddTimer(postDelay, () =>
            {
                if (_unloaded) return;
                _expectedMapId = mapIdToChange;
                _expectedMapName = mapNameToChange;
                try { ChangeMap(mapIdToChange); }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Map change error (possibly mid-transition): {ex.Message}"); }
            }, TimerFlags.STOP_ON_MAPCHANGE);
        }
        return HookResult.Continue;
    }
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { } player)
        {
            _rtvVoters.Remove(player.Slot);
            _playerVotes.Remove(player.Slot);
            CloseNominationMenu(player);
            CloseForcemapMenu(player);
            CloseSetNextMapMenu(player);
            _helpMenuPlayers.Remove(player.Slot);

            // A departure lowers the RTV threshold — re-check so the remaining voters
            // aren't left stuck at e.g. 3/3 with no way to trigger the vote.
            if (!_unloaded && !_voteInProgress && !_voteFinished && Config.EnableRtv && _rtvVoters.Count > 0)
            {
                Server.NextFrame(() =>
                {
                    if (_unloaded || _voteInProgress || _voteFinished || _rtvVoters.Count == 0) return;
                    int currentPlayers = GetHumanPlayers().Count();
                    if (currentPlayers == 0) return;
                    int votesNeeded = Math.Max(1, (int)Math.Ceiling(currentPlayers * Config.RtvRatio));
                    if (_rtvVoters.Count >= votesNeeded)
                    {
                        Log("RTV", $"Threshold reached after disconnect ({_rtvVoters.Count}/{votesNeeded}) — starting vote");
                        Server.PrintToChatAll($" {ColorDefault}RTV Threshold reached! Starting vote...");
                        StartMapVote(isRtv: true);
                    }
                });
            }
        }
        return HookResult.Continue;
    }

    // --- Logging Infrastructure ---
    // Lightweight per-day event log. Only explicitly invoked for: map votes cast by players,
    // RTVs, nominations, admin commands, map changes, and winning-map selections.
    private void Log(string category, string message)
    {
        if (_unloaded || string.IsNullOrEmpty(_logBaseDir)) return;
        try
        {
            var now = DateTime.Now;
            string dayDir = Path.Combine(_logBaseDir, now.ToString("yyyy-MM-dd"));
            string path = Path.Combine(dayDir, "events.log");
            string line = $"[{now:HH:mm:ss}] {category} | {message}{Environment.NewLine}";

            // All disk I/O happens on a worker thread. Synchronous AppendAllText on the
            // game thread stalls the tick on slow/contended disks (visible as client
            // jitter every time someone votes or RTVs). The lock keeps lines whole.
            Task.Run(() =>
            {
                try
                {
                    lock (_logLock)
                    {
                        Directory.CreateDirectory(dayDir);
                        File.AppendAllText(path, line, Encoding.UTF8);
                    }
                }
                catch { /* logging must never crash the plugin */ }
            });
        }
        catch { /* logging must never crash the plugin */ }
    }

    private static string PlayerTag(CCSPlayerController? p)
        => p == null ? "unknown" : $"{p.PlayerName} ({p.SteamID})";
}
