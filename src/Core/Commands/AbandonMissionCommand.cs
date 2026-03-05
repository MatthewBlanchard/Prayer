using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class AbandonMissionCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "abandon_mission";
    protected override bool RequiresStation => true;
    public DslCommandSyntax GetDslSyntax() => new(
        DslArgKind.Any,
        ArgRequired: true);

    protected override bool IsAvailableWhenDocked(GameState state)
    {
        return state.Docked &&
               state.CurrentPOI.IsStation &&
               state.ActiveMissions.Any(m => !string.IsNullOrWhiteSpace(m.Id));
    }

    public override string BuildHelp(GameState state)
        => "- abandon_mission <missionId> → abandon an active mission";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var missionId = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(missionId))
            return new CommandExecutionResult { ResultMessage = "Usage: abandon_mission <missionId>." };

        JsonElement response = (await client.ExecuteCommandAsync(
            "abandon_mission",
            new { mission_id = missionId })).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Abandoned mission {missionId}."
        };
    }
}
