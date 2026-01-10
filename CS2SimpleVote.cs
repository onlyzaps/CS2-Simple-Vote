using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CS2SimpleVote;

// --- Configuration ---
public class VoteConfig : BasePluginConfig
{
    [JsonPropertyName("steam_api_key")] public string SteamApiKey { get; set; } = "YOUR_STEAM_API_KEY_HERE";
    [JsonPropertyName("collection_id")] public string CollectionId { get; set; } = "123456789";
    [JsonPropertyName("vote_round")] public int VoteRound { get; set; } = 10;
    [JsonPropertyName("enable_rtv")] public bool EnableRtv { get; set; } = true;
    [JsonPropertyName("enable_nominate")] public bool EnableNominate { get; set; } = true;
    [JsonPropertyName("nominate_per_page")] public int NominatePerPage { get; set; } = 6;
    [JsonPropertyName("rtv_percentage")] public float RtvPercentage { get; set; } = 0.60f;
    [JsonPropertyName("rtv_change_delay")] public float RtvDelaySeconds { get; set; } = 5.0f;
    [JsonPropertyName("vote_options_count")] public int VoteOptionsCount { get; set; } = 8;
    [JsonPropertyName("vote_reminder_enabled")] public bool EnableReminders { get; set; } = true;
    [JsonPropertyName("vote_reminder_interval")] public float ReminderIntervalSeconds { get; set; } = 30.0f;

    // --- New Features ---
    [JsonPropertyName("server_name")] public string ServerName { get; set; } = "My CS2 Server";
    [JsonPropertyName("show_map_message")] public bool ShowCurrentMapMessage { get; set; } = true;
    [JsonPropertyName("map_message_interval")] public float CurrentMapMessageInterval { get; set; } = 300.0f;
    [JsonPropertyName("enable_recent_maps")] public bool EnableRecentMaps { get; set; } = true;
    [JsonPropertyName("recent_maps_count")] public int RecentMapsCount { get; set; } = 5;
    [JsonPropertyName("randomize_startup_map")] public bool RandomizeFirstMap { get; set; } = false;
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
    public override string ModuleVersion => "1.1.0";

    public VoteConfig Config { get; set; } = new();

    // Data Sources
    private List<MapItem> _availableMaps = new();
    private List<string> _recentMapIds = new();
    private readonly HttpClient _httpClient = new();
    private CounterStrikeSharp.API.Modules.Timers.Timer? _reminderTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _mapInfoTimer;

    // State: Global (Static to persist through immediate map changes)
    private static bool _hasRandomizedStartupMap = false;

    // State: Voting
    private bool _voteInProgress;
    private bool _voteFinished;
    private bool _isScheduledVote;
    private string? _nextMapName;
    private string? _pendingMapId;
    private readonly HashSet<int> _rtvVoters = new();
    private readonly Dictionary<int, string> _activeVoteOptions = new();
    private readonly Dictionary<int, int> _playerVotes = new();

    // State: Nomination
    private readonly List<MapItem> _nominatedMaps = new();
    private readonly HashSet<ulong> _hasNominatedSteamIds = new();
    private readonly Dictionary<int, List<MapItem>> _nominatingPlayers = new();
    private readonly Dictionary<int, int> _playerNominationPage = new();

    // File Paths
    private string _historyFilePath = "";
    private string _cacheFilePath = "";

    public void OnConfigParsed(VoteConfig config)
    {
        Config = config;
        Config.VoteOptionsCount = Math.Clamp(Config.VoteOptionsCount, 2, 10);
        if (Config.NominatePerPage < 1) Config.NominatePerPage = 6;
    }

    public override void Load(bool hotReload)
    {
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

        // Clear existing memory state before loading
        _recentMapIds.Clear();

        // 1. Load Data Immediately (Sync)
        LoadMapHistory();
        LoadMapCache();

        // 2. Perform Startup Randomization
        CheckStartupRandomization();

        // 3. Start Background Update
        Task.Run(FetchCollectionMaps);

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        AddCommandListener("say", OnPlayerChat);
        AddCommandListener("say_team", OnPlayerChat);
    }

