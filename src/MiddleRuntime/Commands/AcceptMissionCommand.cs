using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class AcceptMissionCommand : AutoDockSingleTurnCommand, IDslCommandGrammar
{
    public override string Name => "accept_mission";
    protected override bool RequiresStation => true;
    public DslCommandSyntax GetDslSyntax() => new(
        DslArgKind.Any,
        ArgRequired: true);

    protected override bool IsAvailableWhenDocked(GameState state)
    {
        return state.Docked &&
               state.CurrentPOI.IsStation &&
               state.AvailableMissions.Any(m => !string.IsNullOrWhiteSpace(m.Id));
    }

    public override string BuildHelp(GameState state)
        => "- accept_mission <missionId> → accept a mission from the board";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var missionId = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(missionId))
            return new CommandExecutionResult { ResultMessage = "Usage: accept_mission <missionId>." };

        JsonElement response = (await client.ExecuteCommandAsync(
            "accept_mission",
            new { mission_id = missionId })).Payload;

        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Accepted mission {missionId}."
        };
    }
}
