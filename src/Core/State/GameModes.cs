using System.Collections.Generic;
using System.Linq;

public abstract class GameContextMode
{
    public abstract string Name { get; }

    public abstract IReadOnlyList<ICommand> GetCommands();
    public abstract string ToLlmMarkdown(GameState state);
    public abstract string ToDisplayText(GameState state);

    public virtual string BuildActionsBlock(
        GameState state,
        bool onlyAvailable)
    {
        var commands = GetCommands();
        var help = commands
            .Where(c => !onlyAvailable || c.IsAvailable(state))
            .Select(c => c.BuildHelp(state))
            .ToList();

        if (help.Count == 0)
            return onlyAvailable
                ? "Available actions:\n- none\n\n"
                : "All actions:\n- none\n\n";

        var heading = onlyAvailable ? "Available actions:" : "All actions:";
        return heading + "\n" + string.Join("\n", help) + "\n\n";
    }

}

public sealed class SpaceContextMode : GameContextMode
{
    public static SpaceContextMode Instance { get; } = new();
    private readonly IReadOnlyList<ICommand> _commands;

    private SpaceContextMode()
    {
        _commands = new List<ICommand>
        {
            new MineCommand(),
            new GoCommand(),
            new AcceptMissionCommand(),
            new AbandonMissionCommand(),
            new DockCommand(),
            new RepairCommand(),
            new HaltCommand(),
        };
    }

    public override string Name => "SpaceState";

    public override IReadOnlyList<ICommand> GetCommands() => _commands;
    public override string ToLlmMarkdown(GameState state) => state.RenderSpaceLlmMarkdown();
    public override string ToDisplayText(GameState state)
    {
        int fuelPct = state.MaxFuel > 0 ? (state.Fuel * 100) / state.MaxFuel : 0;
        int hullPct = state.MaxHull > 0 ? (state.Hull * 100) / state.MaxHull : 0;
        int shieldPct = state.MaxShield > 0 ? (state.Shield * 100) / state.MaxShield : 0;
        int cargoFree = state.CargoCapacity - state.CargoUsed;
        if (cargoFree < 0)
            cargoFree = 0;
        int cargoPct = state.CargoCapacity > 0 ? (state.CargoUsed * 100) / state.CargoCapacity : 0;
        string poiDockState = state.CurrentPOI.HasBase
            ? (state.Docked ? " DOCKED" : " DOCKABLE")
            : "";
        var prices = state.BuildEstimatedItemPrices();
        string pois = GameState.StripMarkdown(GameState.FormatPOIs(state.POIs));
        string systems = GameState.StripMarkdown(GameState.FormatList(state.Systems));
        string cargo = GameState.StripMarkdown(GameState.FormatCargo(state.Cargo, prices));
        string storageSection = state.Docked && state.Shared.StorageItems != null && state.Shared.StorageItems.Count > 0
            ? GameState.StripMarkdown($"\n### Storage Items\n{GameState.FormatCargo(state.Shared.StorageItems, prices)}\n")
            : "";
        string stationCreditsLine = state.Docked
            ? $"\nSTATION CREDITS: {state.Shared.StorageCredits}"
            : "";

        return
$@"SYSTEM: {state.System}
POI: {state.CurrentPOI.Id} ({state.CurrentPOI.Type}){poiDockState}
CREDITS: {state.Credits}
{stationCreditsLine}

SHIP
- Name: {(string.IsNullOrWhiteSpace(state.ShipName) ? "-" : state.ShipName)}
- Class: {(string.IsNullOrWhiteSpace(state.ShipClassId) ? "-" : state.ShipClassId)}
- Fuel: {state.Fuel}/{state.MaxFuel} ({fuelPct}%)
- Hull: {state.Hull}/{state.MaxHull} ({hullPct}%)
- Shield: {state.Shield}/{state.MaxShield} ({shieldPct}%)
- Cargo: {state.CargoUsed}/{state.CargoCapacity} ({cargoPct}% used, {cargoFree} free)
- POI Online: {state.CurrentPOI.Online}

POIS
{pois}

CONNECTED SYSTEMS
{systems}

CARGO
{cargo}
{storageSection}{state.BuildChatDisplaySection()}{state.BuildNotificationsDisplaySection()}";
    }
}

public sealed class TradeContextMode : GameContextMode
{
    public static TradeContextMode Instance { get; } = new();
    private readonly IReadOnlyList<ICommand> _commands;

    private TradeContextMode()
    {
        _commands = new List<ICommand>
        {
            new GoCommand(),
            new SellCommand(),
            new BuyCommand(),
            new CancelBuyCommand(),
            new CancelSellCommand(),
            new AcceptMissionCommand(),
            new AbandonMissionCommand(),
            new WithdrawItemsCommand(),
            new DepositItemsCommand(),
            new ExitCommand(),
            new HaltCommand(),
        };
    }

    public override string Name => "TradeState";

    public override IReadOnlyList<ICommand> GetCommands() => _commands;
    public override string ToLlmMarkdown(GameState state) => state.RenderTradeLlmMarkdown();
    public override string ToDisplayText(GameState state)
    {
        var prices = state.BuildEstimatedItemPrices();
        var cargo = GameState.StripMarkdown(GameState.FormatCargo(state.Cargo, prices));
        var storage = state.Shared.StorageItems != null && state.Shared.StorageItems.Count > 0
            ? GameState.StripMarkdown(GameState.FormatCargo(state.Shared.StorageItems, prices))
            : "";
        var economy = GameState.StripMarkdown(
            GameState.FormatEconomy(state.Shared.EconomyDeals, state.Shared.OwnBuyOrders, state.Shared.OwnSellOrders));
        var storageSection = string.IsNullOrWhiteSpace(storage)
            ? ""
            : $"\nSTORAGE\n{storage}\n";

        return
$@"CONTEXT: TRADE TERMINAL
STATION: {state.CurrentPOI.Id}
CREDITS: {state.Credits}
STATION CREDITS: {state.Shared.StorageCredits}
FUEL: {state.Fuel}/{state.MaxFuel}
CARGO: {state.CargoUsed}/{state.CargoCapacity}

CARGO ITEMS
{cargo}
{storageSection}
ECONOMY
{economy}{state.BuildNotificationsDisplaySection()}";
    }
}

