using System;
using System.Collections.Generic;
using System.Linq;

public partial class GameState
{
    private sealed class RenderData
    {
        public string PoiDockState { get; init; } = "";
        public int FuelPct { get; init; }
        public int HullPct { get; init; }
        public int ShieldPct { get; init; }
        public int CargoFree { get; init; }
        public int CargoPct { get; init; }
        public string PoisMarkdown { get; init; } = "";
        public string SystemsMarkdown { get; init; } = "";
        public string CargoMarkdown { get; init; } = "";
        public string StorageItemsSectionMarkdown { get; init; } = "";
        public string EconomySectionMarkdown { get; init; } = "";
        public string PoisDisplay { get; init; } = "";
        public string SystemsDisplay { get; init; } = "";
        public string CargoDisplay { get; init; } = "";
        public string StorageItemsSectionDisplay { get; init; } = "";
        public string EconomySectionDisplay { get; init; } = "";
        public string NotificationsMarkdown { get; init; } = "";
        public string NotificationsDisplay { get; init; } = "";
    }
    private RenderData BuildRenderData()
    {
        var estimatedPrices = BuildEstimatedItemPrices();
        var storageItemsSection = Docked && StorageItems != null && StorageItems.Count > 0
            ? $"\n### Storage Items\n{FormatCargo(StorageItems, estimatedPrices)}\n"
            : "";
        var economySection = Docked && CurrentPOI.IsStation
            ? $"\n### Economy\n{FormatEconomy(EconomyDeals, OwnBuyOrders, OwnSellOrders)}\n"
            : "";

        int fuelPct = Ship.MaxFuel > 0 ? (Ship.Fuel * 100) / Ship.MaxFuel : 0;
        int hullPct = Ship.MaxHull > 0 ? (Ship.Hull * 100) / Ship.MaxHull : 0;
        int shieldPct = Ship.MaxShield > 0 ? (Ship.Shield * 100) / Ship.MaxShield : 0;
        int cargoFree = Math.Max(0, Ship.CargoCapacity - Ship.CargoUsed);
        int cargoPct = Ship.CargoCapacity > 0 ? (Ship.CargoUsed * 100) / Ship.CargoCapacity : 0;
        string poiDockState = CurrentPOI.HasBase
            ? (Docked ? " DOCKED" : " DOCKABLE")
            : "";

        var poisMarkdown = FormatPOIs(POIs);
        var systemsMarkdown = FormatList(Systems);
        var cargoMarkdown = FormatCargo(Ship.Cargo, estimatedPrices);
        var notificationsMarkdown = FormatNotifications(Notifications);

        return new RenderData
        {
            PoiDockState = poiDockState,
            FuelPct = fuelPct,
            HullPct = hullPct,
            ShieldPct = shieldPct,
            CargoFree = cargoFree,
            CargoPct = cargoPct,
            PoisMarkdown = poisMarkdown,
            SystemsMarkdown = systemsMarkdown,
            CargoMarkdown = cargoMarkdown,
            StorageItemsSectionMarkdown = storageItemsSection,
            EconomySectionMarkdown = economySection,
            PoisDisplay = StripMarkdown(poisMarkdown),
            SystemsDisplay = StripMarkdown(systemsMarkdown),
            CargoDisplay = StripMarkdown(cargoMarkdown),
            StorageItemsSectionDisplay = StripMarkdown(storageItemsSection),
            EconomySectionDisplay = StripMarkdown(economySection),
            NotificationsMarkdown = notificationsMarkdown,
            NotificationsDisplay = StripMarkdown(notificationsMarkdown)
        };
    }

    internal string BuildNotificationsLlmSection()
    {
        string body = FormatNotifications(Notifications);
        if (string.IsNullOrWhiteSpace(body))
            return "";

        return $"\n### Notifications\n{body}\n";
    }

    internal string BuildChatLlmSection()
    {
        string body = FormatChat(ChatMessages);
        if (string.IsNullOrWhiteSpace(body))
            return "";

        return $"\n### Chat\n{body}\n";
    }

    internal string BuildNotificationsDisplaySection()
    {
        string body = StripMarkdown(FormatNotifications(Notifications));
        if (string.IsNullOrWhiteSpace(body))
            return "";

        return $"\nNOTIFICATIONS\n{body}";
    }

