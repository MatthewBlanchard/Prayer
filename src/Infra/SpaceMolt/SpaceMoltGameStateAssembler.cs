using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

internal sealed class SpaceMoltGameStateAssembler
{
    private readonly SpaceMoltHttpClient _owner;

    public SpaceMoltGameStateAssembler(SpaceMoltHttpClient owner)
    {
        _owner = owner;
    }

    public async Task<GameState> BuildAsync(JsonElement status)
    {
        var player = SpaceMoltApiTransport.RequireObjectProperty(status, "player", "get_status");
        var ship = SpaceMoltApiTransport.RequireObjectProperty(status, "ship", "get_status");

        string currentSystem = SpaceMoltApiTransport.RequireStringProperty(
            player,
            "current_system",
            "get_status.player");

        var systemResult = await _owner.ExecuteAsync("get_system");
        SpaceMoltApiTransport.EnsureCommandSucceeded("get_system", systemResult);
        var systemObj = SpaceMoltApiTransport.RequireObjectProperty(systemResult, "system", "get_system");
        var connections = SpaceMoltApiTransport.RequireArrayProperty(systemObj, "connections", "get_system.system");

        var jumpTargets = connections
            .EnumerateArray()
            .Select(c => c.GetProperty("system_id").GetString()!)
            .ToArray();

        var currentPoiObj = SpaceMoltApiTransport.RequireObjectProperty(systemResult, "poi", "get_system");

        var currentPOI = new POIInfo
        {
            Id = currentPoiObj.GetProperty("id").GetString() ?? "",
            Name = currentPoiObj.GetProperty("name").GetString() ?? "",
            Type = currentPoiObj.GetProperty("type").GetString() ?? "",
            HasBase = currentPoiObj.TryGetProperty("has_base", out var hb) && hb.GetBoolean(),
            BaseId = currentPoiObj.TryGetProperty("base_id", out var bid) ? bid.GetString() : null,
            BaseName = currentPoiObj.TryGetProperty("base_name", out var bn) ? bn.GetString() : null,
            Online = currentPoiObj.TryGetProperty("online", out var on) ? on.GetInt32() : 0
        };

        var pois = systemObj
            .GetProperty("pois")
            .EnumerateArray()
            .Where(p => p.GetProperty("id").GetString() != currentPOI.Id)
            .Select(p => new POIInfo
            {
                Id = p.GetProperty("id").GetString() ?? "",
                Name = p.GetProperty("name").GetString() ?? "",
                Type = p.GetProperty("type").GetString() ?? "",
                HasBase = p.TryGetProperty("has_base", out var hb2) && hb2.GetBoolean(),
                BaseId = p.TryGetProperty("base_id", out var bid2) ? bid2.GetString() : null,
                BaseName = p.TryGetProperty("base_name", out var bn2) ? bn2.GetString() : null,
                Online = p.TryGetProperty("online", out var on2) ? on2.GetInt32() : 0
            })
            .ToArray();

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

        var state = new GameState
        {
            System = currentSystem,
            CurrentPOI = currentPOI,
            POIs = pois,
            Systems = jumpTargets,
            Cargo = cargo,

            ShipName = ship.TryGetProperty("name", out var shipNameEl) && shipNameEl.ValueKind == JsonValueKind.String
                ? shipNameEl.GetString() ?? ""
                : "",
            ShipClassId = ship.TryGetProperty("class_id", out var shipClassEl) && shipClassEl.ValueKind == JsonValueKind.String
                ? shipClassEl.GetString() ?? ""
                : "",
            Armor = ship.TryGetProperty("armor", out var armorEl) && armorEl.ValueKind == JsonValueKind.Number
                ? armorEl.GetInt32()
                : 0,
            Speed = ship.TryGetProperty("speed", out var speedEl) && speedEl.ValueKind == JsonValueKind.Number
                ? speedEl.GetInt32()
                : 0,
            CpuUsed = ship.TryGetProperty("cpu_used", out var cpuUsedEl) && cpuUsedEl.ValueKind == JsonValueKind.Number
                ? cpuUsedEl.GetInt32()
                : 0,
            CpuCapacity = ship.TryGetProperty("cpu_capacity", out var cpuCapEl) && cpuCapEl.ValueKind == JsonValueKind.Number
                ? cpuCapEl.GetInt32()
                : 0,
            PowerUsed = ship.TryGetProperty("power_used", out var powerUsedEl) && powerUsedEl.ValueKind == JsonValueKind.Number
                ? powerUsedEl.GetInt32()
                : 0,
            PowerCapacity = ship.TryGetProperty("power_capacity", out var powerCapEl) && powerCapEl.ValueKind == JsonValueKind.Number
                ? powerCapEl.GetInt32()
                : 0,
            ModuleCount = ship.TryGetProperty("modules", out var modulesEl) && modulesEl.ValueKind == JsonValueKind.Array
                ? modulesEl.GetArrayLength()
                : 0,

            Fuel = ship.GetProperty("fuel").GetInt32(),
            MaxFuel = ship.GetProperty("max_fuel").GetInt32(),
            Credits = player.GetProperty("credits").GetInt32(),
            Docked = docked,
            Hull = ship.GetProperty("hull").GetInt32(),
            MaxHull = ship.GetProperty("max_hull").GetInt32(),
            Shield = ship.GetProperty("shield").GetInt32(),
            MaxShield = ship.GetProperty("max_shield").GetInt32(),
            CargoUsed = ship.GetProperty("cargo_used").GetInt32(),
            CargoCapacity = ship.GetProperty("cargo_capacity").GetInt32(),
            Shared = new SharedGameState(),
            Notifications = Array.Empty<GameNotification>(),
            ChatMessages = Array.Empty<GameChatMessage>()
        };

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
                stationInfo.ShipyardShowroomLines == null ||
                stationInfo.ShipyardShowroomLines.Length == 0;
            bool shouldRefreshListings =
                stationInfo.ShipyardListingLines == null ||
                stationInfo.ShipyardListingLines.Length == 0;

            if (shouldRefreshShowroom || shouldRefreshListings)
            {
                if (shouldRefreshShowroom)
                {
                    var showroomResult = await _owner.ExecuteAsync("shipyard_showroom");
                    if (SpaceMoltResponseParsers.TryParseShipyardShowroom(showroomResult, out var showroomLines))
                        stationInfo.ShipyardShowroomLines = showroomLines;
                }

                if (shouldRefreshListings)
                {
                    var listingsResult = await _owner.ExecuteAsync("browse_ships");
                    if (SpaceMoltResponseParsers.TryParseShipyardListings(listingsResult, out var listingLines))
                        stationInfo.ShipyardListingLines = listingLines;
                }

                _owner.SaveShipyardCacheToDisk(
                    stationId,
                    stationInfo.ShipyardShowroomLines ?? Array.Empty<string>(),
                    stationInfo.ShipyardListingLines ?? Array.Empty<string>());
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

            state.Shared.StorageCredits = stationInfo.StorageCredits;
            state.Shared.StorageItems = SpaceMoltMarketAnalytics.CloneItems(stationInfo.StorageItems);
            state.Shared.Market = SpaceMoltMarketAnalytics.CloneMarket(stationInfo.Market);
            state.Shared.EconomyDeals = _owner.BuildBestDealsForCurrentStation(stationId, maxDeals: 3);
            state.Shared.OwnBuyOrders = stationInfo.BuyOrders.ToArray();
            state.Shared.OwnSellOrders = stationInfo.SellOrders.ToArray();
            state.ShipyardShowroomLines = stationInfo.ShipyardShowroomLines ?? Array.Empty<string>();
            state.ShipyardListingLines = stationInfo.ShipyardListingLines ?? Array.Empty<string>();
        }
        else
        {
            state.Shared.StorageCredits = 0;
            state.Shared.StorageItems = new Dictionary<string, ItemStack>();
            state.Shared.Market = null;
            state.Shared.EconomyDeals = Array.Empty<EconomyDeal>();
            state.Shared.OwnBuyOrders = Array.Empty<OpenOrderInfo>();
            state.Shared.OwnSellOrders = Array.Empty<OpenOrderInfo>();
            state.ShipyardShowroomLines = Array.Empty<string>();
            state.ShipyardListingLines = Array.Empty<string>();
            state.ShipCatalogue = new Catalogue();
        }

        GalaxyStateHub.MergeMarkets(stationCache.Values.Select(s => s.Market));
        var galaxySnapshot = GalaxyStateHub.Snapshot();
        state.Galaxy = galaxySnapshot;
        state.Shared.GlobalMedianBuyPrices = new Dictionary<string, decimal>(
            galaxySnapshot.Market.GlobalMedianBuyPrices,
            StringComparer.Ordinal);
        state.Shared.GlobalMedianSellPrices = new Dictionary<string, decimal>(
            galaxySnapshot.Market.GlobalMedianSellPrices,
            StringComparer.Ordinal);
        state.Shared.GlobalWeightedMidPrices = new Dictionary<string, decimal>(
            galaxySnapshot.Market.GlobalWeightedMidPrices,
            StringComparer.Ordinal);

        var ownedShipsResult = await _owner.ExecuteAsync("list_ships");
        state.OwnedShips = SpaceMoltResponseParsers.TryParseOwnedShips(ownedShipsResult, out var ownedShips)
            ? ownedShips
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

            state.AvailableMissions = available.ToArray();
        }
        else
        {
            state.AvailableMissions = Array.Empty<MissionInfo>();
        }

        state.ActiveMissions = activeMissions.ToArray();
        state.Notifications = _owner.DrainPendingNotifications(maxCount: 10);
        state.ChatMessages = _owner.SnapshotChatMessages(maxCount: 5);

        return state;
    }
}
