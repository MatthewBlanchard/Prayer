using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public class SpaceMoltHttpClient : IDisposable, IRuntimeTransport
{
    private readonly HttpClient _http;
    private readonly SpaceMoltApiTransport _transport;
    private readonly SpaceMoltSessionService _sessionService;
    private readonly SpaceMoltGameStateAssembler _gameStateAssembler;
    private readonly SpaceMoltCacheRepository _cacheRepository;
    private readonly SpaceMoltCatalogService _catalogService;
    private readonly SpaceMoltMapService _mapService;
    private readonly SpaceMoltNotificationTracker _notificationTracker;
    private readonly SpaceMoltSessionCache _sessionCache;
    private readonly ConcurrentDictionary<string, ApiCommandPerfCounter> _apiCommandPerf =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _sessionId;
    private DateTimeOffset? _sessionExpiresAt;
    private string? _username;
    private string? _password;
    private readonly Dictionary<string, StationInfo> _stationCache = new(StringComparer.Ordinal);
    private int _currentTick;
    private int _shipCatalogPage = 1;
    private long _requestSequence;
    private GameState? _latestGameState;
    private readonly SemaphoreSlim _stateRefreshLock = new(1, 1);
    private readonly SemaphoreSlim _sessionRecoveryLock = new(1, 1);
    private readonly SemaphoreSlim _apiStatsLogLock = new(1, 1);
    private bool _isRefreshingLatestState;
    private readonly DateTime _apiStatsStartedUtc = DateTime.UtcNow;
    private DateTime _lastApiStatsLogUtc = DateTime.UtcNow;
    private long _totalApiCalls;
    private long _totalGetCalls;
    private long _apiCallsSinceLastStatsLog;

    private static readonly TimeSpan MarketCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan ShipyardCacheTtl = TimeSpan.FromHours(24);
    private static readonly TimeSpan CatalogueCacheTtl = TimeSpan.FromHours(24);

    private const int ShipCatalogPageSizeConst = 12;
    private const int CatalogFetchPageSize = 50;
    private const int MaxQueuedNotifications = 100;
    private const int MaxChatMessages = 40;
    private const int ApiStatsLogEveryNCalls = 50;
    private const int ApiStatsTopCommandLimit = 12;
    private static readonly TimeSpan ApiStatsLogInterval = TimeSpan.FromMinutes(1);

    private const string BaseUrl = "https://game.spacemolt.com/api/v1/";

    private static readonly HashSet<string> MutationCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "accept_mission",
        "abandon_mission",
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
        _sessionCache = new SpaceMoltSessionCache();
        _catalogService = new SpaceMoltCatalogService(
            executeAsync: (command, payload) => ExecuteAsync(command, payload, CancellationToken.None),
            cacheRepository: _cacheRepository,
            catalogueCacheTtl: CatalogueCacheTtl,
            catalogFetchPageSize: CatalogFetchPageSize);
        _mapService = new SpaceMoltMapService(
            AppPaths.GalaxyMapFile,
            AppPaths.GalaxyKnownPoisFile,
            (command, payload) => ExecuteAsync(command, payload, CancellationToken.None));
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
        var session = await _sessionService.CreateSessionAsync();
        ApplySession(session);
        PersistSessionSnapshot();
    }

    public async Task LoginAsync(string username, string password)
    {
        _username = username?.Trim();
        _password = password;
        if (string.IsNullOrWhiteSpace(_username))
            throw new ArgumentException("Username is required.", nameof(username));
        if (string.IsNullOrWhiteSpace(_password))
            throw new ArgumentException("Password is required.", nameof(password));

        await EnsureUsableSessionAsync(preferCachedSession: true);

        try
        {
            await _sessionService.LoginAsync(_sessionId!, _username!, _password!);
        }
        catch (Exception ex) when (IsSessionInvalidException(ex))
        {
            await RecoverSessionAsync("login");
            await _sessionService.LoginAsync(_sessionId!, _username!, _password!);
        }

        PersistSessionSnapshot();
        await RefreshLatestStateFromApiAsync();
    }

    public async Task<string> RegisterAsync(string username, string empire, string registrationCode)
    {
        _username = username?.Trim();
        if (string.IsNullOrWhiteSpace(_username))
            throw new ArgumentException("Username is required.", nameof(username));

        await EnsureUsableSessionAsync(preferCachedSession: false);
        string password = await _sessionService.RegisterAsync(_sessionId!, _username, empire, registrationCode);
        _password = password;

        PersistSessionSnapshot();
        await RefreshLatestStateFromApiAsync();
        return password;
    }

    public async Task<JsonElement> ExecuteAsync(
        string command,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken = ResolveCancellationToken(cancellationToken);
        JsonElement result = await ExecuteWithRecoveryAsync(
            command,
            payload,
            allowRecoveryRetry: true,
            cancellationToken);

        await RefreshLatestStateAfterCommandAsync(command, cancellationToken);
        return result;
    }

    async Task<RuntimeCommandResult> IRuntimeTransport.ExecuteCommandAsync(
        string command,
        object? payload,
        CancellationToken cancellationToken)
    {
        JsonElement response = await ExecuteAsync(command, payload, cancellationToken);
        return ToRuntimeCommandResult(response);
    }

    private async Task RefreshLatestStateAfterCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (_isRefreshingLatestState)
            return;

        if (!MutationCommands.Contains(command))
            return;

        if (string.Equals(command, "get_status", StringComparison.OrdinalIgnoreCase))
            return;

        await RefreshLatestStateFromApiAsync(cancellationToken);
    }

    private async Task RefreshLatestStateFromApiAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken = ResolveCancellationToken(cancellationToken);
        await _stateRefreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_isRefreshingLatestState)
                return;

            _isRefreshingLatestState = true;

            var status = await ExecuteAsync("get_status", cancellationToken: cancellationToken);
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

    public async Task<JsonElement> FindRouteAsync(
        string targetSystem,
        CancellationToken cancellationToken = default)
    {
        cancellationToken = ResolveCancellationToken(cancellationToken);
        JsonElement routeResult = await ExecuteAsync(
            "find_route",
            new { target_system = targetSystem },
            cancellationToken);

        await SpaceMoltHttpLogging.LogPathfindAsync(targetSystem, routeResult);
        return routeResult;
    }

    async Task<RuntimeCommandResult> IRuntimeTransport.FindRouteAsync(string targetSystem)
    {
        JsonElement response = await FindRouteAsync(targetSystem);
        return ToRuntimeCommandResult(response);
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

    internal async Task ObserveSeenPoisAsync(string systemId, IEnumerable<POIInfo> pois)
    {
        await _mapService.ObserveSeenPoisAsync(systemId, pois);
    }

    public GameState GetGameState()
    {
        if (_latestGameState == null)
            throw new InvalidOperationException("Game state cache is empty.");

        return _latestGameState;
    }

    GameState IRuntimeTransport.GetLatestState()
    {
        return GetGameState();
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
        try
        {
            FlushApiStatsLogAsync("dispose").GetAwaiter().GetResult();
        }
        catch
        {
            // Best effort on shutdown.
        }

        _http.Dispose();
    }

    public SpaceMoltApiStatsSnapshot GetApiStatsSnapshot()
    {
        var commandStats = _apiCommandPerf
            .Select(pair => pair.Value.ToSnapshot(pair.Key))
            .OrderByDescending(s => s.CallCount)
            .ToList();

        var topGetCommands = commandStats
            .Where(s => s.Command.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
            .Take(ApiStatsTopCommandLimit)
            .ToList();

        return new SpaceMoltApiStatsSnapshot(
            StartedUtc: _apiStatsStartedUtc,
            LastUpdatedUtc: DateTime.UtcNow,
            TotalApiCalls: Interlocked.Read(ref _totalApiCalls),
            TotalGetCalls: Interlocked.Read(ref _totalGetCalls),
            DistinctCommands: commandStats.Count,
            TopCommands: commandStats.Take(ApiStatsTopCommandLimit).ToList(),
            TopGetCommands: topGetCommands);
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

    private async Task<JsonElement> ExecuteWithRecoveryAsync(
        string command,
        object? payload,
        bool allowRecoveryRetry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken = ResolveCancellationToken(cancellationToken);
        var timer = Stopwatch.StartNew();
        await EnsureUsableSessionAsync(preferCachedSession: false);

        long requestId = Interlocked.Increment(ref _requestSequence);
        bool succeeded = false;

        try
        {
            JsonElement result = await _transport.ExecuteCommandAsync(
                _sessionId!,
                command,
                payload,
                DebugEnabled,
                DebugContext,
                requestId,
                content => _notificationTracker.ObservePayload(content, ref _currentTick),
                cancellationToken);

            if (allowRecoveryRetry && IsSessionInvalidPayload(result))
            {
                await RecoverSessionAsync($"command:{command}");
                return await ExecuteWithRecoveryAsync(
                    command,
                    payload,
                    allowRecoveryRetry: false,
                    cancellationToken);
            }

            succeeded = !SpaceMoltApiTransport.TryExtractApiError(result, out _, out _, out _);
            return result;
        }
        catch (Exception ex) when (allowRecoveryRetry && IsSessionInvalidException(ex))
        {
            await RecoverSessionAsync($"command:{command}");
            return await ExecuteWithRecoveryAsync(
                command,
                payload,
                allowRecoveryRetry: false,
                cancellationToken);
        }
        finally
        {
            timer.Stop();
            RecordApiCommandObservation(command, timer.Elapsed.TotalMilliseconds, succeeded);
            _ = MaybeLogApiStatsAsync();
        }
    }

    private async Task EnsureUsableSessionAsync(bool preferCachedSession)
    {
        if (HasUsableSession())
            return;

        if (preferCachedSession && TryRestoreSessionFromCache())
            return;

        await CreateSessionAsync();
    }

    private bool HasUsableSession()
    {
        if (string.IsNullOrWhiteSpace(_sessionId))
            return false;

        if (!_sessionExpiresAt.HasValue)
            return true;

        // Refresh proactively when very close to expiry to avoid mid-command failures.
        return _sessionExpiresAt.Value > DateTimeOffset.UtcNow.AddSeconds(30);
    }

    private bool TryRestoreSessionFromCache()
    {
        if (string.IsNullOrWhiteSpace(_username))
            return false;

        if (!_sessionCache.TryGet(_username!, out var cached))
            return false;

        if (cached.ExpiresAt.HasValue && cached.ExpiresAt.Value <= DateTimeOffset.UtcNow.AddSeconds(30))
        {
            _sessionCache.Remove(_username!);
            return false;
        }

        ApplySession(cached);
        return true;
    }

    private static CancellationToken ResolveCancellationToken(CancellationToken cancellationToken)
    {
        if (cancellationToken.CanBeCanceled)
            return cancellationToken;

        var ambient = RuntimeOperationCancellationContext.Current;
        return ambient ?? CancellationToken.None;
    }

    private async Task RecoverSessionAsync(string trigger)
    {
        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            throw new InvalidOperationException(
                $"Session expired during `{trigger}`, and credentials are unavailable for automatic recovery.");

        await _sessionRecoveryLock.WaitAsync();
        try
        {
            // Another caller may have already refreshed the session while we were waiting.
            if (HasUsableSession())
            {
                try
                {
                    await _sessionService.LoginAsync(_sessionId!, _username!, _password!);
                    PersistSessionSnapshot();
                    return;
                }
                catch (Exception ex) when (IsSessionInvalidException(ex))
                {
                    // Continue with full recovery.
                }
            }

            var session = await _sessionService.CreateSessionAsync();
            ApplySession(session);
            await _sessionService.LoginAsync(_sessionId!, _username!, _password!);
            PersistSessionSnapshot();
        }
        finally
        {
            _sessionRecoveryLock.Release();
        }
    }

    private void ApplySession(SpaceMoltSessionInfo session)
    {
        _sessionId = session.Id;
        _sessionExpiresAt = session.ExpiresAt;
    }

    private void PersistSessionSnapshot()
    {
        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_sessionId))
            return;

        _sessionCache.Upsert(_username!, new SpaceMoltSessionInfo
        {
            Id = _sessionId!,
            ExpiresAt = _sessionExpiresAt
        });
    }

    private void RecordApiCommandObservation(string command, double elapsedMs, bool succeeded)
    {
        var now = DateTime.UtcNow;
        var counter = _apiCommandPerf.GetOrAdd(command, _ => new ApiCommandPerfCounter());
        counter.Record(elapsedMs, succeeded, now);

        Interlocked.Increment(ref _totalApiCalls);
        if (command.StartsWith("get_", StringComparison.OrdinalIgnoreCase))
            Interlocked.Increment(ref _totalGetCalls);
        Interlocked.Increment(ref _apiCallsSinceLastStatsLog);
    }

    private async Task MaybeLogApiStatsAsync()
    {
        var now = DateTime.UtcNow;
        bool byCount = Interlocked.Read(ref _apiCallsSinceLastStatsLog) >= ApiStatsLogEveryNCalls;
        bool byTime = (now - _lastApiStatsLogUtc) >= ApiStatsLogInterval;
        if (!byCount && !byTime)
            return;

        await FlushApiStatsLogAsync("periodic");
    }

    private async Task FlushApiStatsLogAsync(string context)
    {
        if (!await _apiStatsLogLock.WaitAsync(0))
            return;

        try
        {
            var now = DateTime.UtcNow;
            var snapshot = GetApiStatsSnapshot();
            var topGetSummary = snapshot.TopGetCommands.Count == 0
                ? "(none)"
                : string.Join(", ", snapshot.TopGetCommands.Select(s =>
                    $"{s.Command}={s.CallCount} (avg={s.AverageElapsedMs:F1}ms, max={s.MaxElapsedMs:F1}ms, fail={s.FailureCount})"));

            var summary =
                $"StartedUtc: {snapshot.StartedUtc:O}\n" +
                $"NowUtc: {now:O}\n" +
                $"TotalApiCalls: {snapshot.TotalApiCalls}\n" +
                $"TotalGetCalls: {snapshot.TotalGetCalls}\n" +
                $"DistinctCommands: {snapshot.DistinctCommands}\n" +
                $"TopGetCommands: {topGetSummary}";

            await SpaceMoltHttpLogging.LogApiCommandStatsAsync(DebugContext, summary);
            _lastApiStatsLogUtc = now;
            Interlocked.Exchange(ref _apiCallsSinceLastStatsLog, 0);
        }
        finally
        {
            _apiStatsLogLock.Release();
        }
    }

    private static bool IsSessionInvalidPayload(JsonElement payload)
    {
        if (!SpaceMoltApiTransport.TryExtractApiError(payload, out var code, out var message, out _))
            return false;

        if (!string.IsNullOrWhiteSpace(code) &&
            (string.Equals(code, "session_invalid", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(code, "unauthorized", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(code, "not_authenticated", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("invalid session", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("session invalid", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not authenticated", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("must login first", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("must log in first", StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeCommandResult ToRuntimeCommandResult(JsonElement payload)
    {
        bool failed = SpaceMoltApiTransport.TryExtractApiError(payload, out _, out var message, out _);
        return new RuntimeCommandResult(
            Succeeded: !failed,
            Payload: payload,
            ErrorMessage: failed ? message : null);
    }

    private static bool IsSessionInvalidException(Exception ex)
    {
        if (ex is HttpRequestException httpEx && httpEx.StatusCode == HttpStatusCode.Unauthorized)
            return true;

        return ex is SpaceMoltApiException apiEx &&
               (apiEx.Message.Contains("session_invalid", StringComparison.OrdinalIgnoreCase) ||
                apiEx.Message.Contains("invalid session", StringComparison.OrdinalIgnoreCase) ||
                apiEx.Message.Contains("missing or invalid session", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ApiCommandPerfCounter
    {
        private readonly object _lock = new();
        private long _callCount;
        private long _failureCount;
        private double _totalElapsedMs;
        private double _maxElapsedMs;
        private DateTime _lastCallUtc;

        public void Record(double elapsedMs, bool succeeded, DateTime nowUtc)
        {
            lock (_lock)
            {
                _callCount++;
                if (!succeeded)
                    _failureCount++;
                _totalElapsedMs += elapsedMs;
                _maxElapsedMs = Math.Max(_maxElapsedMs, elapsedMs);
                _lastCallUtc = nowUtc;
            }
        }

        public SpaceMoltApiCommandStats ToSnapshot(string command)
        {
            lock (_lock)
            {
                var avgMs = _callCount > 0 ? _totalElapsedMs / _callCount : 0;
                return new SpaceMoltApiCommandStats(
                    Command: command,
                    CallCount: _callCount,
                    FailureCount: _failureCount,
                    AverageElapsedMs: avgMs,
                    MaxElapsedMs: _maxElapsedMs,
                    LastCallUtc: _lastCallUtc);
            }
        }
    }
}

public sealed record SpaceMoltApiStatsSnapshot(
    DateTime StartedUtc,
    DateTime LastUpdatedUtc,
    long TotalApiCalls,
    long TotalGetCalls,
    int DistinctCommands,
    IReadOnlyList<SpaceMoltApiCommandStats> TopCommands,
    IReadOnlyList<SpaceMoltApiCommandStats> TopGetCommands);

public sealed record SpaceMoltApiCommandStats(
    string Command,
    long CallCount,
    long FailureCount,
    double AverageElapsedMs,
    double MaxElapsedMs,
    DateTime LastCallUtc);
