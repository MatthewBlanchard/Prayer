public static class AgentPrompt
{
    public const string BaseSystemPrompt =
        "You are an autonomous agent playing the online game SpaceMolt. " +
        "Pursue the user objective with short, deterministic DSL scripts. " +
        "Avoid redundant movement and setup steps. " +
        "Do not add dock before commands that can auto-dock. " +
        "Do not add go before mine; use mine or mine <resource_id> directly so runtime can resolve navigation.";

    private static readonly string DslCommandReferenceBlock = DslParser.BuildPromptDslReferenceBlock();

    public static string BuildScriptFromUserInputPrompt(
        string baseSystemPrompt,
        string userInput,
        string stateContextBlock,
        string examplesBlock,
        int attemptNumber = 1,
        string? previousScript = null,
        string? previousError = null)
    {
        var retryContext = string.IsNullOrWhiteSpace(previousError)
            ? ""
            :
                "Previous attempt failed.\n" +
                "Error:\n" + previousError.Trim() + "\n\n" +
                "Previous script:\n" + (previousScript ?? "") + "\n\n" +
                "Fix the script and return a corrected version.\n\n";

        return
            "<|start_header_id|>system<|end_header_id|>\n" +
            baseSystemPrompt + "\n" +
            "You write DSL scripts for this game agent.\n" +
            "Output only DSL script text. No markdown fences and no explanation.\n" +
            "Terminate every command with a semicolon (;).\n" +
            "Use only the DSL syntax implied by the examples.\n" +
            "Do not invent unsupported commands.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n" +
            "Attempt: " + attemptNumber + "\n\n" +
            "User request:\n" + userInput + "\n\n" +
            stateContextBlock + "\n\n" +
            DslCommandReferenceBlock +
            "Prompt -> script examples:\n" + examplesBlock + "\n\n" +
            retryContext +
            "Generate a DSL script now.\n" +
            "Checklist:\n" +
            "- every command ends with ;\n" +
            "- blocks are allowed only as: repeat { ... }, if <CONDITION> { ... }, until <CONDITION> { ... }\n" +
            "- avoid explicit dock unless user explicitly asks for dock\n" +
            "- avoid explicit go before mine; use mine or mine <resource_id>\n" +
            "- no markdown fence\n" +
            "Return only the script text.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n";
    }
}
