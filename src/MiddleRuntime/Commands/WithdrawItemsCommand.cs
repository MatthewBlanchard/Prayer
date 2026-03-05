using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class WithdrawItemsCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "retrieve";
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(DslArgKind.Item, Required: true),
            new DslArgumentSpec(DslArgKind.Integer, Required: false),
        });

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked &&
           state.StorageItems.Count > 0 &&
           state.CargoUsed < state.CargoCapacity;

    public override string BuildHelp(GameState state)
        => "- retrieve <itemId> [quantity:int] → move item from storage to cargo";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (!state.Docked || string.IsNullOrWhiteSpace(cmd.Arg1))
            return null;

        if (!state.StorageItems.TryGetValue(cmd.Arg1, out var stack) || stack.Quantity <= 0)
            return null;

        int cargoFree = Math.Max(0, state.CargoCapacity - state.CargoUsed);
        if (cargoFree <= 0)
        {
            return new CommandExecutionResult
            {
                ResultMessage = "Cargo full."
            };
        }

        int requested = cmd.Quantity ?? stack.Quantity;
        if (requested <= 0)
            requested = 1;

        int quantity = Math.Min(requested, Math.Min(stack.Quantity, cargoFree));
        JsonElement response = default;

        while (quantity > 0)
        {
            response = (await client.ExecuteCommandAsync(
                "withdraw_items",
                new
                {
                    item_id = cmd.Arg1,
                    quantity = quantity
                })).Payload;

            if (!CommandJson.TryGetError(response, out var code, out _))
                break;

            bool noCargoSpace = string.Equals(code, "no_cargo_space", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(code, "cargo_full", StringComparison.OrdinalIgnoreCase);

            if (!noCargoSpace)
                break;

            if (quantity == 1)
                break;

            // Item sizes may vary; back off and retry.
            quantity = Math.Max(1, quantity / 2);
        }

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        };
    }
}

// =====================================================
// WARNINGS
// =====================================================
