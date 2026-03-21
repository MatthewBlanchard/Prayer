using System;
using System.Threading.Tasks;

public class WaitCommand : ISingleTurnCommand, IDslCommandGrammar
{
    private const int SecondsPerTick = 10;
    private const int DefaultTicks = 1;
    private const int MaxTicks = 30;

    public string Name => "wait";

    public bool IsAvailable(GameState state) => true;

    public string BuildHelp(GameState state)
        => "- wait [ticks] -> pause script execution (1 tick = 10s, default 1 tick)";

    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(
                DslArgKind.Integer,
                Required: false,
                DefaultValue: DefaultTicks.ToString())
        });

    public async Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult cmd,
        GameState state)
    {
        int ticks = DefaultTicks;
        if (!string.IsNullOrWhiteSpace(cmd.Arg1) &&
            int.TryParse(cmd.Arg1, out var parsed))
        {
            ticks = parsed;
        }

        if (ticks < 0)
            ticks = 0;
        if (ticks > MaxTicks)
            ticks = MaxTicks;

        int seconds = ticks * SecondsPerTick;
        await Task.Delay(TimeSpan.FromSeconds(seconds));

        return new CommandExecutionResult
        {
            ResultMessage = $"Waited {ticks} tick(s) ({seconds}s)."
        };
    }
}
