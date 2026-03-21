public enum RuntimeHaltRequestKind
{
    UserHalt,
    ScriptRestart
}

public readonly record struct RuntimeHaltRequest(RuntimeHaltRequestKind Kind);