    internal string BuildChatDisplaySection()
    {
        string body = StripMarkdown(FormatChat(ChatMessages));
        if (string.IsNullOrWhiteSpace(body))
            return "";

        return $"\nCHAT\n{body}";
    }

    public string ToLLMMarkdown()
    {
        return RenderSpaceLlmMarkdown();
    }

    public string ToDisplayText()
    {
        return RenderSpaceDisplayText();
    }

    internal string RenderTradeLlmMarkdown()
    {
        var estimatedPrices = BuildEstimatedItemPrices();
        var cargo = FormatCargo(Ship.Cargo, estimatedPrices);
        var storage = StorageItems != null && StorageItems.Count > 0
            ? FormatCargo(StorageItems, estimatedPrices)
            : "";
        var economy = FormatEconomy(EconomyDeals, OwnBuyOrders, OwnSellOrders);

        var storageSection = string.IsNullOrWhiteSpace(storage)
            ? ""
            : $"\n### Storage Items\n{storage}\n";

        return
$@"
Active Context: `TradeState`
Current Station: `{CurrentPOI.Id}`
Credits: {Credits}
Fuel: {Ship.Fuel}/{Ship.MaxFuel}
Cargo: {Ship.CargoUsed}/{Ship.CargoCapacity}

### Cargo
{cargo}
{storageSection}
### Economy
{economy}
{BuildNotificationsLlmSection()}
";
    }

    internal string RenderShipyardLlmMarkdown()
    {
        var estimatedPrices = BuildEstimatedItemPrices();
        var cargo = FormatCargo(Ship.Cargo, estimatedPrices);

        return
$@"
Active Context: `ShipYardState`
Current Station: `{CurrentPOI.Id}`
Credits: {Credits}
Fuel: {Ship.Fuel}/{Ship.MaxFuel}
Cargo: {Ship.CargoUsed}/{Ship.CargoCapacity}

### Showroom
{FormatShipyardShowroom(ShipyardShowroom)}

### Player Listings
{FormatShipyardListings(ShipyardListings)}

### Cargo
{cargo}
{BuildNotificationsLlmSection()}
";
    }

    internal string RenderHangarLlmMarkdown()
    {
        var estimatedPrices = BuildEstimatedItemPrices();
        var cargo = FormatCargo(Ship.Cargo, estimatedPrices);

        return
$@"
Active Context: `HangarState`
Current Station: `{CurrentPOI.Id}`
Credits: {Credits}
Fuel: {Ship.Fuel}/{Ship.MaxFuel}
Cargo: {Ship.CargoUsed}/{Ship.CargoCapacity}

### Active Ship Stats
Armor: {Ship.Armor}
Speed: {Ship.Speed}
CPU: {Ship.CpuUsed}/{Ship.CpuCapacity}
Power: {Ship.PowerUsed}/{Ship.PowerCapacity}
Modules: {Ship.ModuleCount}

### Owned Ships
{FormatOwnedShips(OwnedShips)}

### Cargo
{cargo}
{BuildNotificationsLlmSection()}
";
    }

    internal string RenderCraftingLlmMarkdown()
    {
        var estimatedPrices = BuildEstimatedItemPrices();
        var cargo = FormatCargo(Ship.Cargo, estimatedPrices);
        var storage = StorageItems != null && StorageItems.Count > 0
            ? FormatCargo(StorageItems, estimatedPrices)
            : "";

        var storageSection = string.IsNullOrWhiteSpace(storage)
            ? ""
            : $"\n### Storage Items\n{storage}\n";

        return
$@"
Active Context: `CraftingState`
Current Station: `{CurrentPOI.Id}`
Credits: {Credits}
Cargo: {Ship.CargoUsed}/{Ship.CargoCapacity}

### Available Recipes
{FormatAvailableRecipes(AvailableRecipes)}

### Cargo
{cargo}
{storageSection}{BuildNotificationsLlmSection()}
";
    }

    internal static string FormatAvailableRecipes(CatalogueEntry[] recipes)
    {
        if (recipes == null || recipes.Length == 0)
            return "- _(none)_";

        return string.Join("\n", recipes
            .OrderBy(r => string.IsNullOrWhiteSpace(r.Category) ? "Unknown" : r.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Tier ?? int.MaxValue)
            .ThenBy(r => string.IsNullOrWhiteSpace(r.Name) ? r.Id : r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r =>
            {
                string id = string.IsNullOrWhiteSpace(r.Id) ? "?" : r.Id;
                string name = string.IsNullOrWhiteSpace(r.Name) ? id : r.Name;
                string category = string.IsNullOrWhiteSpace(r.Category) ? "Unknown" : r.Category;
                string tier = r.Tier.HasValue ? $" T{r.Tier.Value}" : "";
                return $"- `{id}`: {name} | {category}{tier}";
            }));
    }

