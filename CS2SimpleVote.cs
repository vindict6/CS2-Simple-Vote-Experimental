using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
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
    [JsonPropertyName("show_midvote_progress")] public bool ShowMidVoteProgress { get; set; } = true;
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

    // --- Floating Text Dependencies ---
    private readonly Dictionary<int, int> _playerActiveCount = new();
    private readonly Dictionary<int, int> _playerCurrentIndex = new();
    private readonly Dictionary<int, List<CPointWorldText>> _playerPersistentHUDs = new();
    private readonly Dictionary<int, List<CPointWorldText>> _allPlayerTexts = new();
    private readonly Dictionary<uint, float> _hudUpDist = new();
    private readonly Dictionary<uint, float> _hudRightDist = new();
    private readonly Dictionary<int, CPointOrient> _playerPointOrients = new();
    
    private Dictionary<char, System.Drawing.Color> ChatColors = new Dictionary<char, System.Drawing.Color>
    {
        { '\x01', System.Drawing.Color.White },
        { '\x02', System.Drawing.Color.DarkRed },
        { '\x03', System.Drawing.Color.MediumPurple },
        { '\x04', System.Drawing.Color.LimeGreen },
        { '\x05', System.Drawing.Color.LightGreen },
        { '\x06', System.Drawing.Color.Lime },
        { '\x07', System.Drawing.Color.Red },
        { '\x08', System.Drawing.Color.Gray },
        { '\x09', System.Drawing.Color.Yellow },
        { '\x0A', System.Drawing.Color.Orange },
        { '\x0B', System.Drawing.Color.LightSkyBlue },
        { '\x0C', System.Drawing.Color.DodgerBlue },
        { '\x0D', System.Drawing.Color.Blue },
        { '\x0E', System.Drawing.Color.Purple },
        { '\x0F', System.Drawing.Color.LightCoral },
        { '\x10', System.Drawing.Color.Goldenrod }
    };

    

    private void SlideOutAndRemoveHUD(CPointWorldText wp, CCSPlayerController player, float initialDelay = 0.0f)
    {
        if (wp == null || !wp.IsValid || player == null || !player.IsValid) return;

        Action performSlideOut = () =>
        {
            if (wp == null || !wp.IsValid || player == null || !player.IsValid) return;

            int steps = 15;
            float duration = 0.5f;
            float startOffset = _hudRightDist.GetValueOrDefault(wp.Index, -25.0f);
            float endOffset = startOffset - 185.0f; // Slide rightwards offscreen
            float upDist = _hudUpDist.GetValueOrDefault(wp.Index, 6.0f);

            for (int i = 1; i <= steps; i++)
            {
                float progress = (float)i / steps;
                float easedProgress = progress * progress; // Ease in

                AddTimer(duration * progress, () => 
                {
                    if (wp == null || !wp.IsValid || player == null || !player.IsValid || player.PlayerPawn.Value == null) return;
                    
                    float currentOffset = startOffset + (endOffset - startOffset) * easedProgress;

                    // Update position
                    var pawn = player.PlayerPawn.Value;
                    var eyeAngles = pawn.EyeAngles;
                    float pitch = (float)(eyeAngles.X * Math.PI / 180.0f);
                    float yaw = (float)(eyeAngles.Y * Math.PI / 180.0f);
                    float fwdX = (float)(Math.Cos(pitch) * Math.Cos(yaw));
                    float fwdY = (float)(Math.Cos(pitch) * Math.Sin(yaw));
                    float fwdZ = (float)(-Math.Sin(pitch));
                    float rightX = (float)(-Math.Sin(yaw));
                    float rightY = (float)(Math.Cos(yaw));
                    float rightZ = 0.0f;
                    float upX = fwdY * rightZ - fwdZ * rightY;
                    float upY = fwdZ * rightX - fwdX * rightZ;
                    float upZ = fwdX * rightY - fwdY * rightX;

                    float fwdDist = 110.0f;

                    Vector origin = new Vector(
                        pawn.AbsOrigin.X + pawn.ViewOffset.X + fwdX * fwdDist + rightX * currentOffset + upX * upDist,
                        pawn.AbsOrigin.Y + pawn.ViewOffset.Y + fwdY * fwdDist + rightY * currentOffset + upY * upDist,
                        pawn.AbsOrigin.Z + pawn.ViewOffset.Z + fwdZ * fwdDist + rightZ * currentOffset + upZ * upDist
                    );

                    QAngle angle = new QAngle(0, eyeAngles.Y + 270.0f, 90.0f - eyeAngles.X);
                    wp.Teleport(origin, angle, new Vector(0,0,0));
                });
            }

            AddTimer(duration + 0.1f, () => 
            {
                if (wp != null && wp.IsValid) wp.Remove();
                if (_playerActiveCount.ContainsKey(player.Slot))
                {
                    _playerActiveCount[player.Slot] = System.Math.Max(0, _playerActiveCount[player.Slot] - 1);
                }
            });
        };

        if (initialDelay > 0.0f)
        {
            AddTimer(initialDelay, performSlideOut);
        }
        else
        {
            performSlideOut();
        }
    }

    private void SlideInHUD(CPointWorldText wp, CCSPlayerController player)
    {
        if (wp == null || !wp.IsValid || player == null || !player.IsValid) return;

        int steps = 15;
        float duration = 0.5f;
        float endOffset = _hudRightDist.GetValueOrDefault(wp.Index, -25.0f);
        float startOffset = endOffset - 185.0f;
        float upDist = _hudUpDist.GetValueOrDefault(wp.Index, 6.0f);

        for (int i = 1; i <= steps; i++)
        {
            float progress = (float)i / steps;
            float easedProgress = 1.0f - (float)Math.Pow(1.0f - progress, 3); // Ease out cubic

            AddTimer(duration * progress, () => 
            {
                if (wp == null || !wp.IsValid || player == null || !player.IsValid || player.PlayerPawn.Value == null) return;
                
                float currentOffset = startOffset + (endOffset - startOffset) * easedProgress;

                var pawn = player.PlayerPawn.Value;
                var eyeAngles = pawn.EyeAngles;
                float pitch = (float)(eyeAngles.X * Math.PI / 180.0f);
                float yaw = (float)(eyeAngles.Y * Math.PI / 180.0f);
                float fwdX = (float)(Math.Cos(pitch) * Math.Cos(yaw));
                float fwdY = (float)(Math.Cos(pitch) * Math.Sin(yaw));
                float fwdZ = (float)(-Math.Sin(pitch));
                float rightX = (float)(-Math.Sin(yaw));
                float rightY = (float)(Math.Cos(yaw));
                float rightZ = 0.0f;
                float upX = fwdY * rightZ - fwdZ * rightY;
                float upY = fwdZ * rightX - fwdX * rightZ;
                float upZ = fwdX * rightY - fwdY * rightX;

                float fwdDist = 110.0f;

                Vector origin = new Vector(
                    pawn.AbsOrigin.X + pawn.ViewOffset.X + fwdX * fwdDist + rightX * currentOffset + upX * upDist,
                    pawn.AbsOrigin.Y + pawn.ViewOffset.Y + fwdY * fwdDist + rightY * currentOffset + upY * upDist,
                    pawn.AbsOrigin.Z + pawn.ViewOffset.Z + fwdZ * fwdDist + rightZ * currentOffset + upZ * upDist
                );

                QAngle angle = new QAngle(0, eyeAngles.Y + 270.0f, 90.0f - eyeAngles.X);
                wp.Teleport(origin, angle, new Vector(0,0,0));
            });
        }
    }

    private void CreateFloatingHUDMessages(CCSPlayerController player, string message, bool isPersistent = false, int lineIndex = 0)
    {
        message = message.Trim();
        message = System.Text.RegularExpressions.Regex.Replace(message, @"[\-\]", "");

        if (player.PlayerPawn.Value == null || player.PlayerPawn.Value.AbsOrigin == null) return;
        
        var pawn = player.PlayerPawn.Value;

        if (!_playerPointOrients.ContainsKey(player.Slot) || _playerPointOrients[player.Slot] == null || !_playerPointOrients[player.Slot].IsValid)
        {
            var orient = Utilities.CreateEntityByName<CPointOrient>("point_orient");
            if (orient != null && orient.IsValid)
            {
                orient.Active = true;
                orient.GoalDirection = PointOrientGoalDirectionType_t.eEyesForward;
                orient.DispatchSpawn();
                
                Vector orientPos = new Vector(pawn.AbsOrigin.X, pawn.AbsOrigin.Y, pawn.AbsOrigin.Z + pawn.ViewOffset.Z);
                
                orient.AcceptInput("SetParent", pawn, null, "!activator");
                orient.AcceptInput("SetTarget", pawn, null, "!activator");
                orient.Teleport(orientPos, pawn.EyeAngles, null);
                
                _playerPointOrients[player.Slot] = orient;
            }
        }
        
        var eyeAngles = pawn.EyeAngles;
        float pitch = (float)(eyeAngles.X * Math.PI / 180.0f);
        float yaw = (float)(eyeAngles.Y * Math.PI / 180.0f);
        float fwdX = (float)(Math.Cos(pitch) * Math.Cos(yaw));
        float fwdY = (float)(Math.Cos(pitch) * Math.Sin(yaw));
        float fwdZ = (float)(-Math.Sin(pitch));
        float rightX = (float)(-Math.Sin(yaw));
        float rightY = (float)(Math.Cos(yaw));
        float rightZ = 0.0f;
        float upX = fwdY * rightZ - fwdZ * rightY;
        float upY = fwdZ * rightX - fwdX * rightZ;
        float upZ = fwdX * rightY - fwdY * rightX;

        // Clear existing messages to prevent overlap and simulate UI replacing itself
        if (lineIndex == 0)
        {
            ClearPlayerHUDMessages(player);
        }

        // Reverted to original distance and sizes for crisp font resolution
        float fwdDist = 110.0f;
        float rightDistOffset = -25.0f; // Negative value to place it on the right side of the screen
        float baseUpDist = 6.0f;
        float lineSpacing = 4.0f;
        float upDist = baseUpDist - (lineIndex * lineSpacing);

        var wp = Utilities.CreateEntityByName<CPointWorldText>("point_worldtext");
        if (wp == null) return;
        
        _hudUpDist[wp.Index] = upDist;
        _hudRightDist[wp.Index] = rightDistOffset;

        wp.Enabled = true;
        wp.MessageText = "";
        wp.FontSize = 12;
        wp.FontName = "Stratum2";
        wp.Fullbright = true;
        wp.WorldUnitsPerPx = 0.25f;
        wp.Color = System.Drawing.Color.FromArgb(255, 128, 64);
        wp.JustifyHorizontal = PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_LEFT;
        wp.JustifyVertical = PointWorldTextJustifyVertical_t.POINT_WORLD_TEXT_JUSTIFY_VERTICAL_TOP;
        wp.ReorientMode = PointWorldTextReorientMode_t.POINT_WORLD_TEXT_REORIENT_NONE;
        wp.DrawBackground = true;
        wp.BackgroundBorderWidth = 5.0f;
        wp.BackgroundBorderHeight = 2.0f;

        Vector origin = new Vector(
            player.PlayerPawn.Value.AbsOrigin.X + player.PlayerPawn.Value.ViewOffset.X + fwdX * fwdDist + rightX * rightDistOffset + upX * upDist,
            player.PlayerPawn.Value.AbsOrigin.Y + player.PlayerPawn.Value.ViewOffset.Y + fwdY * fwdDist + rightY * rightDistOffset + upY * upDist,
            player.PlayerPawn.Value.AbsOrigin.Z + player.PlayerPawn.Value.ViewOffset.Z + fwdZ * fwdDist + rightZ * rightDistOffset + upZ * upDist
        );

        QAngle angle = new QAngle(0, eyeAngles.Y + 270.0f, 90.0f - eyeAngles.X);
        wp.DispatchSpawn();

        if (_playerPointOrients.ContainsKey(player.Slot) && _playerPointOrients[player.Slot] != null && _playerPointOrients[player.Slot].IsValid) { wp.AcceptInput("SetParent", _playerPointOrients[player.Slot], null, "!activator"); } else { wp.AcceptInput("SetParent", player.PlayerPawn.Value, null, "!activator"); }
        wp.Teleport(origin, angle, new Vector(0,0,0));
        wp.AcceptInput("SetMessage", wp, wp, message);
        if (!_allPlayerTexts.ContainsKey(player.Slot)) _allPlayerTexts[player.Slot] = new();
        _allPlayerTexts[player.Slot].Add(wp);

        // Make it slide in from the right immediately after spawning
        SlideInHUD(wp, player);

        if (isPersistent)
        {
            if (!_playerPersistentHUDs.ContainsKey(player.Slot)) _playerPersistentHUDs[player.Slot] = new();
            _playerPersistentHUDs[player.Slot].Add(wp);
        }
        else
        {
            SlideOutAndRemoveHUD(wp, player, 5.0f);
        }
    }

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

    // State: SetNextMap
    private readonly Dictionary<int, List<MapItem>> _setnextmapPlayers = new();
    private readonly Dictionary<int, int> _playerSetNextMapPage = new();

    // Logger
    private readonly BlockingCollection<string> _logQueue = new();
    private Task? _logTask;
    private string _logFilePath = "";

    // File Paths
    private string _historyFilePath = "";
    private string _cacheFilePath = "";

    // Cancellation for background task
    private CancellationTokenSource _cts = new();

    // Flag to prevent execution after unload
    private bool _unloaded = false;
    private bool _hasLoadedCollectionMaps = false;
    private bool _isApiLoading = false;

    public void OnConfigParsed(VoteConfig config)
    {
        Config = config;
        Config.VoteOptionsCount = Math.Clamp(Config.VoteOptionsCount, 2, 10);
        if (Config.NominatePerPage < 1) Config.NominatePerPage = 6;
    }

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.CheckTransmit>(OnTransmit);
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
        _logFilePath = Path.Combine(configDir, "CS2SimpleVote_debug.log");
        StartLogWriter();
        LogRoutine(new { hotReload }, null);

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
        AddCommandListener("pause", OnPauseCommand, HookMode.Pre);
        AddCommandListener("setpause", OnPauseCommand, HookMode.Pre);
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.CheckTransmit>(OnTransmit);
        LogRoutine(new { hotReload }, null);
        _unloaded = true;
        _cts.Cancel();
        _logQueue.CompleteAdding();
        _logTask?.Wait(500); // Allow some time for final logs

        // Kill all timers first to prevent any further execution
        _reminderTimer?.Kill();
        _reminderTimer = null;
        _mapInfoTimer?.Kill();
        _mapInfoTimer = null;
        _centerMessageTimer?.Kill();
        _centerMessageTimer = null;

        // Cleanup HUDs - remove entities from the world
        foreach (var kvp in _allPlayerTexts)
        {
            foreach (var wp in kvp.Value)
            {
                if (wp != null && wp.IsValid) wp.Remove();
            }
            kvp.Value.Clear();
        }
        _allPlayerTexts.Clear();
        _playerPersistentHUDs.Clear();

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
        _setnextmapPlayers.Clear();
        _playerSetNextMapPage.Clear();

        // Remove listeners and handlers
        DeregisterEventHandler<EventRoundStart>(OnRoundStart);
        DeregisterEventHandler<EventRoundEnd>(OnRoundEnd);
        DeregisterEventHandler<EventCsWinPanelMatch>(OnMatchEnd);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        RemoveListener<Listeners.OnMapStart>(OnMapStart);

        RemoveCommandListener("say", OnPlayerChat, HookMode.Post);
        RemoveCommandListener("say_team", OnPlayerChat, HookMode.Post);
        RemoveCommandListener("pause", OnPauseCommand, HookMode.Pre);
        RemoveCommandListener("setpause", OnPauseCommand, HookMode.Pre);

        // Cancel background task
        _cts.Cancel();
        _cts.Dispose();

        // Dispose managed resources
        _httpClient.Dispose();
    }

    private void OnMapStart(string mapName)
    {
        LogRoutine(new { mapName }, null);
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
        LogRoutine(new { }, null);
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

        _playerPersistentHUDs.Clear();
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
        LogRoutine(new { currentMapName }, null);
        // Try to find the map by ID first (most reliable for workshop maps)
        var mapItem = _availableMaps.FirstOrDefault(m => !string.IsNullOrEmpty(m.Id) && currentMapName.Contains(m.Id, StringComparison.OrdinalIgnoreCase));

        // Fallback to name if not found by ID (for local maps or if ID isn't in path)
        if (mapItem == null)
        {
            string cleanName = currentMapName.Split('/').Last();
            mapItem = _availableMaps.FirstOrDefault(m => !string.IsNullOrEmpty(m.Name) && (cleanName.Equals(m.Name, StringComparison.OrdinalIgnoreCase) || m.Name.Contains(cleanName, StringComparison.OrdinalIgnoreCase) || cleanName.Contains(m.Name, StringComparison.OrdinalIgnoreCase) && m.Name.Length >= 4));
        }

        string idToAdd = mapItem?.Id ?? currentMapName;

        _recentMapIds.RemoveAll(id => id == idToAdd);
        _recentMapIds.Add(idToAdd);
        if (_recentMapIds.Count > Config.RecentMapsCount) _recentMapIds.RemoveAt(0);
        SaveMapHistory();
    }

    // --- Steam API ---

    private async Task FetchCollectionMaps(CancellationToken token = default)
    {
        LogRoutine(new { token }, null);
        if (string.IsNullOrEmpty(Config.SteamApiKey) || string.IsNullOrEmpty(Config.CollectionId)) return;

        try
        {
            _isApiLoading = true;
            var collContent = new FormUrlEncodedContent(new[] {
                new KeyValuePair<string, string>("key", Config.SteamApiKey),
                new KeyValuePair<string, string>("collectioncount", "1"),
                new KeyValuePair<string, string>("publishedfileids[0]", Config.CollectionId)
            });

            var collRes = await _httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/", collContent, token);
            string collJson = await collRes.Content.ReadAsStringAsync(token);
            using var collDoc = JsonDocument.Parse(collJson);

            var rootResp = collDoc.RootElement.GetProperty("response");
            if (!rootResp.TryGetProperty("collectiondetails", out var collDetails) || collDetails.GetArrayLength() == 0)
            {
                throw new Exception("Invalid response or missing collection details from Steam API. Check Collection ID.");
            }

            var firstColl = collDetails[0];
            if (!firstColl.TryGetProperty("children", out var children))
            {
                int resultObj = firstColl.TryGetProperty("result", out var resToken) ? resToken.GetInt32() : -1;
                throw new Exception($"Collection has no children or is inaccessible. Steam result code: {resultObj}. Check if the Steam API Key is valid and collection is public.");
            }

            var fileIds = children.EnumerateArray()
                .Select(c => c.GetProperty("publishedfileid").GetString())
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>()
                .ToList();

            if (fileIds.Count == 0) throw new Exception("No files found in collection.");

            var itemPairs = new List<KeyValuePair<string, string>> { 
                new("key", Config.SteamApiKey),
                new("itemcount", fileIds.Count.ToString()) 
            };
            for (int i = 0; i < fileIds.Count; i++) itemPairs.Add(new($"publishedfileids[{i}]", fileIds[i]));

            var itemRes = await _httpClient.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", new FormUrlEncodedContent(itemPairs), token);
            string itemJson = await itemRes.Content.ReadAsStringAsync(token);
            using var itemDoc = JsonDocument.Parse(itemJson);

            var newMapList = new List<MapItem>();
            if (itemDoc.RootElement.TryGetProperty("response", out var itemResp) && itemResp.TryGetProperty("publishedfiledetails", out var pubDetails))
            {
                foreach (var item in pubDetails.EnumerateArray())
                {
                    if (item.TryGetProperty("title", out var titleProp) && item.TryGetProperty("publishedfileid", out var idProp))
                    {
                        string? mapName = titleProp.GetString();
                        string? mapId = idProp.GetString();
                        if (!string.IsNullOrEmpty(mapName) && !string.IsNullOrEmpty(mapId))
                        {
                            newMapList.Add(new MapItem { Id = mapId, Name = mapName });
                        }
                    }
                }
            }

            _availableMaps = newMapList;
            _hasLoadedCollectionMaps = true;
            _isApiLoading = false;
            Console.WriteLine($"[CS2SimpleVote] Updated {_availableMaps.Count} maps from Steam.");
            SaveMapCache();
        }
                catch (OperationCanceledException)
        {
            _isApiLoading = false;
        }
        catch (ObjectDisposedException)
        {
            _isApiLoading = false;
            // Plugin unloaded while fetching
        }
        catch (Exception ex)
        {
            _isApiLoading = false;
            Console.WriteLine($"[CS2SimpleVote] Error API: {ex.Message}");
        }
    }

    // --- Helpers ---
    private bool IsValidPlayer(CCSPlayerController? player) => player != null && player.IsValid && !player.IsBot && !player.IsHLTV;
    private bool IsWarmup() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.WarmupPeriod ?? false;
    private bool IsCurrentMap(MapItem map) => Server.MapName.Contains(map.Id, StringComparison.OrdinalIgnoreCase) || Server.MapName.Equals(map.Name, StringComparison.OrdinalIgnoreCase);
    private IEnumerable<CCSPlayerController> GetHumanPlayers() => Utilities.GetPlayers().Where(IsValidPlayer);

    // --- Command Handlers ---
    [ConsoleCommand("rtv", "Rock the Vote")]
    public void OnRtvCommand(CCSPlayerController? player, CommandInfo command) => AttemptRtv(player);

    [ConsoleCommand("nominate", "Nominate a map (Usage: nominate [name])")]
    public void OnNominateCommand(CCSPlayerController? player, CommandInfo command)
    {
        LogRoutine(new { player, command }, null);
        string? searchTerm = command.ArgCount > 1 ? command.GetArg(1) : null;
        AttemptNominate(player, searchTerm);
    }

    [ConsoleCommand("nominatelist", "List nominated maps")]
    public void OnNominateListCommand(CCSPlayerController? player, CommandInfo command) => PrintNominationList(player);

    [ConsoleCommand("revote", "Recast vote")]
    public void OnRevoteCommand(CCSPlayerController? player, CommandInfo command) => AttemptRevote(player);

    [ConsoleCommand("nextmap", "Show next map")]
    public void OnNextMapCommand(CCSPlayerController? player, CommandInfo command) => PrintNextMap(player);

    [ConsoleCommand("lastmap", "Show last played map")]
    public void OnLastMapCommand(CCSPlayerController? player, CommandInfo command) => PrintLastMap(player);

    [ConsoleCommand("recentmaps", "Show recently played maps")]
    public void OnRecentMapsCommand(CCSPlayerController? player, CommandInfo command)
    {
        LogRoutine(new { player, command }, null);
        string? arg = command.ArgCount > 1 ? command.GetArg(1) : null;
        PrintRecentMaps(player, arg);
    }

    [ConsoleCommand("forcemap", "Force change map (Admin only) (Usage: forcemap [name])")]
    public void OnForcemapCommand(CCSPlayerController? player, CommandInfo command)
    {
        LogRoutine(new { player, command }, null);
        string? searchTerm = command.ArgCount > 1 ? command.GetArg(1) : null;
        AttemptForcemap(player, searchTerm);
    }

    [ConsoleCommand("setnextmap", "Set the next map directly (Admin only) (Usage: setnextmap [name])")]
    public void OnSetNextMapCommand(CCSPlayerController? player, CommandInfo command)
    {
        LogRoutine(new { player, command }, null);
        string? searchTerm = command.ArgCount > 1 ? command.GetArg(1) : null;
        AttemptSetNextMap(player, searchTerm);
    }

    [ConsoleCommand("forcevote", "Force start map vote (Admin only)")]
    public void OnForceVoteCommand(CCSPlayerController? player, CommandInfo command) => AttemptForceVote(player);
    [ConsoleCommand("endwarmup", "End the warmup round (Admin only)")]
    public void OnEndWarmupCommand(CCSPlayerController? player, CommandInfo command) => AttemptEndWarmup(player);

    [ConsoleCommand("help", "List available commands")]
    public void OnHelpCommand(CCSPlayerController? player, CommandInfo command) => PrintHelp(player);

    [ConsoleCommand("votedebug", "Show debug info (Admin only)")]
    public void OnVoteDebugCommand(CCSPlayerController? player, CommandInfo command) => AttemptVoteDebug(player);

    private HookResult OnPauseCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || !player.IsValid) return HookResult.Continue;
        
        // Report the user to admins
        string msg = $" {ColorRed}[Admin Alert] {ColorDefault}Player {ColorGreen}{player.PlayerName} {ColorDefault}({player.SteamID}) pressed {ColorRed}Pause Break{ColorDefault}!";
        LogRoutine(new { player.PlayerName, player.SteamID }, "Player pressed pause/setpause");

        foreach (var p in Utilities.GetPlayers())
        {
            if (IsValidPlayer(p) && AdminManager.PlayerHasPermissions(p, "@css/generic"))
            {
                p.PrintToChat(msg);
            }
        }
        
        return HookResult.Continue; // Let the engine handle it, but we alerted admins. (Change to Handled if you want to block it)
    }

    private HookResult OnPlayerChat(CCSPlayerController? player, CommandInfo info)
    {
        LogRoutine(new { player, info }, null);
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
        if (_setnextmapPlayers.ContainsKey(p.Slot)) return HandleSetNextMapInput(p, cleanMsg);

        if (cmd.Equals("rtv", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptRtv(p)); return HookResult.Continue; }
        if (cmd.Equals("nominatelist", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintNominationList(p)); return HookResult.Continue; }
        if (cmd.Equals("help", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintHelp(p)); return HookResult.Continue; }
        if (cmd.Equals("forcevote", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptForceVote(p)); return HookResult.Continue; }
        if (cmd.Equals("endwarmup", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptEndWarmup(p)); return HookResult.Continue; }        if (cmd.Equals("votedebug", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptVoteDebug(p)); return HookResult.Continue; }
        if (cmd.Equals("revote", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => AttemptRevote(p)); return HookResult.Continue; }
        if (cmd.Equals("nextmap", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintNextMap(p)); return HookResult.Continue; }
        if (cmd.Equals("lastmap", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintLastMap(p)); return HookResult.Continue; }
        if (cmd.Equals("recentmaps", StringComparison.OrdinalIgnoreCase)) { Server.NextFrame(() => PrintRecentMaps(p, args)); return HookResult.Continue; }

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

        if (cmd.Equals("setnextmap", StringComparison.OrdinalIgnoreCase))
        {
            Server.NextFrame(() => AttemptSetNextMap(p, args));
            return HookResult.Continue;
        }

        if (_voteInProgress) return HandleVoteInput(p, cleanMsg);

        return HookResult.Continue;
    }

    // --- Logic ---
    private void AttemptRevote(CCSPlayerController? player)
    {
        LogRoutine(new { player }, null);
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
        string lastMapDisplay = _recentMapIds.Count > 1 ? GetMapName(_recentMapIds[_recentMapIds.Count - 2]) : "None";
        
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
                RecentMapIds = _recentMapIds.ToList()
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
        LogRoutine(new { player }, null);
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
        LogRoutine(new { player }, null);
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
        LogRoutine(new { player }, null);
        if (string.IsNullOrEmpty(_nextMapName)) { if (IsValidPlayer(player)) player!.PrintToChat($" {ColorDefault}The next map has not been decided yet."); return; }
        Server.PrintToChatAll($" {ColorDefault}The next map will be: {ColorGreen}{_nextMapName}");
    }

    private void PrintLastMap(CCSPlayerController? player)
    {
        LogRoutine(new { player }, null);
        if (_recentMapIds.Count > 1) 
        {
            // The current map is usually pushed to the end of _recentMapIds upon OnMapStart.
            // Meaning, the "last" map before the current one is at count - 2.
            string lastMapId = _recentMapIds[_recentMapIds.Count - 2];
            string lastMapName = GetMapName(lastMapId);
            Server.PrintToChatAll($" {ColorDefault}The last played map was: {ColorGreen}{lastMapName}");
        }
        else 
        {
            if (IsValidPlayer(player)) player!.PrintToChat($" {ColorDefault}No previous map data found.");
        }
    }

    private void PrintRecentMaps(CCSPlayerController? player, string? arg = null)
    {
        LogRoutine(new { player, arg }, null);
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (_recentMapIds.Count == 0 || (_recentMapIds.Count == 1 && IsCurrentMap(new MapItem { Id = _recentMapIds[0] })))
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
        
        var recentNames = _recentMapIds
            .Select(id => GetMapName(id))
            .Reverse()
            .ToList();

        // Print up to recent configurations limit. Skipping index 0 if it's the current map.
        int displayCount = 1;
        for(int i = 0; i < recentNames.Count; i++)
        {
            if (displayCount > maxDisplayCount) break;

            // Usually index 0 in the reversed list is the current active map because it gets appended to the end of the history.
            // Let's filter out current map to show only purely *past* maps
            if (IsCurrentMap(new MapItem { Id = _recentMapIds[_recentMapIds.Count - 1 - i], Name = recentNames[i] })) continue;

            p.PrintToChat($" {ColorGreen}{displayCount}. {ColorDefault}{recentNames[i]}");
            displayCount++;
        }
    }

    private void AttemptRtv(CCSPlayerController? player)
    {
        LogRoutine(new { player }, null);
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
        LogRoutine(new { player, searchTerm }, null);
        if (!IsValidPlayer(player)) return;
        var p = player!;
        if (!Config.EnableNominate) { p.PrintToChat($" {ColorDefault}Nominations are currently disabled."); return; }
        if (_voteInProgress || _voteFinished) { p.PrintToChat($" {ColorDefault}Voting has already finished."); return; }
        
        bool isRenomination = _hasNominatedSteamIds.Contains(p.SteamID);
        if (!isRenomination && _nominatedMaps.Count >= Config.VoteOptionsCount) { p.PrintToChat($" {ColorDefault}The nomination list is full!"); return; }

        var validMaps = _availableMaps
            .Where(m => !_nominatedMaps.Any(n => n.Id == m.Id))
            .Where(m => !IsCurrentMap(m))
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
        
        int line = 0;
        CreateFloatingHUDMessages(player, $" {ColorDefault}Page {page + 1}/{totalPages}. Type number to select (or 'cancel'):", true, line++);
        for (int i = startIndex; i < endIndex; i++) { 
            int displayNum = (i - startIndex) + 1; 
            CreateFloatingHUDMessages(player, $" {ColorGreen}[{displayNum}] {ColorDefault}{maps[i].Name}", true, line++); 
        }
        if (totalPages > 1) CreateFloatingHUDMessages(player, $" {ColorGreen}[0] {ColorDefault}Next Page", true, line++);
    }

    private HookResult HandleNominationInput(CCSPlayerController player, string input)
    {
        LogRoutine(new { player, input }, null);
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
        LogRoutine(new { player, map }, null);
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

    private void ClearPlayerHUDMessages(CCSPlayerController player)
    {
        if (_allPlayerTexts.TryGetValue(player.Slot, out var existingTexts))
        {
            foreach (var existingWp in existingTexts.ToList())
            {
                if (existingWp != null && existingWp.IsValid)
                {
                    SlideOutAndRemoveHUD(existingWp, player);
                }
            }
            existingTexts.Clear();
        }

        if (_playerPersistentHUDs.TryGetValue(player.Slot, out var persistentTexts))
        {
            persistentTexts.Clear();
        }
    }

    private void CloseNominationMenu(CCSPlayerController player) { 
        _nominatingPlayers.Remove(player.Slot); 
        _playerNominationPage.Remove(player.Slot); 
        ClearPlayerHUDMessages(player);
    }

    // --- Forcemap Logic ---
    private void AttemptForcemap(CCSPlayerController? player, string? searchTerm = null)
    {
        LogRoutine(new { player, searchTerm }, null);
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
        
        int line = 0;
        CreateFloatingHUDMessages(player, $" {ColorDefault}[Forcemap] Page {page + 1}/{totalPages}. Type number to select (or 'cancel'):", true, line++);
        for (int i = startIndex; i < endIndex; i++) { 
            int displayNum = (i - startIndex) + 1; 
            CreateFloatingHUDMessages(player, $" {ColorGreen}[{displayNum}] {ColorDefault}{maps[i].Name}", true, line++); 
        }
        if (totalPages > 1) CreateFloatingHUDMessages(player, $" {ColorGreen}[0] {ColorDefault}Next Page", true, line++);
    }

    private HookResult HandleForcemapInput(CCSPlayerController player, string input)
    {
        LogRoutine(new { player, input }, null);
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
    private void CloseForcemapMenu(CCSPlayerController player) { 
        _forcemapPlayers.Remove(player.Slot); 
        _playerForcemapPage.Remove(player.Slot); 
        ClearPlayerHUDMessages(player);
    }

    // --- SetNextMap Logic ---
    private void AttemptSetNextMap(CCSPlayerController? player, string? searchTerm = null)
    {
        LogRoutine(new { player, searchTerm }, null);
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
        
        int line = 0;
        CreateFloatingHUDMessages(player, $" {ColorDefault}[SetNextMap] Page {page + 1}/{totalPages}. Type number to select (or 'cancel'):", true, line++);
        for (int i = startIndex; i < endIndex; i++) { 
            int displayNum = (i - startIndex) + 1; 
            CreateFloatingHUDMessages(player, $" {ColorGreen}[{displayNum}] {ColorDefault}{maps[i].Name}", true, line++); 
        }
        if (totalPages > 1) CreateFloatingHUDMessages(player, $" {ColorGreen}[0] {ColorDefault}Next Page", true, line++);
    }

    private HookResult HandleSetNextMapInput(CCSPlayerController player, string input)
    {
        LogRoutine(new { player, input }, null);
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
        LogRoutine(new { player, selectedMap }, null);
        _pendingMapId = selectedMap.Id;
        _nextMapName = selectedMap.Name;
        
        string rawMsg = $"{player.PlayerName} has set the next map to {selectedMap.Name}.";
        string dashes = new string('-', rawMsg.Length);

        Server.PrintToChatAll($" {ColorDefault}{dashes}");
        Server.PrintToChatAll($" {ColorGreen}{player.PlayerName} {ColorDefault}has set the next map to {ColorGreen}{selectedMap.Name}{ColorDefault}.");
        Server.PrintToChatAll($" {ColorDefault}{dashes}");
    }

    private void CloseSetNextMapMenu(CCSPlayerController player) { 
        _setnextmapPlayers.Remove(player.Slot); 
        _playerSetNextMapPage.Remove(player.Slot); 
        ClearPlayerHUDMessages(player);
    }

    // --- EndWarmup Logic ---
    private void AttemptEndWarmup(CCSPlayerController? player)
    {
        LogRoutine(new { player }, null);
        if (!IsValidPlayer(player)) return;
        var p = player!;

        if (!Config.Admins.Contains(p.SteamID))
        {
            p.PrintToChat($" {ColorDefault} You do not have permission to use this command.");
            return;
        }

        if (!IsWarmup())
        {
            p.PrintToChat($" {ColorDefault} The game is not in warmup.");
            return;
        }

        Server.ExecuteCommand("mp_warmup_end");
        Server.PrintToChatAll($" {ColorDefault} Admin {ColorGreen}{p.PlayerName}{ColorDefault} ended the warmup.");
    }

    // --- ForceVote Logic ---
    private void AttemptForceVote(CCSPlayerController? player)
    {
        LogRoutine(new { player }, null);
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
        LogRoutine(new { isRtv, isForceVote }, null);
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
                .Where(m => !IsCurrentMap(m));

            if (Config.EnableRecentMaps)
            {
                var filtered = potentialMaps.Where(m => !_recentMapIds.Contains(m.Id)).ToList();
                // To properly omit them, we will use the filtered maps, even if empty.
                // We shouldn't fallback to recent maps unless absolutely necessary to fill slots? 
                // The intent was "make sure the omission list is implemented and actually works", 
                // so we aggressively restrict it by omitting them regardless of count:
                potentialMaps = filtered;
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
        LogRoutine(new { player, input }, null);
        if (int.TryParse(input, out int option) && _activeVoteOptions.ContainsKey(option)) 
        { 
            _playerVotes[player.Slot] = option; 
            ClearPlayerHUDMessages(player);

            player.PrintToChat($" {ColorDefault}You voted for: {ColorGreen}{GetMapName(_activeVoteOptions[option])}{ColorDefault}"); 
            return HookResult.Handled; 
        }
        return HookResult.Continue;
    }

    private void EndVote()
    {
        LogRoutine(new { }, null);
        if (!_voteInProgress) return;
        _voteInProgress = false; _voteFinished = true; _reminderTimer?.Kill(); _reminderTimer = null;
        _centerMessageTimer?.Kill(); _centerMessageTimer = null;

        foreach (var kvp in _allPlayerTexts)
        {
            var p = Utilities.GetPlayerFromSlot(kvp.Key);
            if (p != null && IsValidPlayer(p)) 
            {
                foreach (var wp in kvp.Value.ToList()) if (wp != null && wp.IsValid) SlideOutAndRemoveHUD(wp, p);
            }
            else
            {
                foreach (var wp in kvp.Value.ToList()) if (wp != null && wp.IsValid) wp.Remove();
            }
            kvp.Value.Clear();
        }
        _playerPersistentHUDs.Clear();
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

        string rawMsg = $"Winner: {_nextMapName}" + (voteCount > 0 ? $" with {voteCount} votes!" : " (Random/Previous)");
        string dashes = new string('-', rawMsg.Length);

        Server.PrintToChatAll($" {ColorDefault}{dashes}");
        Server.PrintToChatAll($" {ColorDefault}Winner: {ColorGreen}{_nextMapName}{ColorDefault}" + (voteCount > 0 ? $" with {ColorGreen}{voteCount}{ColorDefault} votes!" : " (Random/Previous)"));
        Server.PrintToChatAll($" {ColorDefault}{dashes}");
        
        if (voteCount > 0 && Config.ShowMidVoteProgress)
        {
            PrintVoteProgress();
        }

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
    private void PrintVoteOptionsToPlayer(CCSPlayerController player) { 
        int line = 0;
        CreateFloatingHUDMessages(player, $" {ColorDefault}Type the {ColorGreen}number{ColorDefault} to vote:", true, line++);
        foreach (var kvp in _activeVoteOptions) 
        {
            CreateFloatingHUDMessages(player, $" {ColorGreen}[{kvp.Key}] {ColorDefault}{GetMapName(kvp.Value)}", true, line++);
        }
    }
    private string GetMapName(string mapId) => _availableMaps.FirstOrDefault(m => m.Id == mapId)?.Name ?? "Unknown";

    private void PrintVoteProgress()
    {
        LogRoutine(new { }, null);
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
        LogRoutine(new { @event, info }, null);
        if (_voteFinished || _voteInProgress) return HookResult.Continue;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules != null && rules.TotalRoundsPlayed + 1 == Config.VoteRound) StartMapVote(isRtv: false);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        LogRoutine(new { @event, info }, null);
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
        LogRoutine(new { @event, info }, null);
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
        LogRoutine(new { player = @event.Userid?.PlayerName }, null);
        if (@event.Userid is { } player) { _rtvVoters.Remove(player.Slot); _playerVotes.Remove(player.Slot); CloseNominationMenu(player); CloseForcemapMenu(player); CloseSetNextMapMenu(player); } return HookResult.Continue; }

    // --- Logging Infrastructure ---
    private void StartLogWriter()
    {
        var token = _cts.Token;
        _logTask = Task.Run(() => 
        {
            try 
            {
                using var fs = new FileStream(_logFilePath, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Write);
                using var writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
                foreach (var logContent in _logQueue.GetConsumingEnumerable())
                {
                    try { writer.WriteLine(logContent); }
                    catch (Exception ex) { Console.WriteLine($"[CS2SimpleVote] Log write error: {ex.Message}"); }
                }
            } 
            catch (OperationCanceledException) { }
            catch (Exception ex) 
            {
                Console.WriteLine($"[CS2SimpleVote] Logger failed: {ex.Message}");
            }
        }, token);
    }

    
    private void OnTransmit(CCheckTransmitInfoList infoList)
    {
        foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
        {
            if (player == null || !player.IsValid || !player.PlayerPawn.IsValid || player.PlayerPawn.Value == null) continue;

            foreach (var kvp in _allPlayerTexts)
            {
                kvp.Value.RemoveAll(w => w == null || !w.IsValid);

                if (player.Slot != kvp.Key)
                {
                    foreach (var wp in kvp.Value)
                    {
                        if (wp != null && wp.IsValid)
                        {
                            info.TransmitEntities.Remove((int)wp.Index);
                        }
                    }
                }
            }
        }
    }

    private void LogRoutine(object? inputs = null, object? outputs = null, [System.Runtime.CompilerServices.CallerMemberName] string routine = "")
    {
        if (_unloaded) return;
        
        string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"[{timestamp}] -> ROUTINE: {routine}");
        sb.AppendLine("================================================================================");
        
        if (inputs != null)
        {
            sb.AppendLine("  INPUTS  : ");
            try 
            { 
                string json = JsonSerializer.Serialize(inputs, new JsonSerializerOptions { WriteIndented = true });
                foreach(var line in json.Split('\n')) sb.AppendLine("    " + line.TrimEnd('\r'));
            }
            catch { sb.AppendLine("    " + inputs.ToString()); }
        }
        else
        {
            sb.AppendLine("  INPUTS  : None");
        }
        
        if (outputs != null)
        {
            sb.AppendLine("  OUTPUTS : ");
            try 
            { 
                string json = JsonSerializer.Serialize(outputs, new JsonSerializerOptions { WriteIndented = true });
                foreach(var line in json.Split('\n')) sb.AppendLine("    " + line.TrimEnd('\r'));
            }
            catch { sb.AppendLine("    " + outputs.ToString()); }
        }
        else
        {
            sb.AppendLine("  OUTPUTS : None");
        }
        sb.AppendLine("--------------------------------------------------------------------------------");
        
        try { _logQueue.Add(sb.ToString()); } catch { /* Queue might be closed */ }
    }
}





