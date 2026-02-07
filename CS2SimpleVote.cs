using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Reflection;
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
    [JsonPropertyName("vote_open_for_rounds")] public int VoteOpenForRounds { get; set; } = 1;
    [JsonPropertyName("admins")] public List<ulong> Admins { get; set; } = new();
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
    public override string ModuleVersion => "1.1.2";

    private const string ColorDefault = "\x01";
    private const string ColorGreen = "\x04";

    public VoteConfig Config { get; set; } = new();

    // Data Sources
    private List<MapItem> _availableMaps = new();
    private List<string> _recentMapIds = new();
    private readonly HttpClient _httpClient = new();
    private CounterStrikeSharp.API.Modules.Timers.Timer? _reminderTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _mapInfoTimer;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _centerMessageTimer;

    // State: Voting
    private bool _voteInProgress;
    private bool _voteFinished;
    private bool _isScheduledVote;
    private int _currentVoteRoundDuration;
    private bool _isForceVote;
    private string? _previousWinningMapId;
    private string? _previousWinningMapName;
    private bool _matchEnded;
    private int _forceVoteTimeRemaining;
    private string? _nextMapName;
    private string? _pendingMapId;
    private readonly HashSet<int> _rtvVoters = new();
    private readonly Dictionary<int, string> _activeVoteOptions = new();
    private readonly Dictionary<int, int> _playerVotes = new();

    // State: Nomination
    private readonly List<MapItem> _nominatedMaps = new();
    private readonly HashSet<ulong> _hasNominatedSteamIds = new();
    private readonly Dictionary<ulong, MapItem> _nominationOwner = new();
    private readonly Dictionary<ulong, string> _nominationNames = new();
    private readonly Dictionary<int, List<MapItem>> _nominatingPlayers = new();
    private readonly Dictionary<int, int> _playerNominationPage = new();

    // State: Forcemap
    private readonly Dictionary<int, List<MapItem>> _forcemapPlayers = new();
    private readonly Dictionary<int, int> _playerForcemapPage = new();

    // File Paths
    private string _historyFilePath = "";
    private string _cacheFilePath = "";

    // Cancellation for background task
    private CancellationTokenSource _cts = new();

    // Flag to prevent execution after unload
    private bool _unloaded = false;

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

        // 3. Start Background Update
        Task.Run(() => FetchCollectionMaps(_cts.Token));

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        AddCommandListener("say", OnPlayerChat, HookMode.Post);
        AddCommandListener("say_team", OnPlayerChat, HookMode.Post);
    }

    public override void Unload(bool hotReload)
    {
        _unloaded = true;

        // Kill all timers first to prevent any further execution
        _reminderTimer?.Kill();
        _reminderTimer = null;
        _mapInfoTimer?.Kill();
        _mapInfoTimer = null;
        _centerMessageTimer?.Kill();
        _centerMessageTimer = null;

        // Clear collections to release references
        _availableMaps.Clear();
        _recentMapIds.Clear();
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

        // Remove listeners and handlers
        DeregisterEventHandler<EventRoundStart>(OnRoundStart);
        DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
        DeregisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        RemoveListener<Listeners.OnMapStart>(OnMapStart);

        RemoveCommandListener("say", OnPlayerChat, HookMode.Post);
        RemoveCommandListener("say_team", OnPlayerChat, HookMode.Post);

        // Cancel background task
        _cts.Cancel();
        _cts.Dispose();

        // Dispose managed resources
        _httpClient.Dispose();
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
                if (_unloaded) return;
                // Find full title from available maps
                string displayMapName = _availableMaps.FirstOrDefault(m => mapName.Contains(m.Name) || m.Id == mapName || mapName.Contains(m.Id))?.Name ?? mapName;
                Server.PrintToChatAll($" {ColorDefault}You're playing {ColorGreen}{displayMapName}{ColorDefault} on {ColorGreen}{Config.ServerName}{ColorDefault}!");
            }, TimerFlags.REPEAT);
        }
    }

    private void ResetState()
    {
        _matchEnded = false;
        _voteInProgress = false;
        _voteFinished = false;
        _isScheduledVote = false;
        _isForceVote = false;
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

        _reminderTimer?.Kill();
        _reminderTimer = null;

        _mapInfoTimer?.Kill();
        _mapInfoTimer = null;

        _centerMessageTimer?.Kill();
        _centerMessageTimer = null;
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
        // Try to find the map by ID first (most reliable for workshop maps)
        var mapItem = _availableMaps.FirstOrDefault(m => currentMapName.Contains(m.Id, StringComparison.OrdinalIgnoreCase));

        // Fallback to name if not found by ID (for local maps or if ID isn't in path)
        if (mapItem == null)
        {
            mapItem = _availableMaps.FirstOrDefault(m => currentMapName.Contains(m.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (mapItem != null)
        {
            _recentMapIds.RemoveAll(id => id == mapItem.Id);
            _recentMapIds.Add(mapItem.Id);
            if (_recentMapIds.Count > Config.RecentMapsCount) _recentMapIds.RemoveAt(0);
            SaveMapHistory();
        }
    }

    // --- Steam API ---

    private async Task FetchCollectionMaps(CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(Config.SteamApiKey) || string.IsNullOrEmpty(Config.CollectionId)) return;

        try
        {
            var collContent = new FormUrlEncodedContent(new[] {
                new KeyValuePair<string, string>("collectioncount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", Config.CollectionId)
            });

            var collRes = await _httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/", collContent, token);
            using var collDoc = JsonDocument.Parse(await collRes.Content.ReadAsStringAsync(token));

            var children = collDoc.RootElement.GetProperty("response").GetProperty("collectiondetails")[0].GetProperty("children");
            var fileIds = children.EnumerateArray().Select(c => c.GetProperty("publishedfileid").GetString()!).ToList();

            var itemPairs = new List<KeyValuePair<string, string>> { new("itemcount", fileIds.Count.ToString()) };
            for (int i = 0; i < fileIds.Count; i++) itemPairs.Add(new($"publishedfileids[{i}]", fileIds[i]));

            var itemRes = await _httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", new FormUrlEncodedContent(itemPairs), token);
            using var itemDoc = JsonDocument.Parse(await itemRes.Content.ReadAsStringAsync(token));

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
        }
        catch (OperationCanceledException)
        {
            // Task was cancelled, ignore
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

    [ConsoleCommand("nominate", "Nominate a map (Usage: nominate [name])")]
    public void OnNominateCommand(CCSPlayerController? player, CommandInfo command)
    {
        string? searchTerm = command.ArgCount > 1 ? command.GetArg(1) : null;
        AttemptNominate(player, searchTerm);
    }

    [ConsoleCommand("nominatelist", "List nominated maps")]
    public void OnNominateListCommand(CCSPlayerController? player, CommandInfo command) => PrintNominationList(player);

    [ConsoleCommand("revote", "Recast vote")]
    public void OnRevoteCommand(CCSPlayerController? player, CommandInfo command) => AttemptRevote(player);

    [ConsoleCommand("nextmap", "Show next map")]
    public void OnNextMapCommand(CCSPlayerController? player, CommandInfo command) => PrintNextMap(player);

    [ConsoleCommand("forcemap", "Force change map (Admin only) (Usage: forcemap [name])")]
    public void OnForcemapCommand(CCSPlayerController? player, CommandInfo command)
    {
        string? searchTerm = command.ArgCount > 1 ? command.GetArg(1) : null;
        AttemptForcemap(player, searchTerm);
    }

    [ConsoleCommand("forcevote", "Force start map vote (Admin only)")]
    public void OnForceVoteCommand(CCSPlayerController? player, CommandInfo command) => AttemptForceVote(player);

    [ConsoleCommand("help", "List available commands")]
    public void OnHelpCommand(CCSPlayerController? player, CommandInfo command) => PrintHelp(player);

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        if (_unloaded) return HookResult.Continue;
        if (!IsValidPlayer(player)) return HookResult.Continue;
        var p = player!;
        string msg = info.GetArg(1).Trim();
        string cleanMsg = msg.StartsWith("!") ? msg[1..] : msg;

        // Parse command and potential arguments
        string[] inputs = cleanMsg.Split(' ', 2);
        string cmd = inputs[0];
        string? args = inputs.Length > 1 ? inputs[1].Trim() : null;

        if (_nominatingPlayers.ContainsKey(p.Slot)) return HandleNominationInput(p, cleanMsg);
        if (_forcemapPlayers.ContainsKey(p.Slot)) return HandleForcemapInput(p, cleanMsg);

        if (cmd.Equals("rtv", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptRtv(p)); return HookResult.Continue; }
        if (cmd.Equals("nominatelist", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintNominationList(p)); return HookResult.Continue; }
        if (cmd.Equals("help", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintHelp(p)); return HookResult.Continue; }
        if (cmd.Equals("forcevote", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptForceVote(p)); return HookResult.Continue; }
        if (cmd.Equals("revote", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptRevote(p)); return HookResult.Continue; }
        if (cmd.Equals("nextmap", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintNextMap(p)); return HookResult.Continue; }

        if (cmd.Equals("nominate", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptNominate(p, args));
            return HookResult.Continue;
        }

        if (cmd.Equals("forcemap", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptForcemap(p, args));
            return HookResult.Continue;
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

    private void PrintHelp(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        bool isAdmin = Config.Admins.Contains(p.SteamID);

        var commands = this.GetType().GetMethods()
            .Select(m => m.GetCustomAttribute<ConsoleCommandAttribute>())
            .Where(a => a != null)
            .ToList();

        p.PrintToChat($" {ColorDefault}---{ColorGreen} CS2SimpleVote Commands {ColorDefault}---");

        if (isAdmin)
        {
            var adminCmds = commands.Where(c => c!.Description.Contains("Admin", StringComparison.OrdinalIgnoreCase)).OrderBy(c => c!.Command);
            foreach (var cmd in adminCmds) p.PrintToChat($" {ColorGreen}!{cmd!.Command} {ColorDefault}- {cmd.Description}");
        }

        var playerCmds = commands.Where(c => !c!.Description.Contains("Admin", StringComparison.OrdinalIgnoreCase)).OrderBy(c => c!.Command);
        foreach (var cmd in playerCmds) p.PrintToChat($" {ColorGreen}!{cmd!.Command} {ColorDefault}- {cmd.Description}");
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
        if (string.IsNullOrEmpty(_nextMapName)) { if (IsValidPlayer(player)) player!.PrintToChat($" {ColorDefault}The next map has not been decided yet."); return; }
        Server.PrintToChatAll($" {ColorDefault}The next map will be: {ColorGreen}{_nextMapName}");
    }

    private void AttemptRtv(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (IsWarmup()) { p.PrintToChat($" {ColorDefault}RTV is disabled during warmup."); return; }
        if (!Config.EnableRtv) { p.PrintToChat($" {ColorDefault}RTV is currently disabled."); return; }
        if (_voteInProgress || _voteFinished) return;
        if (!_rtvVoters.Add(p.Slot)) { p.PrintToChat($" {ColorDefault}You have already rocked the vote."); return; }

        int currentPlayers = GetHumanPlayers().Count();
        int votesNeeded = (int)Math.Ceiling(currentPlayers * Config.RtvPercentage);
        Server.PrintToChatAll($" {ColorDefault}{ColorGreen}{p.PlayerName}{ColorDefault} wants to change the map! ({_rtvVoters.Count}/{votesNeeded})");

        if (_rtvVoters.Count >= votesNeeded) { Server.PrintToChatAll($" {ColorDefault}RTV Threshold reached! Starting vote..."); StartMapVote(isRtv: true); }
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
            .Where(m => !Server.MapName.Contains(m.Id, StringComparison.OrdinalIgnoreCase))
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
            Server.PrintToChatAll($" {ColorDefault}Player {ColorGreen}{player.PlayerName}{ColorDefault} changed their nomination to {ColorGreen}{map.Name}{ColorDefault}.");
        }
        else
        {
            _nominatedMaps.Add(map);
            _hasNominatedSteamIds.Add(player.SteamID);
            _nominationOwner[player.SteamID] = map;
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
            Server.PrintToChatAll($" {ColorDefault}Admin {ColorGreen}{p.PlayerName}{ColorDefault} forced map change to {ColorGreen}{map.Name}{ColorDefault}.");
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
                Server.PrintToChatAll($" {ColorDefault} Admin {ColorGreen}{player.PlayerName}{ColorDefault} forced map change to {ColorGreen}{selectedMap.Name}{ColorDefault}.");
                Server.ExecuteCommand($"host_workshop_map {selectedMap.Id}");
                CloseForcemapMenu(player);
                return HookResult.Handled;
            }
        }
        return HookResult.Continue;
    }
    private void CloseForcemapMenu(CCSPlayerController player) { _forcemapPlayers.Remove(player.Slot); _playerForcemapPage.Remove(player.Slot); }

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

        Server.PrintToChatAll($" {ColorDefault} Admin {ColorGreen}{p.PlayerName}{ColorDefault} initiated a map vote.");
        StartMapVote(isRtv: false, isForceVote: true);
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

        _nextMapName = null; 
        _pendingMapId = null;
        _currentVoteRoundDuration = 0;
        _playerVotes.Clear(); _activeVoteOptions.Clear(); _nominatingPlayers.Clear(); _playerNominationPage.Clear();

        var mapsToVote = new List<MapItem>(_nominatedMaps);
        int slotsNeeded = Config.VoteOptionsCount - mapsToVote.Count;
        if (slotsNeeded > 0 && _availableMaps.Count > 0)
        {
            var potentialMaps = _availableMaps
                .Where(m => !mapsToVote.Any(n => n.Id == m.Id))
                .Where(m => !Server.MapName.Contains(m.Id, StringComparison.OrdinalIgnoreCase));

            if (Config.EnableRecentMaps)
            {
                var filtered = potentialMaps.Where(m => !_recentMapIds.Contains(m.Id)).ToList();
                if (filtered.Count > 0) potentialMaps = filtered;
            }

            mapsToVote.AddRange(potentialMaps.OrderBy(_ => new Random().Next()).Take(slotsNeeded));
        }

        for (int i = 0; i < mapsToVote.Count; i++) _activeVoteOptions[i + 1] = mapsToVote[i].Id;
        Server.PrintToChatAll($" {ColorDefault}--- {ColorGreen}Vote for the Next Map! {ColorDefault}---");

        if (isRtv)
        {
            Server.PrintToChatAll($" {ColorDefault}Vote ending in 30 seconds!");
            AddTimer(30.0f, () => EndVote());
        }
        else if (isForceVote && _previousWinningMapId != null) // Scenario: Vote already happened
        {
             _forceVoteTimeRemaining = 30;
             // Chat message handled by center timer updates or initial print? 
             // Request says center message: "VOTE NOW! Time Remaining: 30s"
             // Typically we should also print to chat.
             Server.PrintToChatAll($" {ColorDefault}Vote ending in 30 seconds!");
             AddTimer(30.0f, () => EndVote());
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
                foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot))) { p.PrintToChat($" {ColorDefault}Reminder: Please vote for the next map!"); PrintVoteOptionsToPlayer(p); }
            }, TimerFlags.REPEAT);
        }

        _centerMessageTimer = AddTimer(1.0f, () => {
            if (_unloaded) return;
            if (_isForceVote && _previousWinningMapId != null)
            {
                _forceVoteTimeRemaining--;
                int displayTime = Math.Max(0, _forceVoteTimeRemaining);
                foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot))) 
                { 
                    p.PrintToCenter($"VOTE NOW! Time Remaining: {displayTime}s"); 
                }
            }
            else
            {
                foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot))) 
                { 
                    p.PrintToCenter("VOTE NOW!"); 
                }
            }
        }, TimerFlags.REPEAT);
    }

    private HookResult HandleVoteInput(CCSPlayerController player, string input)
    {
        if (int.TryParse(input, out int option) && _activeVoteOptions.ContainsKey(option)) { _playerVotes[player.Slot] = option; player.PrintToChat($" {ColorDefault}You voted for: {ColorGreen}{GetMapName(_activeVoteOptions[option])}{ColorDefault}"); return HookResult.Handled; }
        return HookResult.Continue;
    }

    private void EndVote()
    {
        if (!_voteInProgress) return;
        _voteInProgress = false; _voteFinished = true; _reminderTimer?.Kill(); _reminderTimer = null;
        _centerMessageTimer?.Kill(); _centerMessageTimer = null;
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
            var randomKey = _activeVoteOptions.Keys.ElementAt(new Random().Next(_activeVoteOptions.Count));
            winningMapId = _activeVoteOptions[randomKey]; _nextMapName = GetMapName(winningMapId); voteCount = 0;
            Server.PrintToChatAll($" {ColorDefault}No votes cast! Randomly selecting a map...");
        }
        else
        {
            var winner = _playerVotes.Values.GroupBy(v => v).OrderByDescending(g => g.Count()).First();
            winningMapId = _activeVoteOptions[winner.Key]; _nextMapName = GetMapName(winningMapId); voteCount = winner.Count();
        }
        
        // Clear flags
        _isForceVote = false;
        _previousWinningMapId = null;
        _previousWinningMapName = null;

        Server.PrintToChatAll($" {ColorDefault}------------------------------");
        Server.PrintToChatAll($" {ColorDefault}Winner: {ColorGreen}{_nextMapName}{ColorDefault}" + (voteCount > 0 ? $" with {ColorGreen}{voteCount}{ColorDefault} votes!" : " (Random/Previous)"));
        Server.PrintToChatAll($" {ColorDefault}------------------------------");
        _nominatedMaps.Clear(); _hasNominatedSteamIds.Clear(); _nominationOwner.Clear(); _nominationNames.Clear();

        // If it was an RTV, or a ForceVote that happened AFTER normal vote (implied by this not being a scheduled vote), change immediately/soon
        // Logic: 
        // 1. RTV -> Change ID
        // 2. ForceVote -> If handled like normal vote (no prev winner) -> End of match
        // 3. ForceVote -> If handled like special vote (prev winner existed) -> Schedule for next map (End of match), but apply ID now?
        // Wait, "apply the map as the next map (on scoreboard)" implies pending ID.

        // Refined Logic based on request:
        // "If no previous map vote has happened, treat this as a normal map vote... do not bring the normal map vote up"
        // In that case, we want pending ID for end of match, NOT immediate change.

        // So only RTV triggers immediate change. 
        // Wait, what if ForceVote was triggered "like RTV" (e.g. immediate change desired)? 
        // The request doesn't explicitly say ForceVote changes map immediately, it says "apply the map as the next map (on scoreboard)".
        // So behavior is consistent: Pending ID for end of match.

        // RTV is the only one that forces immediate change logic in original code?
        // Original: if (isRtv) ... ExecuteCommand ... else ... Map will change at end of match.
        
        // Since StartMapVote(isRtv: false, isForceVote: true) is called:
        // isRtv is false. 
        // So we fall to else block.
        
        _pendingMapId = winningMapId; 
        Server.PrintToChatAll($" {ColorDefault}Map will change at the end of the match."); 
    }

    private void PrintVoteOptionsToAll() { foreach (var p in GetHumanPlayers()) PrintVoteOptionsToPlayer(p); }
    private void PrintVoteOptionsToPlayer(CCSPlayerController player) { player.PrintToChat($" {ColorDefault}Type the {ColorGreen}number{ColorDefault} to vote:"); foreach (var kvp in _activeVoteOptions) player.PrintToChat($" {ColorGreen}[{kvp.Key}] {ColorDefault}{GetMapName(kvp.Value)}"); }
    private string GetMapName(string mapId) => _availableMaps.FirstOrDefault(m => m.Id == mapId)?.Name ?? "Unknown";

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_voteFinished || _voteInProgress) return HookResult.Continue;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules != null && rules.TotalRoundsPlayed + 1 == Config.VoteRound) StartMapVote(isRtv: false);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
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
            }
        }
        return HookResult.Continue;
    }
    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        _matchEnded = true;

        if (_voteInProgress)
        {
            EndVote();
        }

        if (!string.IsNullOrEmpty(_pendingMapId)) { Server.PrintToChatAll($" {ColorDefault} Changing map to {ColorGreen}{GetMapName(_pendingMapId)}{ColorDefault}!"); AddTimer(8.0f, () => Server.ExecuteCommand($"host_workshop_map {_pendingMapId}")); }
        return HookResult.Continue;
    }
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid is { } player) { _rtvVoters.Remove(player.Slot); _playerVotes.Remove(player.Slot); CloseNominationMenu(player); } return HookResult.Continue; }
}
