using System;
using System.Text.Json;
using System.Threading.Tasks;

public class InstallModCommand : AutoDockSingleTurnCommand
{
    public override string Name => "install_mod";
    protected override bool RequiresStation => true;

    protected override bool IsAvailableWhenDocked(GameState state)
        => state.Docked && state.CurrentPOI.IsStation;
    public override string BuildHelp(GameState state)
        => "- install_mod <moduleId> → install a module on your active ship";

    protected override async Task<CommandExecutionResult?> ExecuteDockedAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        var moduleId = cmd.Arg1?.Trim();
        if (string.IsNullOrWhiteSpace(moduleId))
            return new CommandExecutionResult { ResultMessage = "Usage: install_mod <moduleId>." };

        JsonElement response = (await client.ExecuteCommandAsync("install_mod", new { module_id = moduleId })).Payload;
        return new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response) ?? $"Installed module {moduleId}."
        };
    }
}
