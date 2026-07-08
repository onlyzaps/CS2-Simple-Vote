using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CS2SimpleVote;

// --- Configuration ---
public class VoteConfig : BasePluginConfig
{
    [JsonPropertyName("steam_api_key")] public string SteamApiKey { get; set; } = "YOUR_STEAM_API_KEY_HERE";
    [JsonPropertyName("collection_id")] public string CollectionId { get; set; } = "123456789";
    [JsonPropertyName("vote_on_round")] public int VoteOnRound { get; set; } = 10;
    [JsonPropertyName("enable_rtv")] public bool EnableRtv { get; set; } = true;
    [JsonPropertyName("enable_nominate")] public bool EnableNominate { get; set; } = true;
    [JsonPropertyName("nominate_per_page")] public int NominatePerPage { get; set; } = 6;
    [JsonPropertyName("rtv_ratio")] public float RtvRatio { get; set; } = 0.60f;
    [JsonPropertyName("rtv_change_delay")] public float RtvDelaySeconds { get; set; } = 5.0f;
    [JsonPropertyName("postmap_change_delay")] public float PostMapChangeDelay { get; set; } = 10.0f;
    [JsonPropertyName("vote_options_count")] public int VoteOptionsCount { get; set; } = 8;
    [JsonPropertyName("vote_reminder_enabled")] public bool EnableReminders { get; set; } = true;
    [JsonPropertyName("vote_reminder_interval")] public float ReminderIntervalSeconds { get; set; } = 30.0f;

    // --- New Features ---
    [JsonPropertyName("server_name")] public string ServerName { get; set; } = "My CS2 Server";
    [JsonPropertyName("enable_map_message")] public bool EnableMapMessage { get; set; } = true;
    [JsonPropertyName("map_message_interval")] public float CurrentMapMessageInterval { get; set; } = 300.0f;
    [JsonPropertyName("omit_recent_maps")] public bool OmitRecentMaps { get; set; } = true;
    [JsonPropertyName("recent_maps_count")] public int RecentMapsCount { get; set; } = 5;
    [JsonPropertyName("vote_open_for_rounds")] public int VoteOpenForRounds { get; set; } = 1;
    [JsonPropertyName("show_midvote_progress")] public bool ShowMidVoteProgress { get; set; } = true;
    [JsonPropertyName("admins")] public List<ulong> Admins { get; set; } = new();

    // Background collection refresh. The plugin re-fetches the Workshop collection
    // from Steam on this interval so maps added/removed from the collection are
    // picked up without a server restart. Set to 0 (or negative) to disable and
    // only fetch once at load. Minimum enforced interval is 1 minute.
    [JsonPropertyName("collection_refresh_minutes")] public float CollectionRefreshMinutes { get; set; } = 30.0f;
}

public class MapItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

// --- Main Plugin ---
public class CS2SimpleVote : BasePlugin, IPluginConfig<VoteConfig>
{
    public override string ModuleName => "CS2SimpleVote";
    public override string ModuleVersion => "1.3.0";

    private const string ColorDefault = "\x01";
    private const string ColorGreen = "\x04";

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
    private int _forceVoteTimeRemaining;
    private string? _nextMapName;
    private string? _pendingMapId;
    private readonly HashSet<int> _rtvVoters = new();
    private readonly Dictionary<int, string> _activeVoteOptions = new();
    private readonly Dictionary<int, int> _playerVotes = new();

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

    // State: Custom maps (added via !addmap) and omitted-map patterns (!omitmap).
    // _customMaps is merged into _availableMaps at load and after every collection
    // refresh. _omittedPatterns are word filters applied wherever players can pick
    // maps (scheduled votes, RTV votes, nominations) — admin commands like
    // !forcemap / !setnextmap deliberately still see everything.
    private List<MapItem> _customMaps = new();
    private List<string> _omittedPatterns = new();

    // State: Nomination
    private readonly List<MapItem> _nominatedMaps = new();
    private readonly HashSet<ulong> _hasNominatedSteamIds = new();
    private readonly Dictionary<ulong, MapItem> _nominationOwner = new();
    private readonly Dictionary<ulong, string> _nominationNames = new();
    private readonly Dictionary<int, List<MapItem>> _nominatingPlayers = new();
    private readonly Dictionary<int, int> _playerNominationPage = new();

    private CommandInfo.CommandListenerCallback? _playerChatDelegate;

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
    private string _cacheFilePath = "";
    private string _customMapsFilePath = "";
    private string _omittedMapsFilePath = "";
    private string _configFilePath = "";

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
        if (Config.NominatePerPage < 1) Config.NominatePerPage = 6;
        // rtv_ratio is a fraction of connected players (0–1]. Clamp bad values so a
        // config typo like 60 (instead of 0.60) can't make RTV impossible.
        if (Config.RtvRatio <= 0f) Config.RtvRatio = 0.60f;
        if (Config.RtvRatio > 1f) Config.RtvRatio = 1f;
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

    // Keys that existed in older versions and are no longer part of the config schema.
    // Includes the removed disco feature and the pre-rename keys.
    private static readonly string[] LegacyConfigKeys =
    {
        "disco_party",
        "vote_round",          // -> vote_on_round
        "rtv_percentage",      // -> rtv_ratio
        "enable_recent_maps",  // -> omit_recent_maps
        "show_map_message"     // -> enable_map_message
    };

