using System.Threading.Tasks;

public interface IRuntimeEngine
{
    bool IsHalted { get; }
    bool HasActiveCommand { get; }
    int? CurrentScriptLine { get; }
    string? CurrentScript { get; }

    string SetScript(string script, GameState? state = null);
    void ActivateScriptControl();
    bool InterruptActiveCommand(string reason = "Interrupted");
    void Halt(string reason = "Halted");
    void ResumeFromHalt(string reason = "Resumed");

    Task<RuntimeExecutionResult?> ExecuteAsync(
        IRuntimeTransport transport,
        CommandResult result,
        GameState state);

    Task<CommandResult?> DecideAsync(GameState state);
}
