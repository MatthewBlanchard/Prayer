using System;
using System.Collections.Generic;

public sealed class CommandResult
{
    public string Action { get; set; } = string.Empty;
    public List<string> Args { get; set; } = new();
    public string? Arg1
    {
        get => Args.Count > 0 ? Args[0] : null;
        set => SetArgAt(0, value);
    }
    public int? Quantity
    {
        get => Args.Count > 1 && int.TryParse(Args[1], out var n) ? n : null;
        set => SetArgAt(1, value?.ToString());
    }
    public int? SourceLine { get; set; }

    private void SetArgAt(int index, string? value)
    {
        while (Args.Count <= index)
            Args.Add(string.Empty);

        Args[index] = value ?? string.Empty;

        for (int i = Args.Count - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(Args[i]))
                break;
            Args.RemoveAt(i);
        }
    }
}

public sealed record CommandExecutionCheckpoint
{
    public int Version { get; init; } = 1;
    public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;
    public string Script { get; init; } = string.Empty;
    public bool IsHalted { get; init; }
    public int? CurrentScriptLine { get; init; }
    public bool HadActiveCommand { get; init; }
    public CommandResult? ActiveCommandResult { get; init; }
    public IReadOnlyList<ActionMemoryCheckpoint> Memory { get; init; } = Array.Empty<ActionMemoryCheckpoint>();
    public IReadOnlyList<CommandResult> RequeuedSteps { get; init; } = Array.Empty<CommandResult>();
    public IReadOnlyList<ExecutionFrameCheckpoint> Frames { get; init; } = Array.Empty<ExecutionFrameCheckpoint>();
    public Dictionary<string, int> MinedByItem { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> StashedByItem { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ActionMemoryCheckpoint
{
    public string Action { get; set; } = string.Empty;
    public List<string> Args { get; set; } = new();
    public string? ResultMessage { get; set; }
}

public sealed class ExecutionFrameCheckpoint
{
    public string Kind { get; set; } = string.Empty;
    public int SourceLine { get; set; }
    public int Index { get; set; }
    public string? UntilCondition { get; set; }
    public bool UntilConditionKnown { get; set; }
    public string Path { get; set; } = string.Empty;
}