    private void CheckStartupRandomization()
    {
        if (Config.RandomizeFirstMap && !_hasRandomizedStartupMap && _availableMaps.Count > 0)
        {
            _hasRandomizedStartupMap = true;
            var randomMap = _availableMaps[new Random().Next(_availableMaps.Count)];

            Console.WriteLine($"[CS2SimpleVote] Randomizing startup map to: {randomMap.Name}");

            // Store this map in history immediately
            if (Config.EnableRecentMaps)
            {
                UpdateHistoryWithCurrentMap(randomMap.Name);
            }

            AddTimer(1.0f, () => {
                Server.ExecuteCommand($"host_workshop_map {randomMap.Id}");
            });
        }
    }

    private void OnMapStart(string mapName)
    {
        ResetState();
        Server.ExecuteCommand("mp_endmatch_votenextmap 0");

        if (Config.EnableRecentMaps)
        {
            UpdateHistoryWithCurrentMap(mapName);
        }

        if (Config.ShowCurrentMapMessage && Config.CurrentMapMessageInterval > 0)
        {
            _mapInfoTimer = AddTimer(Config.CurrentMapMessageInterval, () =>
            {
                // Find full title from available maps
                string displayMapName = _availableMaps.FirstOrDefault(m => mapName.Contains(m.Name) || m.Id == mapName || mapName.Contains(m.Id))?.Name ?? mapName;
                Server.PrintToChatAll($" \x01You're playing \x04{displayMapName}\x01 on \x04{Config.ServerName}\x01!");
            }, TimerFlags.REPEAT);
        }
    }

    private void ResetState()
    {
        _voteInProgress = false;
        _voteFinished = false;
        _isScheduledVote = false;
        _nextMapName = null;
        _pendingMapId = null;

        _rtvVoters.Clear();
        _playerVotes.Clear();
        _activeVoteOptions.Clear();
        _nominatedMaps.Clear();
        _hasNominatedSteamIds.Clear();
        _nominatingPlayers.Clear();
        _playerNominationPage.Clear();

        _reminderTimer?.Kill();
        _reminderTimer = null;

        _mapInfoTimer?.Kill();
        _mapInfoTimer = null;
    }

    // --- File Persistence ---

