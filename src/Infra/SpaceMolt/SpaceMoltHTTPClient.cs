using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class SpaceMoltHttpClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly SpaceMoltApiTransport _transport;
    private readonly SpaceMoltSessionService _sessionService;
    private readonly SpaceMoltGameStateAssembler _gameStateAssembler;
    private readonly SpaceMoltCacheRepository _cacheRepository;
    private readonly SpaceMoltCatalogService _catalogService;
    private readonly SpaceMoltMapService _mapService;
    private readonly SpaceMoltNotificationTracker _notificationTracker;

    private string? _sessionId;
    private readonly Dictionary<string, StationInfo> _stationCache = new(StringComparer.Ordinal);
    private int _currentTick;
    private int _shipCatalogPage = 1;
    private long _requestSequence;
    private GameState? _latestGameState;
    private readonly SemaphoreSlim _stateRefreshLock = new(1, 1);
    private bool _isRefreshingLatestState;

    private static readonly TimeSpan MarketCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ShipyardCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan CatalogueCacheTtl = TimeSpan.FromHours(24);

    private const int ShipCatalogPageSizeConst = 12;
    private const int CatalogFetchPageSize = 50;
    private const int MaxQueuedNotifications = 100;
    private const int MaxChatMessages = 40;

    private const string BaseUrl = "https://game.spacemolt.com/api/v1/";

    private static readonly HashSet<string> MutationCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept_mission",
        "attack",
        "battle",
        "buy",
        "buy_insurance",
        "buy_listed_ship",
        "buy_ship",
        "cancel_commission",
        "cancel_order",
        "cancel_ship_listing",
        "claim_commission",
        "cloak",
        "commission_ship",
        "complete_mission",
        "craft",
        "create_buy_order",
        "create_faction",
        "create_sell_order",
        "deposit_credits",
        "deposit_items",
        "dock",
        "faction_accept_peace",
        "faction_cancel_mission",
        "faction_create_buy_order",
        "faction_create_sell_order",
        "faction_declare_war",
        "faction_deposit_credits",
        "faction_deposit_items",
        "faction_invite",
        "faction_kick",
        "faction_post_mission",
        "faction_promote",
        "faction_propose_peace",
        "faction_set_ally",
        "faction_set_enemy",
        "faction_submit_intel",
        "faction_submit_trade_intel",
        "faction_withdraw_credits",
        "faction_withdraw_items",
        "install_mod",
        "jettison",
        "join_faction",
        "jump",
        "leave_faction",
        "list_ship_for_sale",
        "loot_wreck",
        "mine",
        "modify_order",
        "refuel",
        "release_tow",
        "reload",
        "repair",
        "salvage_wreck",
        "scan",
        "scrap_wreck",
        "self_destruct",
        "sell",
        "sell_ship",
        "sell_wreck",
        "send_gift",
        "set_home_base",
        "supply_commission",
        "survey_system",
        "switch_ship",
        "tow_wreck",
        "trade_accept",
        "trade_offer",
        "travel",
        "undock",
        "uninstall_mod",
        "use_item",
        "withdraw_credits",
        "withdraw_items"
    };

    public bool DebugEnabled { get; set; } = true;
    public string DebugContext { get; set; } = "";

    public SpaceMoltHttpClient()
    {
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(30);

        _transport = new SpaceMoltApiTransport(_http, BaseUrl);
        _sessionService = new SpaceMoltSessionService(_http, BaseUrl);
        _cacheRepository = new SpaceMoltCacheRepository();
        _notificationTracker = new SpaceMoltNotificationTracker(MaxQueuedNotifications, MaxChatMessages);
        _catalogService = new SpaceMoltCatalogService(
            executeAsync: ExecuteAsync,
            cacheRepository: _cacheRepository,
            catalogueCacheTtl: CatalogueCacheTtl,
            catalogFetchPageSize: CatalogFetchPageSize);
        _mapService = new SpaceMoltMapService(
            AppPaths.GalaxyMapFile,
            ExecuteAsync);
        _gameStateAssembler = new SpaceMoltGameStateAssembler(this);

        _cacheRepository.LoadMarketCachesFromDisk(_stationCache, MarketCacheTtl);
        _cacheRepository.LoadShipyardCachesFromDisk(_stationCache, ShipyardCacheTtl);
        _catalogService.LoadCachesFromDisk();
        PromoteCachedGalaxyState();
    }

    internal Dictionary<string, StationInfo> StationCache => _stationCache;
    internal TimeSpan CatalogueCacheTtlValue => CatalogueCacheTtl;
    internal int ShipCatalogPageSize => ShipCatalogPageSizeConst;

    private void PromoteCachedGalaxyState()
    {
        GalaxyStateHub.MergeMarkets(_stationCache.Values.Select(s => s.Market));
        _catalogService.PromoteCachedCatalogState();
        _mapService.PromoteCachedMapFromDisk();
    }

    public async Task CreateSessionAsync()
    {
        _sessionId = await _sessionService.CreateSessionAsync();
    }

    public async Task LoginAsync(string username, string password)
    {
        EnsureSession();
        await _sessionService.LoginAsync(_sessionId!, username, password);
        await RefreshLatestStateFromApiAsync();
    }

    public async Task<string> RegisterAsync(string username, string empire, string registrationCode)
    {
        EnsureSession();
        string password = await _sessionService.RegisterAsync(_sessionId!, username, empire, registrationCode);
        await RefreshLatestStateFromApiAsync();
        return password;
    }

    public async Task<JsonElement> ExecuteAsync(string command, object? payload = null)
    {
        EnsureSession();

        long requestId = Interlocked.Increment(ref _requestSequence);
        JsonElement result = await _transport.ExecuteCommandAsync(
            _sessionId!,
            command,
            payload,
            DebugEnabled,
            DebugContext,
            requestId,
            content => _notificationTracker.ObservePayload(content, ref _currentTick));

        await RefreshLatestStateAfterCommandAsync(command);
        return result;
    }

    private async Task RefreshLatestStateAfterCommandAsync(string command)
    {
        if (_isRefreshingLatestState)
            return;

        if (!MutationCommands.Contains(command))
            return;

        if (string.Equals(command, "get_status", StringComparison.OrdinalIgnoreCase))
            return;

        await RefreshLatestStateFromApiAsync();
    }

    private async Task RefreshLatestStateFromApiAsync()
    {
        await _stateRefreshLock.WaitAsync();
        try
        {
            if (_isRefreshingLatestState)
                return;

            _isRefreshingLatestState = true;

            var status = await ExecuteAsync("get_status");
            SpaceMoltApiTransport.EnsureCommandSucceeded("get_status", status);
            if (!_notificationTracker.ObserveTickFromPayload(status, ref _currentTick))
                _currentTick = Math.Max(1, _currentTick + 1);

            await _catalogService.EnsureFreshCataloguesAsync();
            _latestGameState = await BuildGameStateFromStatusAsync(status);
        }
        finally
        {
            _isRefreshingLatestState = false;
            _stateRefreshLock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, CatalogueEntry>> GetFullItemCatalogByIdAsync(
        bool forceRefresh = false)
    {
        return await _catalogService.GetFullItemCatalogByIdAsync(forceRefresh);
    }

    public async Task<IReadOnlyDictionary<string, CatalogueEntry>> GetFullShipCatalogByIdAsync(
        bool forceRefresh = false)
    {
        return await _catalogService.GetFullShipCatalogByIdAsync(forceRefresh);
    }

    public async Task<JsonElement> FindRouteAsync(string targetSystem)
    {
        JsonElement routeResult = await ExecuteAsync(
            "find_route",
            new { target_system = targetSystem });

        await SpaceMoltHttpLogging.LogPathfindAsync(targetSystem, routeResult);
        return routeResult;
    }

    public async Task<Catalogue> GetCatalogueAsync(
        string type,
        string? category = null,
        string? id = null,
        int? page = null,
        int? pageSize = null,
        string? search = null)
    {
        return await _catalogService.GetCatalogueAsync(type, category, id, page, pageSize, search);
    }

    public async Task<GalaxyMapSnapshot> GetMapSnapshotAsync(bool forceRefresh = false)
    {
        return await _mapService.GetMapSnapshotAsync(forceRefresh);
    }

    public GameState GetGameState()
    {
        if (_latestGameState == null)
            throw new InvalidOperationException("Game state cache is empty.");

        return _latestGameState;
    }

    private async Task<GameState> BuildGameStateFromStatusAsync(JsonElement status)
    {
        return await _gameStateAssembler.BuildAsync(status);
    }

    public int ShipCatalogPage => _shipCatalogPage;

    public void ResetShipCatalogPage()
    {
        _shipCatalogPage = 1;
    }

    public bool MoveShipCatalogToNextPage(int? totalPages)
    {
        if (totalPages.HasValue && totalPages.Value > 0 && _shipCatalogPage >= totalPages.Value)
            return false;

        _shipCatalogPage++;
        return true;
    }

    public bool MoveShipCatalogToLastPage()
    {
        if (_shipCatalogPage <= 1)
            return false;

        _shipCatalogPage--;
        return true;
    }

    public void Dispose()
    {
        _http.Dispose();
    }

    internal void SaveMarketCacheToDisk(string stationId, MarketState market)
    {
        _cacheRepository.SaveMarketCacheToDisk(stationId, market);
    }

    internal void SaveShipyardCacheToDisk(
        string stationId,
        string[] showroomLines,
        string[] listingLines)
    {
        _cacheRepository.SaveShipyardCacheToDisk(stationId, showroomLines, listingLines);
    }

    internal bool TryGetCachedCatalogue(string fileKey, out SpaceMoltCatalogueCacheEntry entry)
    {
        return _catalogService.TryGetCachedCatalogue(fileKey, out entry);
    }

    internal void SetShipCatalogPage(int page)
    {
        _shipCatalogPage = Math.Max(1, page);
    }

    internal EconomyDeal[] BuildBestDealsForCurrentStation(string currentStationId, int maxDeals)
    {
        return SpaceMoltMarketAnalytics.BuildBestDealsForCurrentStation(_stationCache, currentStationId, maxDeals);
    }

    internal GameNotification[] DrainPendingNotifications(int maxCount)
    {
        return _notificationTracker.DrainPendingNotifications(maxCount);
    }

    internal GameChatMessage[] SnapshotChatMessages(int maxCount)
    {
        return _notificationTracker.SnapshotChatMessages(maxCount);
    }

    private void EnsureSession()
    {
        if (_sessionId == null)
            throw new InvalidOperationException("Session not created.");
    }
}