    // Surgically deletes stale keys from CS2SimpleVote.json without touching anything
    // else (values, ordering, or unknown keys the user may have added). Only rewrites
    // the file if something was actually removed.
    private void StripLegacyConfigKeys()
    {
        if (string.IsNullOrEmpty(_configFilePath) || !File.Exists(_configFilePath)) return;

        try
        {
            string json = File.ReadAllText(_configFilePath);
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            if (node is not JsonObject obj) return;

            var removed = new List<string>();
            foreach (var key in LegacyConfigKeys)
                if (obj.Remove(key)) removed.Add(key);

            if (removed.Count == 0) return;

            string cleaned = obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, cleaned);
            Console.WriteLine($"[CS2SimpleVote] Removed legacy config key(s): {string.Join(", ", removed)}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Could not clean legacy config keys: {ex.Message}");
        }
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
        _cacheFilePath = Path.Combine(configDir, "map_cache.json");
        _customMapsFilePath = Path.Combine(configDir, "custom_maps.json");
        _omittedMapsFilePath = Path.Combine(configDir, "omitted_maps.json");
        _configFilePath = Path.Combine(configDir, "CS2SimpleVote.json");
        _logBaseDir = Path.Combine(configDir, "logs");
        try { Directory.CreateDirectory(_logBaseDir); } catch { /* non-fatal */ }

        // Remove keys left over from older versions (CounterStrikeSharp merges its
        // generated config into the existing file and never deletes stale keys, so
        // e.g. disco_party and the pre-rename keys would otherwise linger forever).
        StripLegacyConfigKeys();

        // Clear existing memory state before loading
        _recentMaps.Clear();

        // 1. Load Data Immediately (Sync)
        LoadMapHistory();
        LoadMapCache();
        LoadCustomMaps();
        LoadOmittedPatterns();
        MergeCustomMapsInto(_availableMaps);

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

        // HookMode.Pre so returning HookResult.Handled suppresses the chat message —
        // plugin commands and vote/menu input are processed but never broadcast.
        _playerChatDelegate = OnPlayerChat;
        AddCommandListener("say", _playerChatDelegate, HookMode.Pre);
        AddCommandListener("say_team", _playerChatDelegate, HookMode.Pre);

        AddCommand("css_dumpmaps", "Dump all available map names to console", (caller, cmdInfo) =>
        {
            if (caller != null) { cmdInfo.ReplyToCommand("This command can only be used from the server console."); return; }
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
                if (!Config.Admins.Contains(caller.SteamID))
                {
                    cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                    return;
                }
            }
            string searchTerm = cmdInfo.GetArg(1);
            if (string.IsNullOrEmpty(searchTerm)) { cmdInfo.ReplyToCommand("[CS2SimpleVote] Usage: css_setnextmap <partial map name>"); return; }
            if (_availableMaps.Count == 0) { cmdInfo.ReplyToCommand("[CS2SimpleVote] No maps loaded yet."); return; }

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
                if (!Config.Admins.Contains(caller.SteamID))
                {
                    cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                    return;
                }
            }
            string searchTerm = cmdInfo.GetArg(1);
            if (string.IsNullOrEmpty(searchTerm)) { cmdInfo.ReplyToCommand("[CS2SimpleVote] Usage: css_forcemap <partial map name>"); return; }
            if (_availableMaps.Count == 0) { cmdInfo.ReplyToCommand("[CS2SimpleVote] No maps loaded yet."); return; }

            var match = FindBestMapMatch(searchTerm);
            if (match == null) { cmdInfo.ReplyToCommand($"[CS2SimpleVote] No map found matching: {searchTerm}"); return; }