    private void LoadMapHistory()
    {
        if (File.Exists(_historyFilePath))
        {
            try { _recentMapIds = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_historyFilePath)) ?? new List<string>(); }
            catch { _recentMapIds = new List<string>(); }
        }
    }

    private void SaveMapHistory()
    {
        try { File.WriteAllText(_historyFilePath, JsonSerializer.Serialize(_recentMapIds)); }
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

    private void SaveMapCache()
    {
        try { File.WriteAllText(_cacheFilePath, JsonSerializer.Serialize(_availableMaps)); }
        catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Failed to save cache: {ex.Message}"); }
    }

    private void UpdateHistoryWithCurrentMap(string currentMapName)
    {
        var mapItem = _availableMaps.FirstOrDefault(m => currentMapName.Contains(m.Name) || currentMapName.Contains(m.Id));

        if (mapItem != null)
        {
            _recentMapIds.RemoveAll(id => id == mapItem.Id);
            _recentMapIds.Add(mapItem.Id);
            if (_recentMapIds.Count > Config.RecentMapsCount) _recentMapIds.RemoveAt(0);
            SaveMapHistory();
        }
    }

    // --- Steam API ---

    private async Task FetchCollectionMaps()
    {
        if (string.IsNullOrEmpty(Config.SteamApiKey) || string.IsNullOrEmpty(Config.CollectionId)) return;

        try
        {
            var collContent = new FormUrlEncodedContent(new[] {
                new KeyValuePair<string, string>("collectioncount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", Config.CollectionId)
            });

            var collRes = await _httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/", collContent);
            using var collDoc = JsonDocument.Parse(await collRes.Content.ReadAsStringAsync());

            var children = collDoc.RootElement.GetProperty("response").GetProperty("collectiondetails")[0].GetProperty("children");
            var fileIds = children.EnumerateArray().Select(c => c.GetProperty("publishedfileid").GetString()!).ToList();

            var itemPairs = new List<KeyValuePair<string, string>> { new("itemcount", fileIds.Count.ToString()) };
            for (int i = 0; i < fileIds.Count; i++) itemPairs.Add(new($"publishedfileids[{i}]", fileIds[i]));

            var itemRes = await _httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", new FormUrlEncodedContent(itemPairs));
            using var itemDoc = JsonDocument.Parse(await itemRes.Content.ReadAsStringAsync());

            var newMapList = new List<MapItem>();
            foreach (var item in itemDoc.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
            {
                newMapList.Add(new MapItem
                {
                    Id = item.GetProperty("publishedfileid").GetString()!,
                    Name = item.GetProperty("title").GetString()!
                });
            }

            _availableMaps = newMapList;
            Console.WriteLine($"[CS2SimpleVote] Updated {_availableMaps.Count} maps from Steam.");
            SaveMapCache();
            Server.NextFrame(CheckStartupRandomization);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Error: {ex.Message}");
        }
    }

    // --- Helpers ---
    private bool IsValidPlayer(CCSPlayerController? player) => player != null && player.IsValid && !player.IsBot && !player.IsHLTV;
    private bool IsWarmup() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod ?? false;
    private IEnumerable<CCSPlayerController> GetHumanPlayers() => Utilities.GetPlayers().Where(IsValidPlayer);

    // --- Command Handlers ---
    [ConsoleCommand("rtv", "Rock the Vote")]
    public void OnRtvCommand(CCSPlayerController? player, CommandInfo command) => AttemptRtv(player);

    [ConsoleCommand("nominate", "Nominate a map")]
    public void OnNominateCommand(CCSPlayerController? player, CommandInfo command) => AttemptNominate(player);

    [ConsoleCommand("revote", "Recast vote")]
    public void OnRevoteCommand(CCSPlayerController? player, CommandInfo command) => AttemptRevote(player);

    [ConsoleCommand("nextmap", "Show next map")]
    public void OnNextMapCommand(CCSPlayerController? player, CommandInfo command) => PrintNextMap(player);

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        if (!IsValidPlayer(player)) return HookResult.Continue;
        var p = player!;
        string msg = info.GetArg(1).Trim();
        string cleanMsg = msg.StartsWith("!") ? msg[1..] : msg;

        if (_nominatingPlayers.ContainsKey(p.Slot)) return HandleNominationInput(p, cleanMsg);

        if (cleanMsg.Equals("rtv", StringComparison.OrdinalIgnoreCase)) { AttemptRtv(p); return HookResult.Continue; }
        if (cleanMsg.Equals("nominate", StringComparison.OrdinalIgnoreCase)) { AttemptNominate(p); return HookResult.Continue; }
        if (cleanMsg.Equals("revote", StringComparison.OrdinalIgnoreCase)) { AttemptRevote(p); return HookResult.Continue; }
        if (cleanMsg.Equals("nextmap", StringComparison.OrdinalIgnoreCase)) { PrintNextMap(p); return HookResult.Continue; }
        if (_voteInProgress) return HandleVoteInput(p, cleanMsg);

        return HookResult.Continue;
    }

    // --- Logic ---
    private void AttemptRevote(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        if (!_voteInProgress) { player!.PrintToChat(" \x01There is no vote currently in progress."); return; }
        player!.PrintToChat(" \x01Redisplaying vote options. You may recast your vote.");
        PrintVoteOptionsToPlayer(player);
    }

    private void PrintNextMap(CCSPlayerController? player)
    {
        if (string.IsNullOrEmpty(_nextMapName)) { if (IsValidPlayer(player)) player!.PrintToChat(" \x01The next map has not been decided yet."); return; }
        Server.PrintToChatAll($" \x01The next map will be: \x04{_nextMapName}");
    }

    private void AttemptRtv(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (IsWarmup()) { p.PrintToChat(" \x01RTV is disabled during warmup."); return; }
        if (!Config.EnableRtv) { p.PrintToChat(" \x01RTV is currently disabled."); return; }
        if (_voteInProgress || _voteFinished) return;
        if (!_rtvVoters.Add(p.Slot)) { p.PrintToChat(" \x01You have already rocked the vote."); return; }

        int currentPlayers = GetHumanPlayers().Count();
        int votesNeeded = (int)Math.Ceiling(currentPlayers * Config.RtvPercentage);
        Server.PrintToChatAll($" \x01\x04{p.PlayerName}\x01 wants to change the map! ({_rtvVoters.Count}/{votesNeeded})");

        if (_rtvVoters.Count >= votesNeeded) { Server.PrintToChatAll(" \x01RTV Threshold reached! Starting vote..."); StartMapVote(isRtv: true); }
    }

    private void AttemptNominate(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!Config.EnableNominate) { p.PrintToChat(" \x01Nominations are currently disabled."); return; }
        if (_voteInProgress || _voteFinished) { p.PrintToChat(" \x01Voting has already finished."); return; }
        if (_nominatedMaps.Count >= Config.VoteOptionsCount) { p.PrintToChat(" \x01The nomination list is full!"); return; }
        if (_hasNominatedSteamIds.Contains(p.SteamID)) { p.PrintToChat(" \x01You have already nominated a map."); return; }

        var validMaps = _availableMaps.Where(m => !_nominatedMaps.Any(n => n.Id == m.Id)).Where(m => !Server.MapName.Contains(m.Name) && !Server.MapName.Contains(m.Id)).ToList();
        if (validMaps.Count == 0) { p.PrintToChat(" \x01No maps available to nominate."); return; }

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
        player.PrintToChat($" \x01Page {page + 1}/{totalPages}. Type number to select (or 'cancel'):");
        for (int i = startIndex; i < endIndex; i++) { int displayNum = (i - startIndex) + 1; player.PrintToChat($" \x04[{displayNum}] \x01{maps[i].Name}"); }
        if (totalPages > 1) player.PrintToChat(" \x04[0] \x01Next Page");
    }

    private HookResult HandleNominationInput(CCSPlayerController player, string input)
    {
        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase)) { CloseNominationMenu(player); player.PrintToChat(" \x01Nomination cancelled."); return HookResult.Handled; }
        if (input == "0") { _playerNominationPage[player.Slot]++; DisplayNominationMenu(player); return HookResult.Handled; }
        if (int.TryParse(input, out int selection))
        {
            var maps = _nominatingPlayers[player.Slot];
            int page = _playerNominationPage[player.Slot];
            int realIndex = (page * Config.NominatePerPage) + (selection - 1);
            if (realIndex >= 0 && realIndex < maps.Count && realIndex >= (page * Config.NominatePerPage) && realIndex < ((page + 1) * Config.NominatePerPage))
            {
                var selectedMap = maps[realIndex];
                if (_nominatedMaps.Count >= Config.VoteOptionsCount) player.PrintToChat(" \x01Nomination list is full.");
                else if (_nominatedMaps.Any(m => m.Id == selectedMap.Id)) player.PrintToChat(" \x01That map was just nominated by someone else.");
                else { _nominatedMaps.Add(selectedMap); _hasNominatedSteamIds.Add(player.SteamID); Server.PrintToChatAll($" \x01Player \x04{player.PlayerName}\x01 nominated \x04{selectedMap.Name}\x01."); }
                CloseNominationMenu(player);
                return HookResult.Handled;
            }
        }
        return HookResult.Continue;
    }
    private void CloseNominationMenu(CCSPlayerController player) { _nominatingPlayers.Remove(player.Slot); _playerNominationPage.Remove(player.Slot); }

    private void StartMapVote(bool isRtv)
    {
        _voteInProgress = true; _isScheduledVote = !isRtv; _nextMapName = null; _pendingMapId = null;
        _playerVotes.Clear(); _activeVoteOptions.Clear(); _nominatingPlayers.Clear(); _playerNominationPage.Clear();

        var mapsToVote = new List<MapItem>(_nominatedMaps);
        int slotsNeeded = Config.VoteOptionsCount - mapsToVote.Count;
        if (slotsNeeded > 0 && _availableMaps.Count > 0)
        {
            var potentialMaps = _availableMaps.Where(m => !mapsToVote.Any(n => n.Id == m.Id)).Where(m => !Server.MapName.Contains(m.Name) && !Server.MapName.Contains(m.Id));
            if (Config.EnableRecentMaps) { var filtered = potentialMaps.Where(m => !_recentMapIds.Contains(m.Id)).ToList(); if (filtered.Count > 0) potentialMaps = filtered; }
            mapsToVote.AddRange(potentialMaps.OrderBy(_ => new Random().Next()).Take(slotsNeeded));
        }

        for (int i = 0; i < mapsToVote.Count; i++) _activeVoteOptions[i + 1] = mapsToVote[i].Id;
        Server.PrintToChatAll(" \x01--- \x04Vote for the Next Map! \x01---");
        Server.PrintToChatAll(isRtv ? " \x01Vote ending in 30 seconds!" : " \x01Vote will remain open until the round ends.");
        PrintVoteOptionsToAll();

        if (Config.EnableReminders)
        {
            _reminderTimer = AddTimer(Config.ReminderIntervalSeconds, () => {
                foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot))) { p.PrintToChat(" \x01Reminder: Please vote for the next map!"); PrintVoteOptionsToPlayer(p); }
            }, TimerFlags.REPEAT);
        }
        if (isRtv) AddTimer(30.0f, () => EndVote(isRtv: true));
    }

    private HookResult HandleVoteInput(CCSPlayerController player, string input)
    {
        if (int.TryParse(input, out int option) && _activeVoteOptions.ContainsKey(option)) { _playerVotes[player.Slot] = option; player.PrintToChat($" \x01You voted for: \x04{GetMapName(_activeVoteOptions[option])}\x01"); return HookResult.Handled; }
        return HookResult.Continue;
    }

    private void EndVote(bool isRtv)
    {
        if (!_voteInProgress) return;
        _voteInProgress = false; _voteFinished = true; _reminderTimer?.Kill(); _reminderTimer = null;
        string winningMapId; int voteCount;

        if (_playerVotes.Count == 0)
        {
            if (_activeVoteOptions.Count == 0) return;
            var randomKey = _activeVoteOptions.Keys.ElementAt(new Random().Next(_activeVoteOptions.Count));
            winningMapId = _activeVoteOptions[randomKey]; _nextMapName = GetMapName(winningMapId); voteCount = 0;
            Server.PrintToChatAll(" \x01No votes cast! Randomly selecting a map...");
        }
        else
        {
            var winner = _playerVotes.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First();
            winningMapId = _activeVoteOptions[winner.Key]; _nextMapName = GetMapName(winningMapId); voteCount = winner.Count();
        }

        Server.PrintToChatAll(" \x01------------------------------");
        Server.PrintToChatAll($" \x01Winner: \x04{_nextMapName}\x01" + (voteCount > 0 ? $" with \x04{voteCount}\x01 votes!" : " (Random Pick)"));
        Server.PrintToChatAll(" \x01------------------------------");
        _nominatedMaps.Clear(); _hasNominatedSteamIds.Clear();

        if (isRtv) { Server.PrintToChatAll($" \x01 Changing map in {Config.RtvDelaySeconds} seconds..."); AddTimer(Config.RtvDelaySeconds, () => Server.ExecuteCommand($"host_workshop_map {winningMapId}")); }
        else { _pendingMapId = winningMapId; Server.PrintToChatAll(" \x01Map will change at the end of the match (Scoreboard)."); }
    }

    private void PrintVoteOptionsToAll() { foreach (var p in GetHumanPlayers()) PrintVoteOptionsToPlayer(p); }
    private void PrintVoteOptionsToPlayer(CCSPlayerController player) { player.PrintToChat(" \x01Type the \x04number\x01 to vote:"); foreach (var kvp in _activeVoteOptions) player.PrintToChat($" \x04[{kvp.Key}] \x01{GetMapName(kvp.Value)}"); }
    private string GetMapName(string mapId) => _availableMaps.FirstOrDefault(m => m.Id == mapId)?.Name ?? "Unknown";

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_voteFinished || _voteInProgress) return HookResult.Continue;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules != null && rules.TotalRoundsPlayed + 1 == Config.VoteRound) StartMapVote(isRtv: false);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info) { if (_voteInProgress && _isScheduledVote) EndVote(isRtv: false); return HookResult.Continue; }
    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        if (!string.IsNullOrEmpty(_pendingMapId)) { Server.PrintToChatAll($" \x01 Changing map to \x04{GetMapName(_pendingMapId)}\x01 in 8 seconds..."); AddTimer(8.0f, () => Server.ExecuteCommand($"host_workshop_map {_pendingMapId}")); }
        return HookResult.Continue;
    }
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info) { if (@event.Userid is { } player) { _rtvVoters.Remove(player.Slot); _playerVotes.Remove(player.Slot); CloseNominationMenu(player); } return HookResult.Continue; }
}
