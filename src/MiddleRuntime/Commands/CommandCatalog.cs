using System;
using System.Collections.Generic;

public static class CommandCatalog
{
    public static IReadOnlyList<ICommand> All { get; } = new List<ICommand>
    {
        new MineCommand(),
        new SurveyCommand(),
        new GoCommand(),
        new AcceptMissionCommand(),
        new AbandonMissionCommand(),
        new DockCommand(),
        new RepairCommand(),
        new SellCommand(),
        new BuyCommand(),
        new CancelBuyCommand(),
        new CancelSellCommand(),
        new WithdrawItemsCommand(),
        new DepositItemsCommand(),
        new SwitchShipCommand(),
        new InstallModCommand(),
        new UninstallModCommand(),
        new BuyShipCommand(),
        new BuyListedShipCommand(),
        new CommissionQuoteCommand(),
        new CommissionShipCommand(),
        new CommissionStatusCommand(),
        new SellShipCommand(),
        new ListShipForSaleCommand(),
        new ExitCommand(),
    };
}
