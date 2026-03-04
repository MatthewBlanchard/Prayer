using System;
using System.Collections.Generic;
using System.Linq;

// =====================================================
// GAME STATE
// =====================================================

public class GameState
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

    public string System { get; set; } = "";
    public POIInfo CurrentPOI { get; set; } = null!;

    public POIInfo[] POIs { get; set; } = Array.Empty<POIInfo>();
    public string[] Systems { get; set; } = Array.Empty<string>();
    public GalaxyState Galaxy { get; set; } = new();
    public int StorageCredits { get; set; }
    public Dictionary<string, ItemStack> StorageItems { get; set; } = new();
    public EconomyDeal[] EconomyDeals { get; set; } = Array.Empty<EconomyDeal>();
    public OpenOrderInfo[] OwnBuyOrders { get; set; } = Array.Empty<OpenOrderInfo>();
    public OpenOrderInfo[] OwnSellOrders { get; set; } = Array.Empty<OpenOrderInfo>();
    public Dictionary<string, ItemStack> Cargo { get; set; }
        = new Dictionary<string, ItemStack>();

    public string ShipName { get; set; } = "";
    public string ShipClassId { get; set; } = "";
    public int Armor { get; set; }
    public int Speed { get; set; }
    public int CpuUsed { get; set; }
    public int CpuCapacity { get; set; }
    public int PowerUsed { get; set; }
    public int PowerCapacity { get; set; }
    public int ModuleCount { get; set; }

    public int Fuel { get; set; }
    public int MaxFuel { get; set; }
    public int Credits { get; set; }
    public bool Docked { get; set; }
    public string[] ShipyardShowroomLines { get; set; } = Array.Empty<string>();
    public string[] ShipyardListingLines { get; set; } = Array.Empty<string>();
    public Catalogue ShipCatalogue { get; set; } = new();
    public OwnedShipInfo[] OwnedShips { get; set; } = Array.Empty<OwnedShipInfo>();

    public int Hull { get; set; }
    public int MaxHull { get; set; }
    public int Shield { get; set; }
    public int MaxShield { get; set; }

    public MissionInfo[] ActiveMissions { get; set; } = Array.Empty<MissionInfo>();
    public MissionInfo[] AvailableMissions { get; set; } = Array.Empty<MissionInfo>();

    public int CargoUsed { get; set; }
    public int CargoCapacity { get; set; }
    public GameNotification[] Notifications { get; set; } = Array.Empty<GameNotification>();
    public GameChatMessage[] ChatMessages { get; set; } = Array.Empty<GameChatMessage>();
    public MarketState? CurrentMarket
    {
        get
        {
            if (!Docked || !CurrentPOI.IsStation)
                return null;

            if (Galaxy?.Market?.MarketsByStation == null)
                return null;

            return Galaxy.Market.MarketsByStation.TryGetValue(CurrentPOI.Id, out var market)
                ? market
                : null;
        }
    }

    public int GetQuantity(string itemId)
    {
        return Cargo.TryGetValue(itemId, out var stack)
            ? stack.Quantity
            : 0;
    }

    public bool HasItem(string itemId, int minQuantity = 1)
    {
        return GetQuantity(itemId) >= minQuantity;
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

        int fuelPct = MaxFuel > 0 ? (Fuel * 100) / MaxFuel : 0;
        int hullPct = MaxHull > 0 ? (Hull * 100) / MaxHull : 0;
        int shieldPct = MaxShield > 0 ? (Shield * 100) / MaxShield : 0;
        int cargoFree = Math.Max(0, CargoCapacity - CargoUsed);
        int cargoPct = CargoCapacity > 0 ? (CargoUsed * 100) / CargoCapacity : 0;
        string poiDockState = CurrentPOI.HasBase
            ? (Docked ? " DOCKED" : " DOCKABLE")
            : "";

        var poisMarkdown = FormatPOIs(POIs);
        var systemsMarkdown = FormatList(Systems);
        var cargoMarkdown = FormatCargo(Cargo, estimatedPrices);
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
        var cargo = FormatCargo(Cargo, estimatedPrices);
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
Fuel: {Fuel}/{MaxFuel}
Cargo: {CargoUsed}/{CargoCapacity}

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
        var cargo = FormatCargo(Cargo, estimatedPrices);

        return
$@"
Active Context: `ShipYardState`
Current Station: `{CurrentPOI.Id}`
Credits: {Credits}
Fuel: {Fuel}/{MaxFuel}
Cargo: {CargoUsed}/{CargoCapacity}

### Showroom
{FormatShipyardShowroomLines(ShipyardShowroomLines)}

### Player Listings
{FormatShipyardShowroomLines(ShipyardListingLines)}

### Cargo
{cargo}
{BuildNotificationsLlmSection()}
";
    }

    internal string RenderHangarLlmMarkdown()
    {
        var estimatedPrices = BuildEstimatedItemPrices();
        var cargo = FormatCargo(Cargo, estimatedPrices);

        return
$@"
Active Context: `HangarState`
Current Station: `{CurrentPOI.Id}`
Credits: {Credits}
Fuel: {Fuel}/{MaxFuel}
Cargo: {CargoUsed}/{CargoCapacity}

### Active Ship Stats
Armor: {Armor}
Speed: {Speed}
CPU: {CpuUsed}/{CpuCapacity}
Power: {PowerUsed}/{PowerCapacity}
Modules: {ModuleCount}

### Owned Ships
{FormatOwnedShips(OwnedShips)}

### Cargo
{cargo}
{BuildNotificationsLlmSection()}
";
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
        return
$@"
Current System (your location): `{System}`
Current POI (your location): `{CurrentPOI.Id}` ({CurrentPOI.Type}){r.PoiDockState}
Credits: {Credits}

### Ship
Name: {(string.IsNullOrWhiteSpace(ShipName) ? "-" : ShipName)}
Class: {(string.IsNullOrWhiteSpace(ShipClassId) ? "-" : ShipClassId)}
Fuel: {Fuel}/{MaxFuel} ({r.FuelPct}%)
Hull: {Hull}/{MaxHull} ({r.HullPct}%)
Shield: {Shield}/{MaxShield} ({r.ShieldPct}%)
Cargo: {CargoUsed}/{CargoCapacity} ({r.CargoPct}% used, {r.CargoFree} free)
Current POI Online: {CurrentPOI.Online}

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
        string stationCreditsLine = Docked
            ? $"\nSTATION CREDITS: {StorageCredits}"
            : "";

        return
$@"SYSTEM: {System}
POI: {CurrentPOI.Id} ({CurrentPOI.Type}){r.PoiDockState}
CREDITS: {Credits}
{stationCreditsLine}

SHIP
- Name: {(string.IsNullOrWhiteSpace(ShipName) ? "-" : ShipName)}
- Class: {(string.IsNullOrWhiteSpace(ShipClassId) ? "-" : ShipClassId)}
- Fuel: {Fuel}/{MaxFuel} ({r.FuelPct}%)
- Hull: {Hull}/{MaxHull} ({r.HullPct}%)
- Shield: {Shield}/{MaxShield} ({r.ShieldPct}%)
- Cargo: {CargoUsed}/{CargoCapacity} ({r.CargoPct}% used, {r.CargoFree} free)
- POI Online: {CurrentPOI.Online}

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
            $"- `{p.Id}` ({p.Type})" +
            (p.HasBase ? " | Dockable" : "") +
            (p.IsMiningTarget ? " | Mining" : "")
        ));
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

    internal Dictionary<string, decimal> BuildEstimatedItemPrices()
    {
        var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);

        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Cargo.Keys)
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

    internal static string FormatShipyardShowroomLines(string[] lines)
    {
        if (lines == null || lines.Length == 0)
            return "- _(no showroom listings)_";

        return string.Join("\n", lines.Select(l => "- " + l));
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

public class GalaxyMapSnapshot
{
    public List<GalaxySystemInfo> Systems { get; set; } = new();
    public List<GalaxyKnownPoiInfo> KnownPois { get; set; } = new();
}

public class GalaxySystemInfo
{
    public string Id { get; set; } = "";
    public string Empire { get; set; } = "";
    public double? X { get; set; }
    public double? Y { get; set; }
    public List<string> Connections { get; set; } = new();
    public List<GalaxyPoiInfo> Pois { get; set; } = new();
}

public class GalaxyPoiInfo
{
    public string Id { get; set; } = "";
}

public class GalaxyKnownPoiInfo
{
    public string Id { get; set; } = "";
    public string SystemId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool HasBase { get; set; }
    public string? BaseId { get; set; }
    public string? BaseName { get; set; }
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
}

public class MarketState
{
    public string StationId { get; set; } = "";

    public Dictionary<string, List<MarketOrder>> SellOrders { get; set; }
        = new();

    public Dictionary<string, List<MarketOrder>> BuyOrders { get; set; }
        = new();
}

public class MarketOrder
{
    public string ItemId { get; set; } = "";
    public decimal PriceEach { get; set; }
    public int Quantity { get; set; }
}

public class StationInfo
{
    public string StationId { get; set; } = "";
    public int StorageCredits { get; set; }
    public Dictionary<string, ItemStack> StorageItems { get; set; } = new();
    public MarketState? Market { get; set; }
    public List<OpenOrderInfo> BuyOrders { get; set; } = new();
    public List<OpenOrderInfo> SellOrders { get; set; } = new();
    public string[] ShipyardShowroomLines { get; set; } = Array.Empty<string>();
    public string[] ShipyardListingLines { get; set; } = Array.Empty<string>();
}

public class MarketCacheSnapshot
{
    public string StationId { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public MarketState? Market { get; set; }
}

public class ShipyardCacheSnapshot
{
    public string StationId { get; set; } = "";
    public DateTime CapturedAtUtc { get; set; }
    public string[] ShowroomLines { get; set; } = Array.Empty<string>();
    public string[] ListingLines { get; set; } = Array.Empty<string>();
}

public class EconomyDeal
{
    public string ItemId { get; set; } = "";
    public string BuyStationId { get; set; } = "";
    public decimal BuyPrice { get; set; }
    public string SellStationId { get; set; } = "";
    public decimal SellPrice { get; set; }
    public decimal ProfitPerUnit { get; set; }
}

public class OpenOrderInfo
{
    public string OrderId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public decimal PriceEach { get; set; }
    public int Quantity { get; set; }
}

public class OwnedShipInfo
{
    public string ShipId { get; set; } = "";
    public string ClassId { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsActive { get; set; }
}

public class ItemStack
{
    public string ItemId { get; }
    public int Quantity { get; private set; }

    public ItemStack(string itemId, int quantity)
    {
        ItemId = itemId;
        Quantity = quantity;
    }

    public void Add(int amount)
    {
        if (amount > 0)
            Quantity += amount;
    }

    public void Remove(int amount)
    {
        if (amount <= 0) return;

        Quantity -= amount;
        if (Quantity < 0)
            Quantity = 0;
    }

    public override string ToString()
    {
        return $"{ItemId} x{Quantity}";
    }
}

public class POIInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool HasBase { get; set; }
    public string? BaseId { get; set; }
    public string? BaseName { get; set; }
    public int Online { get; set; }

    public bool IsMiningTarget =>
        Type == "asteroid_belt" ||
        Type == "asteroid" ||
        Type == "gas_cloud" ||
        Type == "ice_field";

    public bool IsStation => Type == "station";
}

public class MissionInfo
{
    public string Id { get; set; } = "";
    public string MissionId { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Description { get; set; } = "";
    public string ProgressText { get; set; } = "";
    public bool Completed { get; set; }
    public int? Difficulty { get; set; }
    public int? ExpiresInTicks { get; set; }
    public string AcceptedAt { get; set; } = "";
    public string IssuingBase { get; set; } = "";
    public string GiverName { get; set; } = "";
    public string GiverTitle { get; set; } = "";
    public bool? Repeatable { get; set; }
    public string FactionId { get; set; } = "";
    public string FactionName { get; set; } = "";
    public string ChainNext { get; set; } = "";
    public string ObjectivesSummary { get; set; } = "";
    public string ProgressSummary { get; set; } = "";
    public string RequirementsSummary { get; set; } = "";
    public string RewardsSummary { get; set; } = "";
}

public class GameNotification
{
    public string Type { get; set; } = "";
    public string Summary { get; set; } = "";
    public string PayloadJson { get; set; } = "";
}

public class GameChatMessage
{
    public string MessageId { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Sender { get; set; } = "";
    public string Content { get; set; } = "";
    public int SeenTick { get; set; }
}