            Log("ADMIN", $"{PlayerTag(caller)} ran css_forcemap -> {match.Name} ({match.Id})");
            Log("MAPCHANGE", $"Forcing map change to {match.Name} ({match.Id})");
            cmdInfo.ReplyToCommand($"[CS2SimpleVote] Forcing map change to: {match.Name} (ID: {match.Id})");
            Server.PrintToChatAll($" {ColorDefault}Map is being changed to {ColorGreen}{match.Name}{ColorDefault}.");
            _expectedMapId = match.Id;
            _expectedMapName = match.Name;
            Server.ExecuteCommand($"host_workshop_map {match.Id}");
        });

        AddCommand("css_forcertv", "Start an RTV-style map vote (map changes as soon as the vote ends)", (caller, cmdInfo) =>
        {
            if (caller != null && !Config.Admins.Contains(caller.SteamID))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            if (_matchEnded) { cmdInfo.ReplyToCommand("[CS2SimpleVote] Cannot start a vote after match end."); return; }
            if (_voteInProgress) { cmdInfo.ReplyToCommand("[CS2SimpleVote] A vote is already in progress."); return; }
            if (_availableMaps.Count == 0) { cmdInfo.ReplyToCommand("[CS2SimpleVote] No maps loaded yet."); return; }

            Log("ADMIN", $"{PlayerTag(caller)} ran css_forcertv");
            Server.PrintToChatAll($" {ColorDefault}An {ColorGreen}RTV vote{ColorDefault} has been started! The map will change when the vote ends.");
            StartMapVote(isRtv: true);
        });

        AddCommand("css_addmap", "Add a workshop map by ID to custom_maps.json", (caller, cmdInfo) =>
        {
            if (caller != null && !Config.Admins.Contains(caller.SteamID))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            AttemptAddMap(caller, cmdInfo.GetArg(1));
        });

        AddCommand("css_omitmap", "Omit maps matching the given words from votes and nominations", (caller, cmdInfo) =>
        {
            if (caller != null && !Config.Admins.Contains(caller.SteamID))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            AttemptOmitMap(caller, cmdInfo.ArgString);
        });

        AddCommand("css_unomitmap", "Remove a saved omit pattern so matching maps can appear again", (caller, cmdInfo) =>
        {
            if (caller != null && !Config.Admins.Contains(caller.SteamID))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            AttemptUnomitMap(caller, cmdInfo.ArgString);
        });

        AddCommand("css_addlist", "List maps added via addmap (custom_maps.json)", (caller, cmdInfo) =>
        {
            if (caller != null && !Config.Admins.Contains(caller.SteamID))
            {
                cmdInfo.ReplyToCommand("You do not have permission to use this command.");
                return;
            }
            if (caller != null) { PrintAddList(caller); return; }

            if (_customMaps.Count == 0) { cmdInfo.ReplyToCommand("[CS2SimpleVote] No custom maps added."); return; }
            cmdInfo.ReplyToCommand($"--- CS2SimpleVote: {_customMaps.Count} Custom-Added Map(s) ---");
            foreach (var m in _customMaps.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
                cmdInfo.ReplyToCommand($"  {m.Name}  (ID: {m.Id})");
            cmdInfo.ReplyToCommand($"--- End ({_customMaps.Count} custom map(s)) ---");
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
        _customMaps.Clear();
        _omittedPatterns.Clear();
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

        // Re-read the hand-editable lists each map so manual edits to
        // custom_maps.json / omitted_maps.json are picked up without a reload.
        LoadCustomMaps();
        LoadOmittedPatterns();
        MergeCustomMapsInto(_availableMaps);

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
                Server.PrintToChatAll($" {ColorDefault}You're playing {ColorGreen}{displayMapName}{ColorDefault} on {ColorGreen}{Config.ServerName}{ColorDefault}!");
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
        _forceVoteTimeRemaining = 0;

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
        try
        {
            // Serialize on the game thread (cheap, and avoids racing _recentMaps),
            // but do the disk write on a worker thread so a slow disk can't hitch a tick.
            string json = JsonSerializer.Serialize(_recentMaps);
            string path = _historyFilePath;
            Task.Run(() =>
            {
                try { File.WriteAllText(path, json); }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save history: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save history: {ex.Message}"); }
    }

    private void LoadMapCache()
    {
        if (File.Exists(_cacheFilePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<List<MapItem>>(File.ReadAllText(_cacheFilePath));
                if (cached != null) _availableMaps = cached;
            }
            catch { /* Ignore corrupt cache */ }
        }
    }

    private void SaveMapCache(List<MapItem> maps)
    {
        try { File.WriteAllText(_cacheFilePath, JsonSerializer.Serialize(maps)); }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save cache: {ex.Message}"); }
    }

    // --- Custom Maps (custom_maps.json) ---

    private void LoadCustomMaps()
    {
        try
        {
            if (!File.Exists(_customMapsFilePath)) { _customMaps = new List<MapItem>(); return; }
            string json;
            lock (_logLock) { json = File.ReadAllText(_customMapsFilePath); } // writers hold the same lock
            var loaded = JsonSerializer.Deserialize<List<MapItem>>(json);
            _customMaps = loaded?.Where(m => !string.IsNullOrEmpty(m.Id)).ToList() ?? new List<MapItem>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Failed to load custom_maps.json: {ex.Message}");
            _customMaps = new List<MapItem>();
        }
    }

    private void SaveCustomMaps()
    {
        try
        {
            // Serialize on the game thread; write on a worker thread (same pattern as history).
            string json = JsonSerializer.Serialize(_customMaps, new JsonSerializerOptions { WriteIndented = true });
            string path = _customMapsFilePath;
            Task.Run(() =>
            {
                try { lock (_logLock) { File.WriteAllText(path, json); } }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save custom_maps.json: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save custom_maps.json: {ex.Message}"); }
    }

    // Merge custom maps into a map list in place (dedup by workshop ID). Called on the
    // initial cache load, on every map start, and after every collection refresh so
    // custom maps survive the background collection swap.
    private void MergeCustomMapsInto(List<MapItem> target)
    {
        foreach (var cm in _customMaps)
        {
            if (!target.Any(m => m.Id.Equals(cm.Id, StringComparison.OrdinalIgnoreCase)))
                target.Add(new MapItem { Id = cm.Id, Name = cm.Name });
        }
    }

    // --- Omitted Maps (omitted_maps.json) ---
    // The file is a plain JSON array of word patterns, e.g. ["motel night", "aim"].
    // A map is omitted when its name contains EVERY word of a pattern,
    // case-insensitively and regardless of word order. So "motel night" hides
    // "Motel at Night" and "Night Motel v2".

    private void LoadOmittedPatterns()
    {
        try
        {
            if (!File.Exists(_omittedMapsFilePath)) { _omittedPatterns = new List<string>(); return; }
            string json;
            lock (_logLock) { json = File.ReadAllText(_omittedMapsFilePath); } // writers hold the same lock
            var loaded = JsonSerializer.Deserialize<List<string>>(json);
            _omittedPatterns = loaded?
                .Select(NormalizePattern)
                .Where(p => p.Length > 0)
                .Distinct()
                .ToList() ?? new List<string>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Failed to load omitted_maps.json: {ex.Message}");
            _omittedPatterns = new List<string>();
        }
    }

    private void SaveOmittedPatterns()
    {
        try
        {
            string json = JsonSerializer.Serialize(_omittedPatterns, new JsonSerializerOptions { WriteIndented = true });
            string path = _omittedMapsFilePath;
            Task.Run(() =>
            {
                try { lock (_logLock) { File.WriteAllText(path, json); } }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save omitted_maps.json: {ex.Message}"); }
            });
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save omitted_maps.json: {ex.Message}"); }
    }

    // Lowercase, collapse whitespace: "  Motel   NIGHT " -> "motel night"
    private static string NormalizePattern(string s)
        => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();

    private static bool MapMatchesPattern(string mapName, string pattern)
    {
        var words = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        return words.All(w => mapName.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    // Two patterns are the same filter if they contain the same words in any order.
    private static bool SamePattern(string a, string b)
    {
        var wa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x);
        var wb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).OrderBy(x => x);
        return wa.SequenceEqual(wb);
    }

    private bool IsOmittedMap(MapItem map) => _omittedPatterns.Any(p => MapMatchesPattern(map.Name, p));

    private void ResolveCurrentMapAndUpdateHistory(string currentMapName)
    {
        string? idToAdd = null;
        string? nameToAdd = null;

        // 1) Authoritative source: the workshop ID this plugin itself passed to
        //    host_workshop_map right before the transition. This is what makes the
        //    recent-maps exclusion reliable — the engine map name for workshop maps
        //    (e.g. "de_dust2_remake") usually contains NEITHER the workshop ID nor
        //    the workshop title, so name-based matching alone can never work.
        if (!string.IsNullOrEmpty(_expectedMapId))
        {
            idToAdd = _expectedMapId;
            nameToAdd = _availableMaps.FirstOrDefault(m => m.Id == _expectedMapId)?.Name
                        ?? _expectedMapName
                        ?? _expectedMapId;
        }
        _expectedMapId = null;
        _expectedMapName = null;

        // 2) Fallback (map changed outside the plugin): ID embedded in the engine path,
        //    then normalized title comparison ("De_Dust2 [24/7]" vs "de_dust2").
        if (idToAdd == null)
        {
            var mapItem = _availableMaps.FirstOrDefault(m => !string.IsNullOrEmpty(m.Id) && currentMapName.Contains(m.Id, StringComparison.OrdinalIgnoreCase));

            if (mapItem == null)
            {
                string cleanName = NormalizeMapName(currentMapName.Split('/').Last());
                if (cleanName.Length >= 3)
                {
                    mapItem = _availableMaps.FirstOrDefault(m =>
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
            nameToAdd = _availableMaps.FirstOrDefault(m => m.Id == idToAdd)?.Name ?? idToAdd;
        }

        _currentMapId = idToAdd;

        if (!Config.OmitRecentMaps) return;

        _recentMaps.RemoveAll(m => m.Id.Equals(idToAdd, StringComparison.OrdinalIgnoreCase));
        _recentMaps.Add(new MapItem { Id = idToAdd, Name = nameToAdd ?? idToAdd });
        while (_recentMaps.Count > Config.RecentMapsCount) _recentMaps.RemoveAt(0);

        // Backfill names for any legacy entries now that _availableMaps may be populated
        for (int i = 0; i < _recentMaps.Count; i++)
        {
            if (_recentMaps[i].Name == _recentMaps[i].Id || string.IsNullOrEmpty(_recentMaps[i].Name))
            {
                var known = _availableMaps.FirstOrDefault(m => m.Id == _recentMaps[i].Id);
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

        // Legacy history entries may store the engine map name in Id (e.g. "de_dust2")
        string ain = NormalizeMapName(a.Id);
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
        Log("FETCH", isInitial ? "Initial collection fetch started." : "Background collection refresh started.");
        Task.Run(() => FetchCollectionMaps(isInitial, token));
    }

    private async Task FetchCollectionMaps(bool isInitial, CancellationToken token = default)
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

            // File I/O stays on this background thread (using the local list, so no
            // race with the game thread), but the swap of _availableMaps and the flag
            // updates are marshalled onto the game thread. The game thread iterates
            // _availableMaps constantly (menus, vote setup, name lookups); mutating it
            // from a worker thread risks torn reads / InvalidOperationException.
            SaveMapCache(maps);
            Server.NextFrame(() =>
            {
                if (_unloaded) { _collectionFetchRunning = false; _isApiLoading = false; return; }

                // Custom maps (!addmap) live outside the collection — re-merge them so
                // a background refresh can never silently drop them.
                MergeCustomMapsInto(maps);

                // Diff old vs new by workshop ID so the log shows exactly what changed.
                // Computed here on the game thread where the old list is safe to read.
                var oldIds = _availableMaps.Select(m => m.Id).ToHashSet();
                var newIds = maps.Select(m => m.Id).ToHashSet();
                int added = maps.Count(m => !oldIds.Contains(m.Id));
                int removed = _availableMaps.Count(m => !newIds.Contains(m.Id));

                // Single atomic reference swap. Any code already reading the previous
                // list keeps a valid reference; new reads see the new list. No tearing.
                _availableMaps = maps;
                _hasLoadedCollectionMaps = true;
                _isApiLoading = false;
                _collectionFetchRunning = false;

                string change = (added == 0 && removed == 0) ? "no changes" : $"+{added} added, -{removed} removed";
                Log("FETCH", $"{(isInitial ? "Initial fetch" : "Refresh")} complete: {maps.Count} maps ({change}).");
                Console.WriteLine($"[CS2SimpleVote] Collection {(isInitial ? "loaded" : "refreshed")}: {maps.Count} maps ({change}).");
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
    private MapItem? FindBestMapMatch(string searchTerm)
    {
        if (string.IsNullOrEmpty(searchTerm) || _availableMaps.Count == 0) return null;

        // Exact match (case-insensitive)
        var exact = _availableMaps.FirstOrDefault(m => m.Name.Equals(searchTerm, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // Starts with - pick shortest name (most specific)
        var startsWith = _availableMaps
            .Where(m => m.Name.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name.Length)
            .FirstOrDefault();
        if (startsWith != null) return startsWith;

        // Contains - pick shortest name (most specific)
        var contains = _availableMaps
            .Where(m => m.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name.Length)
            .FirstOrDefault();
        if (contains != null) return contains;

        return null;
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
        if (!string.IsNullOrEmpty(map.Id) && Server.MapName.Contains(map.Id, StringComparison.OrdinalIgnoreCase)) return true;
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
        if (cmd.Equals("finishvote", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptFinishVote(p)); return HookResult.Handled; }
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
        player!.PrintToChat($" {ColorDefault}Redisplaying vote options. You may recast your vote.");
        PrintVoteOptionsToPlayer(player);
    }

    private void AttemptVoteDebug(CCSPlayerController? player)
    {
        if (player != null && !IsValidPlayer(player)) return;
        
        bool isConsole = player == null;
        if (!isConsole && !Config.Admins.Contains(player!.SteamID))
        {
            player.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
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
            $" {ColorDefault}Custom Maps: {_customMaps.Count} | Omit Patterns: {_omittedPatterns.Count}",
            $" {ColorDefault}Target Collection ID: {Config.CollectionId}"
        };

        if (_activeVoteOptions.Count > 0)
        {
            debugInfo.Add($" {ColorDefault}--- {ColorGreen}Active Vote Data {ColorDefault}---");
            foreach (var kvp in _activeVoteOptions)
            {
                int votes = _playerVotes.Values.Count(v => v == kvp.Key);
                debugInfo.Add($" {ColorDefault}Option [{kvp.Key}] {ColorGreen}{GetMapName(kvp.Value)}{ColorDefault}: {votes} votes");
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
                ForceVoteTimeRemaining = _forceVoteTimeRemaining,
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

    private void PrintHelp(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        bool isAdmin = Config.Admins.Contains(p.SteamID);

        p.PrintToChat($" {ColorDefault}---{ColorGreen} CS2SimpleVote Commands {ColorDefault}---");

        if (isAdmin)
        {
            p.PrintToChat($" {ColorGreen}!addmap [workshop ID] {ColorDefault}- Add a workshop map to the map pool (Admin only)");
            p.PrintToChat($" {ColorGreen}!addlist {ColorDefault}- List custom-added maps (Admin only)");
            p.PrintToChat($" {ColorGreen}!endwarmup {ColorDefault}- End the current warmup (Admin only)");
            p.PrintToChat($" {ColorGreen}!finishvote {ColorDefault}- End an active vote early (Admin only)");
            p.PrintToChat($" {ColorGreen}!forcemap [name] {ColorDefault}- Force change map (Admin only)");
            p.PrintToChat($" {ColorGreen}!forcertv {ColorDefault}- Start an RTV vote, map changes at vote end (Admin only)");
            p.PrintToChat($" {ColorGreen}!forcevote {ColorDefault}- Force start map vote (Admin only)");
            p.PrintToChat($" {ColorGreen}!omitmap [words] {ColorDefault}- Hide matching maps from votes/nominations (Admin only)");
            p.PrintToChat($" {ColorGreen}!omitlist {ColorDefault}- List saved omit patterns (Admin only)");
            p.PrintToChat($" {ColorGreen}!setnextmap [name] {ColorDefault}- Set the next map directly (Admin only)");
            p.PrintToChat($" {ColorGreen}!unomitmap [words] {ColorDefault}- Remove an omit pattern (Admin only)");
            p.PrintToChat($" {ColorGreen}!votedebug {ColorDefault}- Show debug info (Admin only)");
        }

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
        if (_voteInProgress || _voteFinished) { p.PrintToChat($" {ColorDefault}Voting has already finished."); return; }
        
        bool isRenomination = _hasNominatedSteamIds.Contains(p.SteamID);
        if (!isRenomination && _nominatedMaps.Count >= Config.VoteOptionsCount) { p.PrintToChat($" {ColorDefault}The nomination list is full!"); return; }

        var validMaps = _availableMaps
            .Where(m => !_nominatedMaps.Any(n => n.Id == m.Id))
            .Where(m => !IsCurrentMap(m))
            .Where(m => !IsOmittedMap(m))
            .ToList();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            validMaps = validMaps.Where(m => m.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)).ToList();
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

        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        var validMaps = _availableMaps.ToList();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            validMaps = validMaps.Where(m => m.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)).ToList();
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
            Server.ExecuteCommand($"host_workshop_map {map.Id}");
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
                Server.ExecuteCommand($"host_workshop_map {selectedMap.Id}");
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

        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        var validMaps = _availableMaps.ToList();

        if (!string.IsNullOrEmpty(searchTerm))
        {
            validMaps = validMaps.Where(m => m.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)).ToList();
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
        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }
        AttemptAddMap(p, arg);
    }

    // Shared by chat (!addmap) and console (css_addmap). Caller may be null (console).
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

        if (_customMaps.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
        {
            Reply($"Workshop ID {ColorGreen}{id}{ColorDefault} is already in custom_maps.json.");
            return;
        }
        var existing = _availableMaps.FirstOrDefault(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            Reply($"{ColorGreen}{existing.Name}{ColorDefault} ({id}) is already available via the collection.");
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

                // Re-check duplicates: a collection refresh may have landed while we were fetching.
                if (_customMaps.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) ||
                    _availableMaps.Any(m => m.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    LateReply($"{title} ({id}) is already available.");
                    return;
                }

                _customMaps.Add(new MapItem { Id = id, Name = title });
                SaveCustomMaps();
                _availableMaps.Add(new MapItem { Id = id, Name = title });

                Log("ADMIN", $"addmap: {title} ({id}) added to custom_maps.json");
                LateReply($"Added {ColorGreen}{title}{ColorDefault} ({id}) to custom maps. It is now available for votes and nominations.");
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

    // --- OmitMap Logic (omitted_maps.json) ---

    private void AttemptOmitMapFromChat(CCSPlayerController? player, string? words)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }
        AttemptOmitMap(p, words);
    }

    // Shared by chat (!omitmap) and console (css_omitmap). Caller may be null (console).
    private void AttemptOmitMap(CCSPlayerController? caller, string? words)
    {
        void Reply(string text)
        {
            if (caller != null && caller.IsValid) caller.PrintToChat($" {ColorDefault}{text}");
            else Console.WriteLine($"[CS2SimpleVote] {text.Replace(ColorGreen, "").Replace(ColorDefault, "")}");
        }

        string pattern = NormalizePattern(words ?? "");
        if (pattern.Length == 0)
        {
            Reply($"Usage: {ColorGreen}!omitmap <word(s)>{ColorDefault} — e.g. !omitmap motel night");
            return;
        }

        var matches = _availableMaps.Where(m => MapMatchesPattern(m.Name, pattern)).ToList();
        var customMatches = matches
            .Where(m => _customMaps.Any(c => c.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var collectionMatches = matches.Except(customMatches).ToList();

        // Maps that came from !addmap are simply deleted from custom_maps.json —
        // no omit pattern is needed to keep them out.
        if (customMatches.Count > 0)
        {
            foreach (var m in customMatches)
            {
                _customMaps.RemoveAll(c => c.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
                _availableMaps.RemoveAll(c => c.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase));
            }
            SaveCustomMaps();
            Log("ADMIN", $"{PlayerTag(caller)} omitmap '{pattern}': removed {customMatches.Count} custom map(s): {string.Join(", ", customMatches.Select(m => $"{m.Name} ({m.Id})"))}");
            Reply($"Removed {ColorGreen}{customMatches.Count}{ColorDefault} custom map(s) from custom_maps.json: {ColorGreen}{string.Join(", ", customMatches.Select(m => m.Name))}");
        }

        // Collection maps can't be deleted from the collection, so they're filtered
        // by pattern. The pattern is also saved when nothing matched right now, so it
        // applies to maps added to the collection later.
        if (collectionMatches.Count > 0 || matches.Count == 0)
        {
            if (_omittedPatterns.Any(existing => SamePattern(existing, pattern)))
            {
                Reply($"Pattern '{ColorGreen}{pattern}{ColorDefault}' is already in omitted_maps.json.");
            }
            else
            {
                _omittedPatterns.Add(pattern);
                SaveOmittedPatterns();
                Log("ADMIN", $"{PlayerTag(caller)} omitmap '{pattern}': pattern saved, currently matching {collectionMatches.Count} collection map(s)");

                if (collectionMatches.Count > 0)
                {
                    const int maxNames = 8;
                    string names = string.Join(", ", collectionMatches.Take(maxNames).Select(m => m.Name));
                    if (collectionMatches.Count > maxNames) names += $", +{collectionMatches.Count - maxNames} more";
                    Reply($"Omitting {ColorGreen}{collectionMatches.Count}{ColorDefault} map(s) from votes and nominations: {ColorGreen}{names}");
                }
                else
                {
                    Reply($"No maps currently match '{ColorGreen}{pattern}{ColorDefault}'. Pattern saved — it will apply to matching maps added later.");
                }
            }
        }

        // Pull any now-omitted maps out of the pending nomination list, and free their
        // nominators to nominate again.
        int purged = PurgeNominations(m => IsOmittedMap(m) || !_availableMaps.Any(a => a.Id.Equals(m.Id, StringComparison.OrdinalIgnoreCase)));
        if (purged > 0)
            Reply($"Removed {ColorGreen}{purged}{ColorDefault} pending nomination(s) that matched.");

        // A live vote's options are locked in — omission takes effect from the next vote.
        if (_voteInProgress && matches.Count > 0)
            Reply("Note: a vote is currently in progress; its options are unchanged. Omission applies from the next vote.");
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
        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }
        AttemptUnomitMap(p, words);
    }

    // Shared by chat (!unomitmap) and console (css_unomitmap). Caller may be null (console).
    private void AttemptUnomitMap(CCSPlayerController? caller, string? words)
    {
        void Reply(string text)
        {
            if (caller != null && caller.IsValid) caller.PrintToChat($" {ColorDefault}{text}");
            else Console.WriteLine($"[CS2SimpleVote] {text.Replace(ColorGreen, "").Replace(ColorDefault, "")}");
        }

        string pattern = NormalizePattern(words ?? "");
        if (pattern.Length == 0)
        {
            Reply($"Usage: {ColorGreen}!unomitmap <word(s)>{ColorDefault} — use {ColorGreen}!omitlist{ColorDefault} to see saved patterns.");
            return;
        }

        int removed = _omittedPatterns.RemoveAll(existing => SamePattern(existing, pattern));
        if (removed > 0)
        {
            SaveOmittedPatterns();
            Log("ADMIN", $"{PlayerTag(caller)} unomitmap '{pattern}'");
            Reply($"Removed omit pattern '{ColorGreen}{pattern}{ColorDefault}'. Matching maps can appear in votes again.");
        }
        else
        {
            Reply($"No saved pattern matches '{ColorGreen}{pattern}{ColorDefault}'. Use {ColorGreen}!omitlist{ColorDefault} to see saved patterns.");
        }
    }

    private void PrintOmitList(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        if (_omittedPatterns.Count == 0)
        {
            p.PrintToChat($" {ColorDefault}No omit patterns saved.");
            return;
        }

        p.PrintToChat($" {ColorDefault}--- {ColorGreen}Omitted Map Patterns ({_omittedPatterns.Count}) {ColorDefault}---");
        foreach (var pattern in _omittedPatterns)
        {
            int count = _availableMaps.Count(m => MapMatchesPattern(m.Name, pattern));
            p.PrintToChat($" {ColorGreen}'{pattern}'{ColorDefault} — currently matches {ColorGreen}{count}{ColorDefault} map(s)");
        }
    }

    private void PrintAddList(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        if (_customMaps.Count == 0)
        {
            p.PrintToChat($" {ColorDefault}No custom maps added. Use {ColorGreen}!addmap <workshop ID>{ColorDefault} to add one.");
            return;
        }

        p.PrintToChat($" {ColorDefault}--- {ColorGreen}Custom-Added Maps ({_customMaps.Count}) {ColorDefault}---");
        foreach (var m in _customMaps.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            p.PrintToChat($" {ColorGreen}{m.Name}{ColorDefault} (ID: {ColorGreen}{m.Id}{ColorDefault})");
        }
    }

    // --- FinishVote Logic ---
    private void AttemptFinishVote(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault}You do not have permission to use this command.");
            return;
        }

        if (!_voteInProgress)
        {
            p.PrintToChat($" {ColorDefault}There is no vote currently in progress.");
            return;
        }

        Log("ADMIN", $"{PlayerTag(p)} ran !finishvote");
        Server.PrintToChatAll($" {ColorDefault}Admin {ColorGreen}{p.PlayerName}{ColorDefault} ended the vote early.");
        EndVote();
    }

    // --- EndWarmup Logic ---
    private void AttemptEndWarmup(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!Config.Admins.Contains(p.SteamID))
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

        if (!Config.Admins.Contains(p.SteamID))
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

        if (!Config.Admins.Contains(p.SteamID))
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

        // Nominations are re-checked against the omit list here in case a pattern was
        // added after the map was nominated.
        var mapsToVote = _nominatedMaps.Where(m => !IsOmittedMap(m)).ToList();
        int slotsNeeded = Config.VoteOptionsCount - mapsToVote.Count;
        if (slotsNeeded > 0 && _availableMaps.Count > 0)
        {
            var potentialMaps = _availableMaps
                .Where(m => !mapsToVote.Any(n => n.Id == m.Id))
                .Where(m => !IsCurrentMap(m))
                .Where(m => !IsOmittedMap(m));

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
        Server.PrintToChatAll($" {ColorDefault}--- {ColorGreen}Vote for the Next Map! {ColorDefault}---");

        if (isRtv)
        {
            Server.PrintToChatAll($" {ColorDefault}Vote ending in 30 seconds!");
            _voteEndTimer = AddTimer(30.0f, () => EndVote(), TimerFlags.STOP_ON_MAPCHANGE);
        }
        else if (isForceVote && _previousWinningMapId != null) // Scenario: Vote already happened
        {
             _forceVoteTimeRemaining = 30;
             // Chat message handled by center timer updates or initial print? 
             // Request says center message: "VOTE NOW! Time Remaining: 30s"
             // Typically we should also print to chat.
             Server.PrintToChatAll($" {ColorDefault}Vote ending in 30 seconds!");
             _voteEndTimer = AddTimer(30.0f, () => EndVote(), TimerFlags.STOP_ON_MAPCHANGE);
        }
        else
        {
            // Scenario: Normal vote or "Force vote behaving as normal vote"
            Server.PrintToChatAll(Config.VoteOpenForRounds > 1
               ? $" {ColorDefault}Vote will remain open for {ColorGreen}{Config.VoteOpenForRounds}{ColorDefault} rounds."
               : $" {ColorDefault}Vote will remain open until the round ends.");
        }

        PrintVoteOptionsToAll();

        if (Config.EnableReminders)
        {
            _reminderTimer = AddTimer(Config.ReminderIntervalSeconds, () => {
                if (_unloaded) return;
                try { foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot))) { p.PrintToChat($" {ColorDefault}Reminder: Please vote for the next map!"); PrintVoteOptionsToPlayer(p); } }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Reminder timer error: {ex.Message}"); }
            }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
        }

        _centerMessageTimer = AddTimer(1.0f, () => {
            if (_unloaded) return;
            try
            {
                string msg;
                if (_isForceVote && _previousWinningMapId != null)
                {
                    _forceVoteTimeRemaining--;
                    msg = $"VOTE NOW! Time Remaining: {Math.Max(0, _forceVoteTimeRemaining)}s";
                }
                else
                {
                    msg = "VOTE NOW!";
                }

                foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot)))
                {
                    p.PrintToCenter(msg);
                }
            }
            catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Center message timer error: {ex.Message}"); }
        }, TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    private HookResult HandleVoteInput(CCSPlayerController player, string input)
    {
        if (int.TryParse(input, out int option) && _activeVoteOptions.ContainsKey(option)) { _playerVotes[player.Slot] = option; string votedMapId = _activeVoteOptions[option]; string votedMapName = GetMapName(votedMapId); Log("VOTE", $"{PlayerTag(player)} voted for option {option}: {votedMapName} ({votedMapId})"); player.PrintToChat($" {ColorDefault}You voted for: {ColorGreen}{votedMapName}{ColorDefault}"); return HookResult.Handled; }
        return HookResult.Continue;
    }

    private void EndVote()
    {
        if (!_voteInProgress) return;
        _voteInProgress = false; _voteFinished = true; _reminderTimer?.Kill(); _reminderTimer = null;
        _centerMessageTimer?.Kill(); _centerMessageTimer = null;
        _voteEndTimer?.Kill(); _voteEndTimer = null;
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
            if (_activeVoteOptions.Count == 0) return;
            var randomKey = _activeVoteOptions.Keys.ElementAt(Random.Shared.Next(_activeVoteOptions.Count));
            winningMapId = _activeVoteOptions[randomKey]; _nextMapName = GetMapName(winningMapId); voteCount = 0;
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
            winningMapId = _activeVoteOptions[winner.Key]; _nextMapName = GetMapName(winningMapId); voteCount = winner.Count();
        }
        
        // Clear flags
        _isForceVote = false;
        _previousWinningMapId = null;
        _previousWinningMapName = null;

        if (voteCount > 0 && Config.ShowMidVoteProgress)
        {
            PrintVoteProgress();
        }

        string rawMsg = $"Winner: {_nextMapName}" + (voteCount > 0 ? $" with {voteCount} votes!" : " (Random/Previous)");
        string dashes = new string('-', rawMsg.Length);

        Log("WINNER", $"Selected {_nextMapName} ({winningMapId}) with {voteCount} vote(s); total votes cast: {_playerVotes.Count}");
        Server.PrintToChatAll($" {ColorDefault}{dashes}");
        Server.PrintToChatAll($" {ColorDefault}Winner: {ColorGreen}{_nextMapName}{ColorDefault}" + (voteCount > 0 ? $" with {ColorGreen}{voteCount}{ColorDefault} votes!" : " (Random/Previous)"));
        Server.PrintToChatAll($" {ColorDefault}{dashes}");

        _nominatedMaps.Clear(); _hasNominatedSteamIds.Clear(); _nominationOwner.Clear(); _nominationNames.Clear();

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
                try { Server.ExecuteCommand($"host_workshop_map {mapIdToChange}"); }
                catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] RTV map change error: {ex.Message}"); }
            }, TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }

        _pendingMapId = winningMapId; 
        Server.PrintToChatAll($" {ColorDefault}Map will change at the end of the match."); 
    }

    private void PrintVoteOptionsToAll() { foreach (var p in GetHumanPlayers()) PrintVoteOptionsToPlayer(p); }
    private void PrintVoteOptionsToPlayer(CCSPlayerController player) { player.PrintToChat($" {ColorDefault}Type the {ColorGreen}number{ColorDefault} to vote:"); foreach (var kvp in _activeVoteOptions) player.PrintToChat($" {ColorGreen}[{kvp.Key}] {ColorDefault}{GetMapName(kvp.Value)}"); }
    private string GetMapName(string mapId)
    {
        // Exact match first
        var map = _availableMaps.FirstOrDefault(m => m.Id == mapId);
        if (map != null) return map.Name;
        // Fallback: mapId might be a raw engine path like "workshop/123456/de_map" — check if any map's ID is contained within it
        map = _availableMaps.FirstOrDefault(m => !string.IsNullOrEmpty(m.Id) && mapId.Contains(m.Id, StringComparison.OrdinalIgnoreCase));
        return map?.Name ?? mapId;
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
                Server.PrintToChatAll($" {ColorGreen}{vote.Count} {ColorDefault}votes - {ColorGreen}{GetMapName(mapId)}");
            }
        }
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_unloaded) return HookResult.Continue;
        if (_voteFinished || _voteInProgress || _nextMapSetByAdmin) return HookResult.Continue;
        if (Config.VoteOnRound <= 0) return HookResult.Continue; // 0 disables the scheduled vote entirely
        try
        {
            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
            if (rules != null && rules.TotalRoundsPlayed + 1 == Config.VoteOnRound) StartMapVote(isRtv: false);
        }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] OnRoundStart error: {ex.Message}"); }
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_unloaded) return HookResult.Continue;
        if (_voteInProgress && _isScheduledVote)
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
                try { Server.ExecuteCommand($"host_workshop_map {mapIdToChange}"); }
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