    internal string RenderShipCatalogLlmMarkdown()
    {
        int currentPage = ShipCatalogue.Page ?? 1;
        int totalPages = ShipCatalogue.TotalPages ?? 1;
        int totalItems = ShipCatalogue.Total ?? ShipCatalogue.TotalItems ?? 0;
        var entries = ShipCatalogue.NormalizedEntries;

        return
$@"
Active Context: `ShipCatalogState`
Current Station: `{CurrentPOI.Id}`
Page: {currentPage}/{totalPages}
Entries On Page: {entries.Length}
Total Ships: {totalItems}

### Ships
{FormatCatalogueEntries(entries)}
{BuildNotificationsLlmSection()}
";
    }

    // Compatibility: legacy callers still get LLM markdown.
    public string ToMD() => ToLLMMarkdown();

    internal string RenderSpaceLlmMarkdown()
    {
        var r = BuildRenderData();
        string currentPoiResources = FormatPoiResources(CurrentPOI.Resources);
        return
$@"
Current System (your location): `{System}`
Current POI (your location): `{CurrentPOI.Id}` ({CurrentPOI.Type}){r.PoiDockState}
Credits: {Credits}

### Ship
Name: {(string.IsNullOrWhiteSpace(Ship.Name) ? "-" : Ship.Name)}
Class: {(string.IsNullOrWhiteSpace(Ship.ClassId) ? "-" : Ship.ClassId)}
Fuel: {Ship.Fuel}/{Ship.MaxFuel} ({r.FuelPct}%)
Hull: {Ship.Hull}/{Ship.MaxHull} ({r.HullPct}%)
Shield: {Ship.Shield}/{Ship.MaxShield} ({r.ShieldPct}%)
Cargo: {Ship.CargoUsed}/{Ship.CargoCapacity} ({r.CargoPct}% used, {r.CargoFree} free)
Current POI Online: {CurrentPOI.Online}
Current POI Resources: {currentPoiResources}

### POIs
{r.PoisMarkdown}

### Connected Systems
{r.SystemsMarkdown}

### Cargo
{r.CargoMarkdown}
{r.StorageItemsSectionMarkdown}
{BuildChatLlmSection()}
{r.NotificationsMarkdown}
";
    }

    internal string RenderSpaceDisplayText()
    {
        var r = BuildRenderData();
        string currentPoiResources = FormatPoiResources(CurrentPOI.Resources);
        string stationCreditsLine = Docked
            ? $"\nSTATION CREDITS: {StorageCredits}"
            : "";

        return
$@"SYSTEM: {System}
POI: {CurrentPOI.Id} ({CurrentPOI.Type}){r.PoiDockState}
CREDITS: {Credits}
{stationCreditsLine}

SHIP
- Name: {(string.IsNullOrWhiteSpace(Ship.Name) ? "-" : Ship.Name)}
- Class: {(string.IsNullOrWhiteSpace(Ship.ClassId) ? "-" : Ship.ClassId)}
- Fuel: {Ship.Fuel}/{Ship.MaxFuel} ({r.FuelPct}%)
- Hull: {Ship.Hull}/{Ship.MaxHull} ({r.HullPct}%)
- Shield: {Ship.Shield}/{Ship.MaxShield} ({r.ShieldPct}%)
- Cargo: {Ship.CargoUsed}/{Ship.CargoCapacity} ({r.CargoPct}% used, {r.CargoFree} free)
- POI Online: {CurrentPOI.Online}
- POI Resources: {currentPoiResources}

POIS
{r.PoisDisplay}

CONNECTED SYSTEMS
{r.SystemsDisplay}

CARGO
{r.CargoDisplay}
{r.StorageItemsSectionDisplay}{BuildChatDisplaySection()}{BuildNotificationsDisplaySection()}";
    }

    internal static string StripMarkdown(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return value
            .Replace("### ", "", StringComparison.Ordinal)
            .Replace("`", "", StringComparison.Ordinal);
    }

    internal static string FormatList(string[] values)
    {
        if (values == null || values.Length == 0)
            return "- _(none)_";

        return string.Join("\n", values.Select(v => $"- `{v}`"));
    }