public sealed class HangarContextMode : GameContextMode
{
    public static HangarContextMode Instance { get; } = new();
    private readonly IReadOnlyList<ICommand> _commands;

    private HangarContextMode()
    {
        _commands = new List<ICommand>
        {
            new GoCommand(),
            new SwitchShipCommand(),
            new InstallModCommand(),
            new UninstallModCommand(),
            new AcceptMissionCommand(),
            new AbandonMissionCommand(),
            new ExitCommand(),
            new HaltCommand(),
        };
    }

    public override string Name => "HangarState";

    public override IReadOnlyList<ICommand> GetCommands() => _commands;
    public override string ToLlmMarkdown(GameState state) => state.RenderHangarLlmMarkdown();
    public override string ToDisplayText(GameState state)
    {
        var prices = state.BuildEstimatedItemPrices();
        var cargo = GameState.StripMarkdown(GameState.FormatCargo(state.Cargo, prices));
        var ownedShips = GameState.StripMarkdown(GameState.FormatOwnedShips(state.OwnedShips));

        return
$@"CONTEXT: HANGAR
STATION: {state.CurrentPOI.Id}
CREDITS: {state.Credits}
STATION CREDITS: {state.Shared.StorageCredits}
FUEL: {state.Fuel}/{state.MaxFuel}
CARGO: {state.CargoUsed}/{state.CargoCapacity}

ACTIVE SHIP STATS
- Armor: {state.Armor}
- Speed: {state.Speed}
- CPU: {state.CpuUsed}/{state.CpuCapacity}
- Power: {state.PowerUsed}/{state.PowerCapacity}
- Modules: {state.ModuleCount}

OWNED SHIPS
{ownedShips}

CARGO ITEMS
{cargo}{state.BuildNotificationsDisplaySection()}";
    }
}

public sealed class ShipyardContextMode : GameContextMode
{
    public static ShipyardContextMode Instance { get; } = new();
    private readonly IReadOnlyList<ICommand> _commands;

    private ShipyardContextMode()
    {
        _commands = new List<ICommand>
        {
            new GoCommand(),
            new BuyShipCommand(),
            new BuyListedShipCommand(),
            new CommissionQuoteCommand(),
            new CommissionShipCommand(),
            new CommissionStatusCommand(),
            new AcceptMissionCommand(),
            new AbandonMissionCommand(),
            new SellShipCommand(),
            new ListShipForSaleCommand(),
            new ShipCatalogCommand(),
            new ExitCommand(),
            new HaltCommand(),
        };
    }

    public override string Name => "ShipYardState";

    public override IReadOnlyList<ICommand> GetCommands() => _commands;
    public override string ToLlmMarkdown(GameState state) => state.RenderShipyardLlmMarkdown();
    public override string ToDisplayText(GameState state)
    {
        var prices = state.BuildEstimatedItemPrices();
        var cargo = GameState.StripMarkdown(GameState.FormatCargo(state.Cargo, prices));
        var showroom = GameState.StripMarkdown(GameState.FormatShipyardShowroomLines(state.ShipyardShowroomLines));
        var listings = GameState.StripMarkdown(GameState.FormatShipyardShowroomLines(state.ShipyardListingLines));

        return
$@"CONTEXT: SHIPYARD
STATION: {state.CurrentPOI.Id}
CREDITS: {state.Credits}
STATION CREDITS: {state.Shared.StorageCredits}
FUEL: {state.Fuel}/{state.MaxFuel}
CARGO: {state.CargoUsed}/{state.CargoCapacity}

SHOWROOM
{showroom}

PLAYER LISTINGS
{listings}

CARGO ITEMS
{cargo}{state.BuildNotificationsDisplaySection()}";
    }
}

public sealed class ShipCatalogContextMode : GameContextMode
{
    public static ShipCatalogContextMode Instance { get; } = new();
    private readonly IReadOnlyList<ICommand> _commands;

    private ShipCatalogContextMode()
    {
        _commands = new List<ICommand>
        {
            new GoCommand(),
            new NextPageCommand(),
            new LastPageCommand(),
            new ExitCommand(),
            new HaltCommand(),
        };
    }

    public override string Name => "ShipCatalogState";

    public override IReadOnlyList<ICommand> GetCommands() => _commands;
    public override string ToLlmMarkdown(GameState state) => state.RenderShipCatalogLlmMarkdown();
    public override string ToDisplayText(GameState state)
    {
        int currentPage = state.ShipCatalogue.Page ?? 1;
        int totalPages = state.ShipCatalogue.TotalPages ?? 1;
        int totalItems = state.ShipCatalogue.Total ?? state.ShipCatalogue.TotalItems ?? 0;
        var entries = state.ShipCatalogue.NormalizedEntries;
        var renderedEntries = GameState.StripMarkdown(GameState.FormatCatalogueEntries(entries));

        return
$@"CONTEXT: SHIP CATALOG
STATION: {state.CurrentPOI.Id}
PAGE: {currentPage}/{totalPages}
ENTRIES ON PAGE: {entries.Length}
TOTAL SHIPS: {totalItems}

SHIPS
{renderedEntries}{state.BuildNotificationsDisplaySection()}";
    }
}
