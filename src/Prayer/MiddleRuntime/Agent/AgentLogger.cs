using System;
using System.Text;
using System.Threading.Tasks;

public interface IAgentLogger
{
    Task LogCommandExecutionAsync(
        string controlModeName,
        string commandText,
        GameState state,
        string phase,
        string? details = null);

    Task LogPlannerPromptAsync(string stage, string prompt);
    Task LogPromptGenerationPairAsync(int attempt, int maxAttempts, string prompt, string generatedScript, bool parseSucceeded);

    Task LogScriptWriterContextTokensAsync(
        int attempt,
        int maxAttempts,
        string userInput,
        string stateContextBlock,
        string examplesBlock,
        string? previousScript,
        string? previousError,
        string fullPrompt);

    void LogScriptNormalization(string source, string inputScript, string outputScript);
    void LogAstWalker(string eventName, string detail);
    void LogScriptCommandFailure(string commandText, string failureMessage);
    void LogOverride(string eventName, string overrideName, string detail);
}

public sealed class FileAgentLogger : IAgentLogger
{
    public Task LogCommandExecutionAsync(
        string controlModeName,
        string commandText,
        GameState state,
        string phase,
        string? details = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === COMMAND_EXECUTION:{phase} ===");
        sb.AppendLine($"ControlMode: {controlModeName}");
        sb.AppendLine($"Command: {commandText}");

        if (!string.IsNullOrWhiteSpace(details))
            sb.AppendLine($"Details: {details}");

        sb.AppendLine("GameState:");
        sb.AppendLine(state.ToLLMMarkdown());
        sb.AppendLine();

        LogSink.Instance.Enqueue(new LogEvent(
            DateTime.UtcNow,
            LogKind.CommandExecution,
            sb.ToString(),
            AppPaths.CommandExecutionLogFile));

        return Task.CompletedTask;
    }

    public Task LogPlannerPromptAsync(string stage, string prompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === {stage} ===");
        sb.AppendLine(prompt ?? string.Empty);
        sb.AppendLine();

        LogSink.Instance.Enqueue(new LogEvent(
            DateTime.UtcNow,
            LogKind.PlannerPrompt,
            sb.ToString(),
            AppPaths.PlannerPromptLogFile));

        return Task.CompletedTask;
    }

    public Task LogPromptGenerationPairAsync(
        int attempt,
        int maxAttempts,
        string prompt,
        string generatedScript,
        bool parseSucceeded)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === prompt_generation_pair ===");
        sb.AppendLine($"attempt={attempt}/{maxAttempts}");
        sb.AppendLine($"parse_succeeded={parseSucceeded}");
        sb.AppendLine("PROMPT:");
        sb.AppendLine(prompt ?? string.Empty);
        sb.AppendLine("GENERATED_SCRIPT:");
        sb.AppendLine(generatedScript ?? string.Empty);
        sb.AppendLine();

        LogSink.Instance.Enqueue(new LogEvent(
            DateTime.UtcNow,
            LogKind.PromptGenerationPairs,
            sb.ToString(),
            AppPaths.PromptGenerationPairsLogFile));

        return Task.CompletedTask;
    }

    public Task LogScriptWriterContextTokensAsync(
        int attempt,
        int maxAttempts,
        string userInput,
        string stateContextBlock,
        string examplesBlock,
        string? previousScript,
        string? previousError,
        string fullPrompt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === script_writer_context ===");
        sb.AppendLine($"attempt={attempt}/{maxAttempts}");
        sb.AppendLine($"est_tokens.full_prompt={EstimateTokenCount(fullPrompt)}");
        sb.AppendLine($"est_tokens.user_input={EstimateTokenCount(userInput)}");
        sb.AppendLine($"est_tokens.state_context={EstimateTokenCount(stateContextBlock)}");
        sb.AppendLine($"est_tokens.examples={EstimateTokenCount(examplesBlock)}");
        sb.AppendLine($"est_tokens.previous_script={EstimateTokenCount(previousScript)}");
        sb.AppendLine($"est_tokens.previous_error={EstimateTokenCount(previousError)}");
        sb.AppendLine($"chars.full_prompt={(fullPrompt ?? string.Empty).Length}");
        sb.AppendLine($"chars.user_input={(userInput ?? string.Empty).Length}");
        sb.AppendLine($"chars.state_context={(stateContextBlock ?? string.Empty).Length}");
        sb.AppendLine($"chars.examples={(examplesBlock ?? string.Empty).Length}");
        sb.AppendLine($"chars.previous_script={(previousScript ?? string.Empty).Length}");
        sb.AppendLine($"chars.previous_error={(previousError ?? string.Empty).Length}");
        sb.AppendLine();

        LogSink.Instance.Enqueue(new LogEvent(
            DateTime.UtcNow,
            LogKind.ScriptWriterContext,
            sb.ToString(),
            AppPaths.ScriptWriterContextLogFile));

        return Task.CompletedTask;
    }

    public void LogScriptNormalization(string source, string inputScript, string outputScript)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === script_normalization ({source}) ===");
        sb.AppendLine("INPUT:");
        sb.AppendLine(inputScript ?? string.Empty);
        sb.AppendLine("OUTPUT:");
        sb.AppendLine(outputScript ?? string.Empty);
        sb.AppendLine();

        LogSink.Instance.Enqueue(new LogEvent(
            DateTime.UtcNow,
            LogKind.ScriptNormalization,
            sb.ToString(),
            AppPaths.ScriptNormalizationLogFile));
    }

    public void LogAstWalker(string eventName, string detail)
    {
        var line = $"[{DateTime.UtcNow:O}] event={eventName} detail={detail}{Environment.NewLine}";

        LogSink.Instance.Enqueue(new LogEvent(
            DateTime.UtcNow,
            LogKind.AstWalker,
            line,
            AppPaths.AstWalkerLogFile));
    }

    public void LogOverride(string eventName, string overrideName, string detail)
    {
        var line = $"[{DateTime.UtcNow:O}] event={eventName} override={overrideName} detail={detail}{Environment.NewLine}";

        LogSink.Instance.Enqueue(new LogEvent(
            DateTime.UtcNow,
            LogKind.Override,
            line,
            AppPaths.OverrideLogFile));
    }

    public void LogScriptCommandFailure(string commandText, string failureMessage)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.UtcNow:O}] === script_command_failure ===");
        sb.AppendLine($"COMMAND: {commandText}");
        sb.AppendLine($"REASON: {failureMessage}");
        sb.AppendLine();

        LogSink.Instance.Enqueue(new LogEvent(
            DateTime.UtcNow,
            LogKind.ScriptCommandFailure,
            sb.ToString(),
            AppPaths.ScriptCommandFailuresLogFile));
    }

    private static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        int nonWhitespaceChars = 0;
        foreach (char ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                nonWhitespaceChars++;
        }

        // Rough cross-model heuristic: ~1 token per 4 non-whitespace chars.
        return (int)Math.Ceiling(nonWhitespaceChars / 4d);
    }
}
