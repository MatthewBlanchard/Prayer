using System;
using System.Collections.Generic;
using System.Linq;

public static class CommandCatalog
{
    public static IReadOnlyList<ICommand> Space { get; } = new List<ICommand>
    {
        new MineCommand(),
        new SeekCommand(),
        new SurveyCommand(),
        new GoCommand(),
        new AcceptMissionCommand(),
        new AbandonMissionCommand(),
        new DockCommand(),
        new RepairCommand(),
        new HaltCommand(),
    };

    public static IReadOnlyList<ICommand> Trade { get; } = new List<ICommand>
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

    public static IReadOnlyList<ICommand> Hangar { get; } = new List<ICommand>
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

    public static IReadOnlyList<ICommand> Shipyard { get; } = new List<ICommand>
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

    public static IReadOnlyList<ICommand> ShipCatalog { get; } = new List<ICommand>
    {
        new GoCommand(),
        new NextPageCommand(),
        new LastPageCommand(),
        new ExitCommand(),
        new HaltCommand(),
    };

    public static IReadOnlyList<ICommand> All { get; } = Space
        .Concat(Trade)
        .Concat(Hangar)
        .Concat(Shipyard)
        .Concat(ShipCatalog)
        .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
        .Select(g => g.First())
        .ToList();
}
