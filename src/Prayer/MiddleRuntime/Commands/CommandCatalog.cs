using System;
using System.Collections.Generic;

public static class CommandCatalog
{
    // Used for DSL parsing/metadata only — do not use for command execution.
    public static IReadOnlyList<ICommand> All { get; } = CreateAll();

    public static IReadOnlyList<ICommand> CreateAll() => new List<ICommand>
    {
        new MineCommand(),
        new SurveyCommand(),
        new ExploreCommand(),
        new GoCommand(),
        new AcceptMissionCommand(),
        new AbandonMissionCommand(),
        new DockCommand(),
        new SetHomeCommand(),
        new RepairCommand(),
        new RefuelCommand(),
        new SellCommand(),
        new BuyCommand(),
        new CancelBuyCommand(),
        new CancelSellCommand(),
        new RetrieveCommand(),
        new StashCommand(),
        new SwitchShipCommand(),
        new InstallModCommand(),
        new UninstallModCommand(),
        new BuyShipCommand(),
        new BuyListedShipCommand(),
        new CommissionShipCommand(),
        new SellShipCommand(),
        new ListShipForSaleCommand(),
        new WaitCommand(),
        new CraftCommand(),
        new SelfDestructCommand(),
        new UseItemCommand(),
        new UndockCommand(),
        new AttackCommand(),
        new ScanCommand(),
        new ReloadCommand(),
        new JettisonCommand(),
    };
}
