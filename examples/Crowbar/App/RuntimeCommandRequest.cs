public static class RuntimeCommandNames
{
    public const string SetScript = "set_script";
    public const string GenerateScript = "generate_script";
    public const string ExecuteScript = "execute_script";
    public const string Halt = "halt";
    public const string SaveExample = "save_example";
}

public sealed record RuntimeCommandRequest(
    string BotId,
    string Command,
    string? Argument = null);
