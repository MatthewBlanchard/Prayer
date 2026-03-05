using System.Text.Json;
using System.Threading.Tasks;

public class CommissionStatusCommand : AutoDockSingleTurnCommand
{
    public override string Name => "commission_status";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- commission_status → view ship commission progress";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        JsonElement response = (await client.ExecuteCommandAsync("commission_status")).Payload;

        int count = 0;
        if (response.ValueKind == JsonValueKind.Object &&
            response.TryGetProperty("commissions", out var commissions) &&
            commissions.ValueKind == JsonValueKind.Array)
        {
            count = commissions.GetArrayLength();
        }

        return new CommandExecutionResult
        {
            ResultMessage = count > 0
                ? $"You have {count} active commission(s)."
                : (CommandJson.TryGetResultMessage(response) ?? "No active commissions.")
        };
    }
}
