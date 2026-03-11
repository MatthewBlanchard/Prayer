using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

internal sealed class SpaceMoltGameStateAssembler
{
    private readonly SpaceMoltHttpClient _owner;
    private readonly RuntimeStateBuilder _stateBuilder;

    public SpaceMoltGameStateAssembler(SpaceMoltHttpClient owner)
    {
        _owner = owner;
        _stateBuilder = new RuntimeStateBuilder();
    }

    private static void LogAssembler(string message)
    {
        var line = $"[{DateTime.UtcNow:O}] state_assembler | {message}{Environment.NewLine}";
        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.RuntimeHost, line, AppPaths.RuntimeHostLogFile));
    }

    public async Task<GameState> BuildAsync(JsonElement status)
    {
        try
        {
            return await BuildAsyncCore(status);
        }
        catch (Exception ex)
        {
            LogAssembler($"build_failed | {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task<GameState> BuildAsyncCore(JsonElement status)
    {
        try
        {
            // Hydrate map cache/state during ordinary state refreshes so script validation
            // can resolve global targets before any `go` step executes.
            await _owner.GetMapSnapshotAsync(forceRefresh: false);
        }
        catch (Exception ex)
        {
            // Best-effort: map hydration failures should not block state assembly.
            LogAssembler($"map_hydration_failed | {ex.GetType().Name}: {ex.Message}");
        }

        var player = SpaceMoltApiTransport.RequireObjectProperty(status, "player", "get_status");
        var ship = SpaceMoltApiTransport.RequireObjectProperty(status, "ship", "get_status");

        string currentSystem = SpaceMoltApiTransport.RequireStringProperty(
            player,
            "current_system",
            "get_status.player");

        var systemResult = await _owner.ExecuteAsync("get_system");

        string[] jumpTargets;
        POIInfo currentPOI;
        POIInfo[] pois;

        bool noSystemError =
            SpaceMoltApiTransport.TryExtractApiError(systemResult, out var errorCode, out _, out _) &&
            string.Equals(errorCode, "no_system", StringComparison.OrdinalIgnoreCase);
        bool inTransitResponse = IsInTransitGetSystemPayload(systemResult);

        if (string.IsNullOrWhiteSpace(currentSystem) && inTransitResponse)
        {
            // Some in-transit status payloads leave current_system empty.
            // Keep the UI/runtime grounded to known system ids while jumping.
            currentSystem =
                TryGetString(systemResult, "from_system") ??
                TryGetString(systemResult, "to_system") ??
                currentSystem;
        }

        if (noSystemError || inTransitResponse)
        {
            // Character is mid-jump and has no current system context yet.
            // Use empty defaults so session creation succeeds; state will refresh on landing.
            jumpTargets = Array.Empty<string>();
            currentPOI = new POIInfo { Id = "", SystemId = currentSystem };
            pois = Array.Empty<POIInfo>();
        }
        else
        {
            SpaceMoltApiTransport.EnsureCommandSucceeded("get_system", systemResult);
            var systemObj = SpaceMoltApiTransport.RequireObjectProperty(systemResult, "system", "get_system");
            var connections = SpaceMoltApiTransport.RequireArrayProperty(systemObj, "connections", "get_system.system");

            if (string.IsNullOrWhiteSpace(currentSystem))
                currentSystem = TryGetString(systemObj, "id") ?? currentSystem;

            jumpTargets = connections
                .EnumerateArray()
                .Select(c => c.GetProperty("system_id").GetString()!)
                .ToArray();

            var currentPoiObj = SpaceMoltApiTransport.RequireObjectProperty(systemResult, "poi", "get_system");

            currentPOI = ParsePoiInfo(currentPoiObj, currentSystem);

            pois = systemObj.TryGetProperty("pois", out var poisArray) &&
                       poisArray.ValueKind == JsonValueKind.Array
                ? poisArray
                    .EnumerateArray()
                    .Select(p => ParsePoiInfo(p, currentSystem))
                    .Where(p => !string.Equals(p.Id, currentPOI.Id, StringComparison.Ordinal))
                    .ToArray()
                : Array.Empty<POIInfo>();

            await EnrichCurrentPoiFromGetPoiAsync(currentPOI);
            GalaxyStateHub.MarkPoiVisited(
                currentPOI.Id,
                currentSystem,
                currentPOI.Name,
                currentPOI.Type,
                currentPOI.X,
                currentPOI.Y,
                currentPOI.HasBase,
                currentPOI.BaseId,
                currentPOI.BaseName);

            await _owner.ObserveSeenPoisAsync(
                currentSystem,
                new[] { currentPOI }.Concat(pois));
            GalaxyStateHub.MergeResourceLocations(
                currentSystem,
                new[] { currentPOI }.Concat(pois));
        }

        var cargo = new Dictionary<string, ItemStack>();
        if (ship.TryGetProperty("cargo", out var cargoArray))
        {
            foreach (var c in cargoArray.EnumerateArray())
            {
                string itemId = c.GetProperty("item_id").GetString()!;
                int quantity = c.GetProperty("quantity").GetInt32();
                cargo[itemId] = new ItemStack(itemId, quantity);
            }
        }

        string? dockedBaseId = null;
        bool docked =
            player.TryGetProperty("docked_at_base", out var d) &&
            d.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(d.GetString());

        if (docked)
            dockedBaseId = d.GetString();

        var state = _stateBuilder.BuildBaseState(
            currentSystem,
            currentPOI,
            pois,
            jumpTargets,
            cargo,
            player,
            ship,
            docked);

        try
        {
            // OpenAPI explicitly defines /get_skills for skill progress; hydrate from it to
            // avoid relying on get_status variants that may omit or reshape player.skills.
            var skillsResult = await _owner.ExecuteAsync("get_skills");
            if (SpaceMoltResponseParsers.TryParseSkills(skillsResult, out var parsedSkills) &&
                parsedSkills.Count > 0)
            {
                state.Skills = parsedSkills;
            }
        }
        catch (Exception ex)
        {
            LogAssembler($"skills_fetch_failed | {ex.GetType().Name}: {ex.Message}");
        }

        var stationCache = _owner.StationCache;

        if (state.Docked && state.CurrentPOI.IsStation)
        {
            string stationId = state.CurrentPOI.Id;
            string storageStationId = string.IsNullOrWhiteSpace(dockedBaseId)
                ? stationId
                : dockedBaseId!;

            if (!stationCache.TryGetValue(stationId, out var stationInfo))
            {
                stationInfo = new StationInfo
                {
                    StationId = stationId
                };
                stationCache[stationId] = stationInfo;
            }

            var storageResult = await _owner.ExecuteAsync("view_storage", new { station_id = storageStationId });
            if (SpaceMoltMarketAnalytics.TryParseStorageSnapshot(storageResult, out int credits, out var storageItems))
            {
                stationInfo.StorageCredits = credits;
                stationInfo.StorageItems = storageItems;
                _owner.StorageCacheByPoi[storageStationId] = SpaceMoltMarketAnalytics.CloneItems(storageItems);
            }

            var marketResult = await _owner.ExecuteAsync("view_market");
            if (SpaceMoltMarketAnalytics.TryParseMarketSnapshot(marketResult, stationId, out var market))
            {
                stationInfo.Market = market;
                _owner.SaveMarketCacheToDisk(stationId, market);
                GalaxyStateHub.MergeMarkets(new[] { market });
            }

            var analyzeMarketResult = await _owner.ExecuteAsync("analyze_market");
            await SpaceMoltHttpLogging.LogAnalyzeMarketAsync(stationId, analyzeMarketResult);

            var viewOrdersResult = await _owner.ExecuteAsync("view_orders", new { station_id = stationId });
            if (SpaceMoltMarketAnalytics.TryParseOwnOrders(viewOrdersResult, out var buyOrders, out var sellOrders))
            {
                stationInfo.BuyOrders = buyOrders;
                stationInfo.SellOrders = sellOrders;
            }

            bool shouldRefreshShowroom =
                stationInfo.ShipyardShowroom == null ||
                stationInfo.ShipyardShowroom.Length == 0;
            bool shouldRefreshListings =
                stationInfo.ShipyardListings == null ||
                stationInfo.ShipyardListings.Length == 0;

            if (shouldRefreshShowroom || shouldRefreshListings)
            {
                if (shouldRefreshShowroom)
                {
                    var showroomResult = await _owner.ExecuteAsync("shipyard_showroom");
                    stationInfo.ShipyardShowroom = SpaceMoltResponseParsers.ParseShipyardShowroom(showroomResult);
                }

                if (shouldRefreshListings)
                {
                    var listingsResult = await _owner.ExecuteAsync("browse_ships");
                    stationInfo.ShipyardListings = SpaceMoltResponseParsers.ParseShipyardListings(listingsResult);
                }

                _owner.SaveShipyardCacheToDisk(
                    stationId,
                    stationInfo.ShipyardShowroom ?? Array.Empty<ShipyardShowroomEntry>(),
                    stationInfo.ShipyardListings ?? Array.Empty<ShipyardListingEntry>());
            }

            if (_owner.TryGetCachedCatalogue(SpaceMoltCatalogService.FullShipCatalogueCacheFileKey, out var cachedFullCatalogue))
            {
                var age = DateTime.UtcNow - cachedFullCatalogue.CachedAtUtc;
                state.ShipCatalogue = age <= _owner.CatalogueCacheTtlValue && cachedFullCatalogue.Catalogue.NormalizedEntries.Length > 0
                    ? SpaceMoltCatalogService.BuildShipCatalogPage(
                        cachedFullCatalogue.Catalogue,
                        _owner.ShipCatalogPage,
                        _owner.ShipCatalogPageSize)
                    : new Catalogue();
                _owner.SetShipCatalogPage(state.ShipCatalogue.Page ?? 1);
            }
            else
            {
                state.ShipCatalogue = new Catalogue();
            }

            _stateBuilder.ApplyDockedStationState(
                state,
                stationInfo.StorageCredits,
                SpaceMoltMarketAnalytics.CloneItems(stationInfo.StorageItems),
                _owner.BuildBestDealsForCurrentStation(stationId, maxDeals: 3),
                stationInfo.BuyOrders.ToArray(),
                stationInfo.SellOrders.ToArray(),
                stationInfo.ShipyardShowroom ?? Array.Empty<ShipyardShowroomEntry>(),
                stationInfo.ShipyardListings ?? Array.Empty<ShipyardListingEntry>(),
                state.ShipCatalogue);

            try
            {
                var recipeCatalog = await _owner.GetFullRecipeCatalogByIdAsync(forceRefresh: false);
                state.AvailableRecipes = recipeCatalog.Values.ToArray();
            }
            catch (Exception ex)
            {
                // Best-effort: recipe catalog failure should not block state assembly.
                LogAssembler($"recipe_catalog_failed | {ex.GetType().Name}: {ex.Message}");
            }
        }
        else
        {
            _stateBuilder.ApplyUndockedDefaults(state);
        }

        GalaxyStateHub.MergeMarkets(stationCache.Values.Select(s => s.Market));
        var galaxySnapshot = GalaxyStateHub.Snapshot();
        var ownedShipsResult = await _owner.ExecuteAsync("list_ships");
        var ownedShips = SpaceMoltResponseParsers.TryParseOwnedShips(ownedShipsResult, out var parsedOwnedShips)
            ? parsedOwnedShips
            : Array.Empty<OwnedShipInfo>();

        var activeMissionsResult = await _owner.ExecuteAsync("get_active_missions");
        var activeMissions = new List<MissionInfo>();
        if (activeMissionsResult.TryGetProperty("missions", out var activeArray))
        {
            foreach (var m in activeArray.EnumerateArray())
                activeMissions.Add(SpaceMoltResponseParsers.ParseMissionInfo(m, isActiveMission: true));
        }

        if (state.Docked && state.CurrentPOI.IsStation)
        {
            var missionsResult = await _owner.ExecuteAsync("get_missions");
            var available = new List<MissionInfo>();

            if (missionsResult.TryGetProperty("missions", out var missionArray))
            {
                foreach (var m in missionArray.EnumerateArray())
                    available.Add(SpaceMoltResponseParsers.ParseMissionInfo(m, isActiveMission: false));
            }

            var availableMissions = available.ToArray();
            _stateBuilder.ApplyGlobalState(
                state,
                galaxySnapshot,
                ownedShips,
                activeMissions.ToArray(),
                availableMissions,
                _owner.DrainPendingNotifications(maxCount: 10),
                _owner.SnapshotChatMessages(maxCount: 5));
        }
        else
        {
            _stateBuilder.ApplyGlobalState(
                state,
                galaxySnapshot,
                ownedShips,
                activeMissions.ToArray(),
                Array.Empty<MissionInfo>(),
                _owner.DrainPendingNotifications(maxCount: 10),
                _owner.SnapshotChatMessages(maxCount: 5));
        }

        state.StorageCacheByPoi = _owner.StorageCacheByPoi
            .ToDictionary(
                kvp => kvp.Key,
                kvp => SpaceMoltMarketAnalytics.CloneItems(kvp.Value),
                StringComparer.Ordinal);

        return state;
    }

    private static POIInfo ParsePoiInfo(JsonElement poiObj, string fallbackSystemId)
    {
        var info = new POIInfo
        {
            Id = TryGetString(poiObj, "id") ?? "",
            SystemId = TryGetString(poiObj, "system_id") ?? fallbackSystemId,
            Name = TryGetString(poiObj, "name") ?? "",
            Type = TryGetString(poiObj, "type") ?? "",
            Description = TryGetString(poiObj, "description") ?? "",
            Hidden = TryGetBool(poiObj, "hidden") ?? false,
            HasBase = TryGetBool(poiObj, "has_base") ?? false,
            BaseId = TryGetString(poiObj, "base_id"),
            BaseName = TryGetString(poiObj, "base_name"),
            Online = TryGetInt(poiObj, "online") ?? 0,
            Resources = ParsePoiResources(poiObj)
        };

        if (poiObj.TryGetProperty("position", out var positionObj) &&
            positionObj.ValueKind == JsonValueKind.Object)
        {
            info.X = TryGetDouble(positionObj, "x");
            info.Y = TryGetDouble(positionObj, "y");
        }

        return info;
    }

    private static PoiResourceInfo[] ParsePoiResources(JsonElement poiObj)
    {
        if (!poiObj.TryGetProperty("resources", out var resources) ||
            resources.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<PoiResourceInfo>();
        }

        return ParseResourcesArray(resources);
    }

    private async Task EnrichCurrentPoiFromGetPoiAsync(POIInfo currentPOI)
    {
        try
        {
            var poiResult = await _owner.ExecuteAsync("get_poi");
            if (poiResult.ValueKind != JsonValueKind.Object)
                return;

            if (poiResult.TryGetProperty("poi", out var poiObj) &&
                poiObj.ValueKind == JsonValueKind.Object)
            {
                currentPOI.Description = TryGetString(poiObj, "description") ?? currentPOI.Description;
                currentPOI.Hidden = TryGetBool(poiObj, "hidden") ?? currentPOI.Hidden;
                currentPOI.SystemId = TryGetString(poiObj, "system_id") ?? currentPOI.SystemId;

                if (poiObj.TryGetProperty("position", out var positionObj) &&
                    positionObj.ValueKind == JsonValueKind.Object)
                {
                    currentPOI.X = TryGetDouble(positionObj, "x") ?? currentPOI.X;
                    currentPOI.Y = TryGetDouble(positionObj, "y") ?? currentPOI.Y;
                }
            }

            if (poiResult.TryGetProperty("resources", out var resourcesObj) &&
                resourcesObj.ValueKind == JsonValueKind.Array)
            {
                currentPOI.Resources = ParseResourcesArray(resourcesObj);
            }
            else if (poiResult.TryGetProperty("poi", out var nestedPoiObj) &&
                     nestedPoiObj.ValueKind == JsonValueKind.Object &&
                     nestedPoiObj.TryGetProperty("resources", out var nestedResources) &&
                     nestedResources.ValueKind == JsonValueKind.Array)
            {
                currentPOI.Resources = ParseResourcesArray(nestedResources);
            }
        }
        catch (Exception ex)
        {
            // Optional enrichment: keep get_system snapshot even if get_poi fails.
            LogAssembler($"get_poi_enrich_failed | {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static PoiResourceInfo[] ParseResourcesArray(JsonElement resources)
    {
        return resources
            .EnumerateArray()
            .Where(r => r.ValueKind == JsonValueKind.Object)
            .Select(r => new PoiResourceInfo
            {
                ResourceId = TryGetString(r, "resource_id") ?? "",
                Name = TryGetString(r, "name") ?? "",
                RichnessText = TryGetString(r, "richness") ?? "",
                Richness = TryGetInt(r, "richness"),
                Remaining = TryGetInt(r, "remaining"),
                RemainingDisplay = TryGetString(r, "remaining_display") ?? ""
            })
            .ToArray();
    }

    private static string? TryGetString(JsonElement obj, string property)
    {
        return obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;
    }

    private static int? TryGetInt(JsonElement obj, string property)
    {
        return obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static double? TryGetDouble(JsonElement obj, string property)
    {
        return obj.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var value)
            ? value
            : null;
    }

    private static bool? TryGetBool(JsonElement obj, string property)
    {
        return obj.TryGetProperty(property, out var el) &&
               (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
            ? el.GetBoolean()
            : null;
    }

    private static bool IsInTransitGetSystemPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return false;

        return payload.TryGetProperty("in_transit", out var inTransitEl) &&
               inTransitEl.ValueKind == JsonValueKind.True;
    }
}