    internal static string FormatPOIs(POIInfo[] pois)
    {
        if (pois == null || pois.Length == 0)
            return "- _(none)_";

        return string.Join("\n", pois.Select(p =>
        {
            string resources = "";
            if (p.Resources != null && p.Resources.Length > 0)
            {
                string resourceList = string.Join(", ", p.Resources
                    .Where(r => !string.IsNullOrWhiteSpace(r.ResourceId))
                    .Take(3)
                    .Select(r => r.ResourceId));
                if (!string.IsNullOrWhiteSpace(resourceList))
                    resources = $" | Resources: {resourceList}";
            }

            return
            $"- `{p.Id}` ({p.Type})" +
            (p.HasBase ? " | Dockable" : "") +
            (p.IsMiningTarget ? " | Mining" : "") +
            resources;
        }));
    }

    internal static string FormatCargo(Dictionary<string, ItemStack> cargo)
    {
        return FormatCargo(cargo, null);
    }

    internal static string FormatCargo(
        Dictionary<string, ItemStack> cargo,
        Dictionary<string, decimal>? estimatedPrices)
    {
        if (cargo == null || cargo.Count == 0)
            return "";

        return string.Join("\n",
            cargo.Values
                 .OrderByDescending(c => c.Quantity)
                 .Select(c => FormatItemStackLine(c, estimatedPrices)));
    }

    private static string FormatItemStackLine(ItemStack stack, Dictionary<string, decimal>? estimatedPrices)
    {
        if (estimatedPrices != null &&
            estimatedPrices.TryGetValue(stack.ItemId, out var unitPrice) &&
            unitPrice > 0)
        {
            decimal stackTotal = unitPrice * stack.Quantity;
            return $"- `{stack.ItemId}` x{stack.Quantity} {FormatCredits(unitPrice)} ({FormatCredits(stackTotal)} stack total)";
        }

        return $"- `{stack.ItemId}` x{stack.Quantity}";
    }

    private static string FormatCredits(decimal amount)
    {
        return $"{Math.Round(amount, 2):0.##}cr";
    }

    private static string FormatPoiResources(PoiResourceInfo[] resources)
    {
        if (resources == null || resources.Length == 0)
            return "(none)";

        var lines = resources
            .Where(r => !string.IsNullOrWhiteSpace(r.ResourceId))
            .Take(5)
            .Select(r =>
            {
                string richness = r.Richness.HasValue
                    ? r.Richness.Value.ToString()
                    : (string.IsNullOrWhiteSpace(r.RichnessText) ? "?" : r.RichnessText);
                string remaining = !string.IsNullOrWhiteSpace(r.RemainingDisplay)
                    ? r.RemainingDisplay
                    : (r.Remaining.HasValue ? r.Remaining.Value.ToString() : "?");
                return $"{r.ResourceId} (richness: {richness}, remaining: {remaining})";
            })
            .ToList();

        return lines.Count == 0 ? "(none)" : string.Join("; ", lines);
    }

