using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class DepositItemsCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "stash";
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(
                DslArgKind.Item | DslArgKind.Enum,
                Required: true,
                EnumType: "cargo_keyword")
        });

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.Cargo.Count > 0;

    public override string BuildHelp(GameState state)
        => "- stash <itemId|cargo> → move one stack or dump all cargo to storage";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (!state.Docked || string.IsNullOrWhiteSpace(cmd.Arg1))
            return null;

        if (string.Equals(cmd.Arg1, "cargo", StringComparison.OrdinalIgnoreCase))
        {
            var cargoItems = state.Cargo
                .Where(kvp => kvp.Value.Quantity > 0)
                .ToList();

            if (cargoItems.Count == 0)
            {
                return new CommandExecutionResult
                {
                    ResultMessage = "No cargo to deposit."
                };
            }

            int depositedCount = 0;
            string? lastMessage = null;

            foreach (var (itemId, cargoStack) in cargoItems)
            {
                JsonElement depositResponse = (await client.ExecuteCommandAsync(
                    "deposit_items",
                    new
                    {
                        item_id = itemId,
                        quantity = cargoStack.Quantity
                    })).Payload;

                lastMessage = CommandJson.TryGetResultMessage(depositResponse);
                if (CommandJson.TryGetError(depositResponse, out _, out _))
                {
                    return new CommandExecutionResult
                    {
                        ResultMessage = $"Deposit cargo stopped on {itemId}: {lastMessage}"
                    };
                }

                depositedCount++;
            }

            return new CommandExecutionResult
            {
                ResultMessage = depositedCount == cargoItems.Count
                    ? $"Deposited all cargo stacks ({depositedCount} item types)."
                    : (lastMessage ?? "Finished depositing cargo.")
            };
        }

        if (!state.Cargo.TryGetValue(cmd.Arg1, out var stack) || stack.Quantity <= 0)
            return null;

        JsonElement response = (await client.ExecuteCommandAsync(
            "deposit_items",
            new
            {
                item_id = cmd.Arg1,
                quantity = stack.Quantity
            })).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        };
    }
}
