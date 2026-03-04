using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class RefuelCommand : AutoDockSingleTurnCommand
{
    public override string Name => "refuel";

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.Credits > 0;
    public override string BuildHelp(GameState state)
        => "- refuel";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        SpaceMoltHttpClient client,
        CommandResult cmd,
        GameState state)
    {
        JsonElement response = await client.ExecuteAsync("refuel", new { });

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        };
    }
}

// =====================================================
// TRAVEL
// =====================================================
