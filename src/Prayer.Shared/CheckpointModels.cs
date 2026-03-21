using System;
using System.Collections.Generic;

public sealed class CommandResult
{
    public string Action { get; set; } = string.Empty;
    public string? Arg1 { get; set; }
    public string? Arg2 { get; set; }
    public int? Quantity { get; set; }
    public int? SourceLine { get; set; }
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
}

public sealed class ActionMemoryCheckpoint
{
    public string Action { get; set; } = string.Empty;
    public string? Arg1 { get; set; }
    public int? Quantity { get; set; }
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