    internal Dictionary<string, decimal> BuildEstimatedItemPrices()
    {
        var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);

        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Ship.Cargo.Keys)
            itemIds.Add(item);
        foreach (var item in StorageItems.Keys)
            itemIds.Add(item);

        foreach (var itemId in itemIds)
        {
            if (Docked)
            {
                decimal? localBuyOnly = EstimateFromLocalBuyOrders(itemId);
                if (localBuyOnly.HasValue && localBuyOnly.Value > 0m)
                {
                    prices[itemId] = localBuyOnly.Value;
                    continue;
                }

                decimal? localLowestAsk = EstimateFromLocalLowestAsk(itemId);
                if (localLowestAsk.HasValue && localLowestAsk.Value > 0m)
                    prices[itemId] = localLowestAsk.Value;

                continue;
            }

            decimal? estimate = EstimateFromLocalMarket(itemId);
            if (estimate.HasValue && estimate.Value > 0m)
            {
                prices[itemId] = estimate.Value;
                continue;
            }

            if (Galaxy.Market.GlobalMedianBuyPrices.TryGetValue(itemId, out var fallbackBuy) && fallbackBuy > 0m)
            {
                prices[itemId] = fallbackBuy;
                continue;
            }

            if (Galaxy.Market.GlobalMedianSellPrices.TryGetValue(itemId, out var fallbackSell) && fallbackSell > 0m)
            {
                prices[itemId] = fallbackSell;
            }
        }

        return prices;
    }

    private decimal? EstimateFromLocalBuyOrders(string itemId)
    {
        if (CurrentMarket == null)
            return null;

        CurrentMarket.BuyOrders.TryGetValue(itemId, out var bids);
        if (bids != null && bids.Count > 0)
        {
            decimal? highestBid = bids
                .Where(o => o.Quantity > 0 && o.PriceEach > 0)
                .Select(o => (decimal?)o.PriceEach)
                .DefaultIfEmpty(null)
                .Max();

            if (highestBid.HasValue && highestBid.Value > 0m)
                return highestBid;
        }

        return null;
    }

    private decimal? EstimateFromLocalLowestAsk(string itemId)
    {
        if (CurrentMarket == null)
            return null;

        if (!CurrentMarket.SellOrders.TryGetValue(itemId, out var asks) ||
            asks == null ||
            asks.Count == 0)
        {
            return null;
        }

        decimal? lowestAsk = asks
            .Where(o => o.Quantity > 0 && o.PriceEach > 0)
            .Select(o => (decimal?)o.PriceEach)
            .DefaultIfEmpty(null)
            .Min();

        if (lowestAsk.HasValue && lowestAsk.Value > 0m)
            return lowestAsk;

        return null;
    }

    private decimal? EstimateFromLocalMarket(string itemId)
    {
        if (CurrentMarket == null)
            return null;

        CurrentMarket.BuyOrders.TryGetValue(itemId, out var bids);
        decimal? bidMedian = ComputeMedianPriceFromOrders(bids);
        if (bidMedian.HasValue && bidMedian.Value > 0m)
            return bidMedian;

        if (CurrentMarket.SellOrders.TryGetValue(itemId, out var asks) &&
            asks != null &&
            asks.Count > 0)
        {
            decimal? lowestAsk = asks
                .Where(o => o.Quantity > 0 && o.PriceEach > 0)
                .Select(o => (decimal?)o.PriceEach)
                .DefaultIfEmpty(null)
                .Min();

            if (lowestAsk.HasValue && lowestAsk.Value > 0m)
                return lowestAsk;
        }

        return null;
    }

    private static decimal? ComputeMedianPriceFromOrders(List<MarketOrder>? orders)
    {
        if (orders == null || orders.Count == 0)
            return null;

        var expanded = new List<decimal>();
        foreach (var order in orders.Where(o => o.Quantity > 0 && o.PriceEach > 0))
        {
            for (int i = 0; i < order.Quantity; i++)
                expanded.Add(order.PriceEach);
        }

        if (expanded.Count == 0)
            return null;

        expanded.Sort();
        int n = expanded.Count;
        int mid = n / 2;

        if (n % 2 == 1)
            return expanded[mid];

        return (expanded[mid - 1] + expanded[mid]) / 2m;
    }

    private static string FormatEconomyDeals(EconomyDeal[] deals)
    {
        if (deals == null || deals.Length == 0)
            return "- _(no profitable cached cross-station deals yet)_";

        return string.Join("\n", deals.Select(d =>
            $"- `{d.ItemId}` buy `{d.BuyStationId}` @ {d.BuyPrice:0.##} -> sell `{d.SellStationId}` @ {d.SellPrice:0.##} (+{d.ProfitPerUnit:0.##}/unit)"));
    }

    internal static string FormatEconomy(EconomyDeal[] deals, OpenOrderInfo[] ownBuyOrders, OpenOrderInfo[] ownSellOrders)
    {
        return
$@"Best Cross-Station Deals:
{FormatEconomyDeals(deals)}

Your Open Buy Orders:
{FormatOpenOrders(ownBuyOrders)}

Your Open Sell Orders:
{FormatOpenOrders(ownSellOrders)}";
    }

    private static string FormatOpenOrders(OpenOrderInfo[] orders)
    {
        if (orders == null || orders.Length == 0)
            return "- _(none)_";

        return string.Join("\n", orders
            .OrderByDescending(o => o.PriceEach)
            .ThenByDescending(o => o.Quantity)
            .Take(8)
            .Select(o => string.IsNullOrWhiteSpace(o.OrderId)
                ? $"- `{o.ItemId}` @ {o.PriceEach:0.##} (qty {o.Quantity})"
                : $"- `{o.ItemId}` @ {o.PriceEach:0.##} (qty {o.Quantity}) [order `{o.OrderId}`]"));
    }

    internal static string FormatMissions(MissionInfo[] missions)
    {
        if (missions == null || missions.Length == 0)
            return "";

        return string.Join("\n", missions.Select(FormatMission));
    }

    private static string FormatMission(MissionInfo mission)
    {
        var headerParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(mission.Id))
            headerParts.Add($"`{mission.Id}`");
        if (!string.IsNullOrWhiteSpace(mission.Title))
            headerParts.Add(mission.Title);
        if (!string.IsNullOrWhiteSpace(mission.Type))
            headerParts.Add($"type `{mission.Type}`");
        if (mission.Difficulty.HasValue)
            headerParts.Add($"difficulty {mission.Difficulty.Value}");

        if (mission.Completed)
            headerParts.Add("✅");

        if (headerParts.Count == 0)
            headerParts.Add("mission");

        var lines = new List<string>
        {
            $"- {string.Join(" | ", headerParts.Where(p => !string.IsNullOrWhiteSpace(p)))}"
        };

        if (!string.IsNullOrWhiteSpace(mission.Description))
            lines.Add($"  desc: {mission.Description}");

        if (mission.ExpiresInTicks.HasValue)
            lines.Add($"  expires_in_ticks: {mission.ExpiresInTicks.Value}");

        if (!string.IsNullOrWhiteSpace(mission.AcceptedAt))
            lines.Add($"  accepted_at: {mission.AcceptedAt}");

        if (!string.IsNullOrWhiteSpace(mission.TemplateId))
            lines.Add($"  template_id: `{mission.TemplateId}`");

        if (!string.IsNullOrWhiteSpace(mission.MissionId))
            lines.Add($"  mission_id: `{mission.MissionId}`");

        if (!string.IsNullOrWhiteSpace(mission.IssuingBase))
            lines.Add($"  issuing_base: `{mission.IssuingBase}`");

        if (!string.IsNullOrWhiteSpace(mission.GiverName) || !string.IsNullOrWhiteSpace(mission.GiverTitle))
            lines.Add($"  giver: {mission.GiverName}{(string.IsNullOrWhiteSpace(mission.GiverTitle) ? "" : $" ({mission.GiverTitle})")}");

        if (mission.Repeatable.HasValue)
            lines.Add($"  repeatable: {mission.Repeatable.Value}");

        if (!string.IsNullOrWhiteSpace(mission.FactionId))
            lines.Add($"  faction_id: `{mission.FactionId}`");

        if (!string.IsNullOrWhiteSpace(mission.FactionName))
            lines.Add($"  faction_name: {mission.FactionName}");

        if (!string.IsNullOrWhiteSpace(mission.ChainNext))
            lines.Add($"  chain_next: `{mission.ChainNext}`");

        if (!string.IsNullOrWhiteSpace(mission.ProgressText))
            lines.Add($"  progress_text: {mission.ProgressText}");

        if (!string.IsNullOrWhiteSpace(mission.ProgressSummary))
            lines.Add($"  progress: {mission.ProgressSummary}");

        if (!string.IsNullOrWhiteSpace(mission.ObjectivesSummary))
            lines.Add($"  objectives: {mission.ObjectivesSummary}");

        if (!string.IsNullOrWhiteSpace(mission.RequirementsSummary))
            lines.Add($"  requirements: {mission.RequirementsSummary}");

        if (!string.IsNullOrWhiteSpace(mission.RewardsSummary))
            lines.Add($"  rewards: {mission.RewardsSummary}");

        return string.Join("\n", lines);
    }

    internal static string FormatShipyardShowroom(ShipyardShowroomEntry[] entries)
    {
        if (entries == null || entries.Length == 0)
            return "- _(no showroom listings)_";

        return string.Join(
            "\n",
            entries.Select(entry =>
            {
                string name = string.IsNullOrWhiteSpace(entry.Name) ? "ship" : entry.Name;
                string classId = string.IsNullOrWhiteSpace(entry.ShipClassId) ? "-" : entry.ShipClassId;
                string stats = $"Hull {entry.Hull?.ToString() ?? "-"} | Shield {entry.Shield?.ToString() ?? "-"} | Cargo {entry.Cargo?.ToString() ?? "-"} | Speed {entry.Speed?.ToString() ?? "-"}";
                string price = entry.Price.HasValue ? $"@ {Math.Round(entry.Price.Value, 2):0.##}cr" : "-";
                return $"- `{name}` ({classId}) | {stats} | {price}";
            }));
    }

    internal static string FormatShipyardListings(ShipyardListingEntry[] entries)
    {
        if (entries == null || entries.Length == 0)
            return "- _(no player listings)_";

        return string.Join(
            "\n",
            entries.Select(entry =>
            {
                string listingId = string.IsNullOrWhiteSpace(entry.ListingId) ? "-" : entry.ListingId;
                string name = string.IsNullOrWhiteSpace(entry.Name) ? "ship" : entry.Name;
                string classId = string.IsNullOrWhiteSpace(entry.ClassId) ? "-" : entry.ClassId;
                string price = entry.Price.HasValue ? $"@ {Math.Round(entry.Price.Value, 2):0.##}cr" : "-";
                return $"- `{listingId}`: `{name}` ({classId}) | {price}";
            }));
    }

    internal static string FormatOwnedShips(OwnedShipInfo[] ships)
    {
        if (ships == null || ships.Length == 0)
            return "- _(none)_";

        return string.Join("\n", ships.Select(s =>
        {
            string active = s.IsActive ? " | ACTIVE" : "";
            string location = string.IsNullOrWhiteSpace(s.Location) ? "" : $" | {s.Location}";
            string classId = string.IsNullOrWhiteSpace(s.ClassId) ? "-" : s.ClassId;
            return $"- `{s.ShipId}` ({classId}){active}{location}";
        }));
    }

    internal static string FormatCatalogueEntries(CatalogueEntry[] entries)
    {
        if (entries == null || entries.Length == 0)
            return "- _(no catalog entries)_";

        return string.Join("\n", entries.Select(e =>
        {
            string id = !string.IsNullOrWhiteSpace(e.Id)
                ? e.Id
                : (!string.IsNullOrWhiteSpace(e.ClassId) ? e.ClassId : "-");
            string name = string.IsNullOrWhiteSpace(e.Name) ? id : e.Name;
            string shipClass = !string.IsNullOrWhiteSpace(e.ClassId)
                ? e.ClassId
                : (string.IsNullOrWhiteSpace(e.Class) ? "-" : e.Class);
            string category = string.IsNullOrWhiteSpace(e.Category) ? "-" : e.Category;
            int? hull = e.Hull ?? e.BaseHull;
            int? shield = e.Shield ?? e.BaseShield;
            int? cargo = e.Cargo ?? e.CargoCapacity;
            int? speed = e.Speed ?? e.BaseSpeed;
            string price = e.Price.HasValue && e.Price.Value > 0
                ? $"@ {Math.Round(e.Price.Value, 2):0.##}cr"
                : "-";

            return $"- `{id}`: {name} | Class {shipClass} | {category} | T{e.Tier?.ToString() ?? "-"} | Scale {e.Scale?.ToString() ?? "-"} | Hull {hull?.ToString() ?? "-"} | Shield {shield?.ToString() ?? "-"} | Cargo {cargo?.ToString() ?? "-"} | Speed {speed?.ToString() ?? "-"} | {price}";
        }));
    }

    private static string FormatNotifications(GameNotification[] notifications)
    {
        if (notifications == null || notifications.Length == 0)
            return "";

        return string.Join("\n", notifications
            .TakeLast(10)
            .Select(n =>
            {
                if (!string.IsNullOrWhiteSpace(n.Summary))
                    return $"- `{n.Summary}`";

                if (!string.IsNullOrWhiteSpace(n.Type))
                    return $"- `{n.Type}`";

                return "- `notification`";
            }));
    }

    private static string FormatChat(GameChatMessage[] chatMessages)
    {
        if (chatMessages == null || chatMessages.Length == 0)
            return "";

        return string.Join("\n", chatMessages
            .TakeLast(5)
            .Select(m =>
            {
                string channelPrefix = string.IsNullOrWhiteSpace(m.Channel)
                    ? ""
                    : $"[{m.Channel}] ";
                string sender = string.IsNullOrWhiteSpace(m.Sender) ? "unknown" : m.Sender;
                string content = m.Content ?? "";

                return string.IsNullOrWhiteSpace(content)
                    ? $"- `{channelPrefix}{sender}`"
                    : $"- `{channelPrefix}{sender}: {content}`";
            }));
    }
}
