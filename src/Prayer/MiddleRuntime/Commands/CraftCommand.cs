using System;
using System.Text.Json;
using System.Threading.Tasks;

public class CraftCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "craft";
    protected override bool RequiresStation => true;

    public override DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(DslArgKind.Any, Required: true),
            new DslArgumentSpec(DslArgKind.Integer, Required: false, DefaultValue: "1"),
        });

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.AvailableRecipes.Length > 0;

    public override string BuildHelp(GameState state)
        => "- craft <recipe_id> <count?> → craft an item at this station (batch up to 10)";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        if (string.IsNullOrWhiteSpace(cmd.Arg1))
            return new CommandExecutionResult { ResultMessage = "No recipe_id provided." };

        int quantity = Math.Clamp(cmd.Quantity ?? 1, 1, 10);

        JsonElement response = (await client.ExecuteCommandAsync(
            "craft",
            new
            {
                recipe_id = cmd.Arg1,
                quantity
            })).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        };
    }
}
