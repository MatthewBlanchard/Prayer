using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class StashCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "stash";
    public override DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(
                DslArgKind.Item,
                Required: false)
        });

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.Ship.Cargo.Count > 0;

    public override string BuildHelp(GameState state)
        => "- stash [itemId] → move one stack, or stash all cargo when omitted";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (!state.Docked)
            return null;

        if (string.IsNullOrWhiteSpace(cmd.Arg1))
        {
            var cargoItems = state.Ship.Cargo
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

        if (!state.Ship.Cargo.TryGetValue(cmd.Arg1, out var stack) || stack.Quantity <= 0)
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
