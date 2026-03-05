using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class RepairCommand : AutoDockSingleTurnCommand
{
    public override string Name => "repair";

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked &&
           state.Hull < state.MaxHull;
    public override string BuildHelp(GameState state)
        => "- repair → restore hull";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        JsonElement response = (await client.ExecuteCommandAsync("repair")).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        };
    }
}
