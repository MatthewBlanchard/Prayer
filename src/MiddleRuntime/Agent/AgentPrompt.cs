public static class AgentPrompt
{
    public const string BaseSystemPrompt =
        "You are an autonomous agent playing the online game SpaceMolt. " +
        "Your objective is to pursue the active objective. " +
        "Make rational, goal-directed decisions based on the current game state. ";

    private static readonly string DslCommandReferenceBlock = DslParser.BuildPromptDslReferenceBlock();

    public static string BuildExecutorPrompt(
        string stateMarkdown,
        string availableActionsBlock,
        string selectedMove)
    {
        return
            "<|start_header_id|>system<|end_header_id|>\n" +
            "You are the command executor.\n" +
            "Convert the planner suggestion into exactly one valid command.\n" +
            "If the suggestion is invalid, choose the best command from the actions list.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n" +
            stateMarkdown + "\n\n" +
            availableActionsBlock +
            "Selected move:\n" + selectedMove + "\n\n" +
            "Repeat the selected move as a valid command." +
            "Return only one command.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n";
    }

    public static string BuildSuggestNextMovePrompt(
        string baseSystemPrompt,
        string stateMarkdown,
        string availableActionsBlock,
        string allActionsBlock,
        string currentObjectiveBlock,
        string previousActionsBlock)
    {
        return
            "<|start_header_id|>system<|end_header_id|>\n" +
            baseSystemPrompt +
            "You are the command selector.\n" +
            "Output exactly one command the agent can execute right now.\n" +
            "You must only output a command from the Available actions block.\n" +
            "Return only the command text, with no explanation.\n" +
            "Choose the command that most directly carries out the objective right now.\n" +
            "If no suitable command is already found or if the command would cause a loop and `halt` is available, output `halt`.\n" +
            "Use `go <poiId|systemId>` for movement to POIs or systems. You do not need to be able to see a system to go there.\n" +
            "`go` automatically pathfinds across connected systems; target does not need to be directly connected.\n" +
            "If the objective says to go to a destination, issue `go` to that destination even if it is not nearby or directly connected.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n" +
            stateMarkdown + "\n\n" +
            availableActionsBlock +
            allActionsBlock +
            previousActionsBlock +
            currentObjectiveBlock +
            "What is the single best immediate next command? Return ONLY a single command. Only use available commands.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n";
    }

    public static string BuildPlanPrompt(
        string baseSystemPrompt,
        string requestBlock,
        string stateMarkdown,
        string currentObjectiveBlock,
        string previousActionsBlock,
        string currentRationaleBlock,
        string objective)
    {
        return
            "<|start_header_id|>system<|end_header_id|>\n" +
            baseSystemPrompt +
            "Create a freeform rationale in plain English.\n" +
            "Rationale can include ideas, priorities, hypotheses, and next-step thoughts.\n" +
            "Keep it concise and practical.\n" +
            "Output one thought per line.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n" +
            requestBlock +
            DslCommandReferenceBlock +
            stateMarkdown + "\n\n" +
            currentObjectiveBlock +
            previousActionsBlock +
            currentRationaleBlock +
            objective + "\n" +
            "<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n";
    }

    public static string BuildObjectivePrompt(
        string baseSystemPrompt,
        string stateMarkdown,
        string allActionsBlock,
        string previousActionsBlock)
    {
        return
            "<|start_header_id|>system<|end_header_id|>\n" +
            baseSystemPrompt +
            "Define a single high-level objective in plain English.\n" +
            "Objective should be stable for multiple steps and reflect long-term value.\n" +
            "Return exactly one concise line.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n" +
            stateMarkdown + "\n\n" +
            allActionsBlock +
            previousActionsBlock +
            "What is the current objective?\n" +
            "<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n";
    }

    public static string BuildObjectiveFromUserInputPrompt(
        string baseSystemPrompt,
        string userInput,
        string stateMarkdown)
    {
        return
            "<|start_header_id|>system<|end_header_id|>\n" +
            baseSystemPrompt +
            "Turn the user request into one high-level objective.\n" +
            "Objective should preserve user intent and remain stable across several steps.\n" +
            "Return exactly one concise line.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>user<|end_header_id|>\n" +
            "User request:\n" + userInput + "\n\n" +
            stateMarkdown + "\n\n" +
            "What is the objective?\n" +
            "<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n";
    }

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
            baseSystemPrompt +
            "You write DSL scripts for this game agent.\n" +
            "Output only DSL script text. No markdown fences and no explanation.\n" +
            "Terminate every command with a semicolon (;).\n" +
            "Use only the DSL syntax implied by the examples.\n" +
            "Keep scripts short, deterministic, and directly aligned to the user request.\n" +
            "Prefer explicit movement steps with go <identifier>; before follow-up actions when needed.\n" +
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
            "- blocks are allowed only as: repeat { ... }, if <BOOLEAN> { ... }, until <BOOLEAN> { ... }\n" +
            "- no markdown fence\n" +
            "Return only the script text.\n" +
            "<|eot_id|>" +
            "<|start_header_id|>assistant<|end_header_id|>\n";
    }
}
