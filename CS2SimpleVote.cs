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
    public override string ModuleVersion => "1.0.0";

    public VoteConfig Config { get; set; } = new();

    // Data Sources
    private List<MapItem> _availableMaps = new();
    private readonly HttpClient _httpClient = new();
    private CounterStrikeSharp.API.Modules.Timers.Timer? _reminderTimer;

    // State: Voting
    private bool _voteInProgress;
    private bool _voteFinished;
    private bool _isScheduledVote;
    private string? _nextMapName;
    private string? _pendingMapId; // Stores the winning ID for the end of the match
    private readonly HashSet<int> _rtvVoters = new();
    private readonly Dictionary<int, string> _activeVoteOptions = new(); // Option Number -> Map ID
    private readonly Dictionary<int, int> _playerVotes = new(); // Player Slot -> Option Number

    // State: Nomination
    private readonly List<MapItem> _nominatedMaps = new();
    private readonly HashSet<ulong> _hasNominatedSteamIds = new();
    private readonly Dictionary<int, List<MapItem>> _nominatingPlayers = new(); // Player Slot -> Maps Snapshot
    private readonly Dictionary<int, int> _playerNominationPage = new();

    public void OnConfigParsed(VoteConfig config)
    {
        Config = config;
        Config.VoteOptionsCount = Math.Clamp(Config.VoteOptionsCount, 2, 10);
        if (Config.NominatePerPage < 1) Config.NominatePerPage = 6;

        Task.Run(FetchCollectionMaps);
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd); // Hooks the final scoreboard
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);

        AddCommandListener("say", OnPlayerChat);
        AddCommandListener("say_team", OnPlayerChat);
    }

    private void OnMapStart(string mapName)
    {
        ResetState();
        // Disable the built-in Valve vote screen so it doesn't conflict
        Server.ExecuteCommand("mp_endmatch_votenextmap 0");
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
    }

    // --- Helpers ---

    private bool IsValidPlayer(CCSPlayerController? player)
    {
        return player != null && player.IsValid && !player.IsBot && !player.IsHLTV;
    }

    private bool IsWarmup()
    {
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        return rules?.WarmupPeriod ?? false;
    }

    private IEnumerable<CCSPlayerController> GetHumanPlayers()
    {
        return Utilities.GetPlayers().Where(IsValidPlayer);
    }

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
        string cleanMsg = msg.StartsWith("!") ? msg[1..] : msg; // Strip leading '!'

        // 1. Nomination Menu Input
        if (_nominatingPlayers.ContainsKey(p.Slot))
        {
            return HandleNominationInput(p, cleanMsg);
        }

        // 2. Commands
        if (cleanMsg.Equals("rtv", StringComparison.OrdinalIgnoreCase))
        {
            AttemptRtv(p);
            return HookResult.Continue;
        }
        if (cleanMsg.Equals("nominate", StringComparison.OrdinalIgnoreCase))
        {
            AttemptNominate(p);
            return HookResult.Continue;
        }
        if (cleanMsg.Equals("revote", StringComparison.OrdinalIgnoreCase))
        {
            AttemptRevote(p);
            return HookResult.Continue;
        }
        if (cleanMsg.Equals("nextmap", StringComparison.OrdinalIgnoreCase))
        {
            PrintNextMap(p);
            return HookResult.Continue;
        }

        // 3. Vote Input
        if (_voteInProgress)
        {
            return HandleVoteInput(p, cleanMsg);
        }

        return HookResult.Continue;
    }

    // --- Logic: Commands ---

    private void AttemptRevote(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!_voteInProgress)
        {
            p.PrintToChat(" \x01There is no vote currently in progress.");
            return;
        }

        p.PrintToChat(" \x01Redisplaying vote options. You may recast your vote.");
        PrintVoteOptionsToPlayer(p);
    }

    private void PrintNextMap(CCSPlayerController? player)
    {
        if (string.IsNullOrEmpty(_nextMapName))
        {
            if (IsValidPlayer(player)) player!.PrintToChat(" \x01The next map has not been decided yet.");
            return;
        }

        Server.PrintToChatAll($" \x01The next map will be: \x04{_nextMapName}");
    }

    // --- Logic: RTV ---

    private void AttemptRtv(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (IsWarmup())
        {
            p.PrintToChat(" \x01RTV is disabled during warmup.");
            return;
        }

        if (!Config.EnableRtv)
        {
            p.PrintToChat(" \x01RTV is currently disabled.");
            return;
        }

        if (_voteInProgress || _voteFinished) return;

        if (!_rtvVoters.Add(p.Slot))
        {
            p.PrintToChat(" \x01You have already rocked the vote.");
            return;
        }

        int currentPlayers = GetHumanPlayers().Count();
        int votesNeeded = (int)Math.Ceiling(currentPlayers * Config.RtvPercentage);

        Server.PrintToChatAll($" \x01\x04{p.PlayerName}\x01 wants to change the map! ({_rtvVoters.Count}/{votesNeeded})");

        if (_rtvVoters.Count >= votesNeeded)
        {
            Server.PrintToChatAll(" \x01RTV Threshold reached! Starting vote...");
            StartMapVote(isRtv: true);
        }
    }

    // --- Logic: Nomination ---

    private void AttemptNominate(CCSPlayerController? player)
    {
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!Config.EnableNominate)
        {
            p.PrintToChat(" \x01Nominations are currently disabled.");
            return;
        }

        if (_voteInProgress || _voteFinished)
        {
            p.PrintToChat(" \x01Voting has already finished.");
            return;
        }

        if (_nominatedMaps.Count >= Config.VoteOptionsCount)
        {
            p.PrintToChat(" \x01The nomination list is full!");
            return;
        }

        if (_hasNominatedSteamIds.Contains(p.SteamID))
        {
            p.PrintToChat(" \x01You have already nominated a map.");
            return;
        }

        var validMaps = _availableMaps
            .Where(m => !_nominatedMaps.Any(n => n.Id == m.Id))
            .Where(m => !Server.MapName.Contains(m.Name) && !Server.MapName.Contains(m.Id))
            .ToList();

        if (validMaps.Count == 0)
        {
            p.PrintToChat(" \x01No maps available to nominate.");
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

        player.PrintToChat($" \x01Page {page + 1}/{totalPages}. Type number to select (or 'cancel'):");

        for (int i = startIndex; i < endIndex; i++)
        {
            int displayNum = (i - startIndex) + 1;
            player.PrintToChat($" \x04[{displayNum}] \x01{maps[i].Name}");
        }

        if (totalPages > 1) player.PrintToChat(" \x04[0] \x01Next Page");
    }

    private HookResult HandleNominationInput(CCSPlayerController player, string input)
    {
        if (input.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            CloseNominationMenu(player);
            player.PrintToChat(" \x01Nomination cancelled.");
            return HookResult.Handled;
        }

        if (input == "0")
        {
            _playerNominationPage[player.Slot]++;
            DisplayNominationMenu(player);
            return HookResult.Handled;
        }

        if (int.TryParse(input, out int selection))
        {
            var maps = _nominatingPlayers[player.Slot];
            int page = _playerNominationPage[player.Slot];
            int realIndex = (page * Config.NominatePerPage) + (selection - 1);

            if (realIndex >= 0 && realIndex < maps.Count && realIndex >= (page * Config.NominatePerPage) && realIndex < ((page + 1) * Config.NominatePerPage))
            {
                var selectedMap = maps[realIndex];

                if (_nominatedMaps.Count >= Config.VoteOptionsCount)
                {
                    player.PrintToChat(" \x01Nomination list is full.");
                }
                else if (_nominatedMaps.Any(m => m.Id == selectedMap.Id))
                {
                    player.PrintToChat(" \x01That map was just nominated by someone else.");
                }
                else
                {
                    _nominatedMaps.Add(selectedMap);
                    _hasNominatedSteamIds.Add(player.SteamID);
                    Server.PrintToChatAll($" \x01Player \x04{player.PlayerName}\x01 nominated \x04{selectedMap.Name}\x01.");
                }

                CloseNominationMenu(player);
                return HookResult.Handled;
            }
        }
        return HookResult.Continue;
    }

    private void CloseNominationMenu(CCSPlayerController player)
    {
        _nominatingPlayers.Remove(player.Slot);
        _playerNominationPage.Remove(player.Slot);
    }

    // --- Logic: Voting ---

    private void StartMapVote(bool isRtv)
    {
        _voteInProgress = true;
        _isScheduledVote = !isRtv;
        _nextMapName = null;
        _pendingMapId = null;
        _playerVotes.Clear();
        _activeVoteOptions.Clear();
        _nominatingPlayers.Clear();
        _playerNominationPage.Clear();

        var mapsToVote = new List<MapItem>(_nominatedMaps);
        int slotsNeeded = Config.VoteOptionsCount - mapsToVote.Count;

        if (slotsNeeded > 0 && _availableMaps.Count > 0)
        {
            var random = new Random();
            var randomPool = _availableMaps
                .Where(m => !mapsToVote.Any(n => n.Id == m.Id))
                .Where(m => !Server.MapName.Contains(m.Name) && !Server.MapName.Contains(m.Id))
                .OrderBy(_ => random.Next())
                .Take(slotsNeeded);
            mapsToVote.AddRange(randomPool);
        }

        for (int i = 0; i < mapsToVote.Count; i++)
        {
            _activeVoteOptions[i + 1] = mapsToVote[i].Id;
        }

        Server.PrintToChatAll(" \x01--- \x04Vote for the Next Map! \x01---");
        Server.PrintToChatAll(isRtv ? " \x01Vote ending in 30 seconds!" : " \x01Vote will remain open until the round ends.");

        PrintVoteOptionsToAll();

        if (Config.EnableReminders)
        {
            _reminderTimer = AddTimer(Config.ReminderIntervalSeconds, () =>
            {
                foreach (var p in GetHumanPlayers().Where(p => !_playerVotes.ContainsKey(p.Slot)))
                {
                    p.PrintToChat(" \x01Reminder: Please vote for the next map!");
                    PrintVoteOptionsToPlayer(p);
                }
            }, TimerFlags.REPEAT);
        }

        if (isRtv) AddTimer(30.0f, () => EndVote(isRtv: true));
    }

    private HookResult HandleVoteInput(CCSPlayerController player, string input)
    {
        if (int.TryParse(input, out int option) && _activeVoteOptions.ContainsKey(option))
        {
            _playerVotes[player.Slot] = option;
            string mapName = GetMapName(_activeVoteOptions[option]);
            player.PrintToChat($" \x01You voted for: \x04{mapName}\x01");
            return HookResult.Handled;
        }
        return HookResult.Continue;
    }

    private void EndVote(bool isRtv)
    {
        if (!_voteInProgress) return;

        _voteInProgress = false;
        _voteFinished = true;
        _reminderTimer?.Kill();
        _reminderTimer = null;

        string winningMapId;
        int voteCount;

        if (_playerVotes.Count == 0)
        {
            var randomKey = _activeVoteOptions.Keys.ElementAt(new Random().Next(_activeVoteOptions.Count));
            winningMapId = _activeVoteOptions[randomKey];
            _nextMapName = GetMapName(winningMapId);
            voteCount = 0;
            Server.PrintToChatAll(" \x01No votes cast! Randomly selecting a map...");
        }
        else
        {
            var winner = _playerVotes.Values
                .GroupBy(v => v)
                .OrderByDescending(g => g.Count())
                .First();

            winningMapId = _activeVoteOptions[winner.Key];
            _nextMapName = GetMapName(winningMapId);
            voteCount = winner.Count();
        }

        Server.PrintToChatAll(" \x01------------------------------");
        Server.PrintToChatAll($" \x01Winner: \x04{_nextMapName}\x01" + (voteCount > 0 ? $" with \x04{voteCount}\x01 votes!" : " (Random Pick)"));
        Server.PrintToChatAll(" \x01------------------------------");

        _nominatedMaps.Clear();
        _hasNominatedSteamIds.Clear();

        if (isRtv)
        {
            Server.PrintToChatAll($" \x01Changing map in {Config.RtvDelaySeconds} seconds...");
            AddTimer(Config.RtvDelaySeconds, () => Server.ExecuteCommand($"host_workshop_map {winningMapId}"));
        }
        else
        {
            // Set this as the pending map for the end of the match
            _pendingMapId = winningMapId;
            Server.PrintToChatAll(" \x01Map will change at the end of the match (Scoreboard).");
        }
    }

    private void PrintVoteOptionsToAll()
    {
        foreach (var p in GetHumanPlayers()) PrintVoteOptionsToPlayer(p);
    }

    private void PrintVoteOptionsToPlayer(CCSPlayerController player)
    {
        player.PrintToChat(" \x01Type the \x04number\x01 to vote:");
        foreach (var kvp in _activeVoteOptions)
        {
            player.PrintToChat($" \x04[{kvp.Key}] \x01{GetMapName(kvp.Value)}");
        }
    }

    private string GetMapName(string mapId) => _availableMaps.FirstOrDefault(m => m.Id == mapId)?.Name ?? "Unknown";

    // --- Events ---

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (_voteFinished || _voteInProgress) return HookResult.Continue;

        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules != null && rules.TotalRoundsPlayed + 1 == Config.VoteRound)
        {
            StartMapVote(isRtv: false);
        }
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (_voteInProgress && _isScheduledVote) EndVote(isRtv: false);
        return HookResult.Continue;
    }

    private HookResult OnMatchEnd(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        // This triggers when the match actually ends (Scoreboard)
        if (!string.IsNullOrEmpty(_pendingMapId))
        {
            Server.PrintToChatAll($" \x01Changing map to \x04{GetMapName(_pendingMapId)}\x01 in 8 seconds...");
            // Slight delay to let players see the scoreboard, then force change
            AddTimer(8.0f, () => Server.ExecuteCommand($"host_workshop_map {_pendingMapId}"));
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
        }
        return HookResult.Continue;
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

            _availableMaps.Clear();
            foreach (var item in itemDoc.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
            {
                _availableMaps.Add(new MapItem
                {
                    Id = item.GetProperty("publishedfileid").GetString()!,
                    Name = item.GetProperty("title").GetString()!
                });
            }
            Console.WriteLine($"[CS2SimpleVote] Loaded {_availableMaps.Count} maps.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2SimpleVote] Error: {ex.Message}");
        }
    }
}
