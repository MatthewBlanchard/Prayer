public sealed record RuntimeHostSnapshot(
    bool IsHalted,
    bool HasActiveCommand,
    int? CurrentScriptLine,
    string? CurrentScript);
