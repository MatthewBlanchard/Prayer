using System;
using System.IO;
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
}

public sealed class FileAgentLogger : IAgentLogger
{
    public async Task LogCommandExecutionAsync(
        string controlModeName,
        string commandText,
        GameState state,
        string phase,
        string? details = null)
    {
        try
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

            await File.AppendAllTextAsync(AppPaths.CommandExecutionLogFile, sb.ToString());
        }
        catch
        {
            // Never fail command execution due to logging errors.
        }
    }

    public async Task LogPlannerPromptAsync(string stage, string prompt)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:O}] === {stage} ===");
            sb.AppendLine(prompt ?? string.Empty);
            sb.AppendLine();

            await File.AppendAllTextAsync(AppPaths.PlannerPromptLogFile, sb.ToString());
        }
        catch
        {
            // Prompt logging should never block gameplay.
        }
    }

    public async Task LogScriptWriterContextTokensAsync(
        int attempt,
        int maxAttempts,
        string userInput,
        string stateContextBlock,
        string examplesBlock,
        string? previousScript,
        string? previousError,
        string fullPrompt)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
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

            await File.AppendAllTextAsync(AppPaths.ScriptWriterContextLogFile, sb.ToString());
        }
        catch
        {
            // Context metrics logging should never block script generation.
        }
    }

    public void LogScriptNormalization(string source, string inputScript, string outputScript)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:O}] === script_normalization ({source}) ===");
            sb.AppendLine("INPUT:");
            sb.AppendLine(inputScript ?? string.Empty);
            sb.AppendLine("OUTPUT:");
            sb.AppendLine(outputScript ?? string.Empty);
            sb.AppendLine();
            File.AppendAllText(AppPaths.ScriptNormalizationLogFile, sb.ToString());
        }
        catch
        {
            // Script normalization logging should never block gameplay.
        }
    }

    public void LogAstWalker(string eventName, string detail)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var line =
                $"[{DateTime.UtcNow:O}] event={eventName} detail={detail}{Environment.NewLine}";
            File.AppendAllText(AppPaths.AstWalkerLogFile, line);
        }
        catch
        {
            // AST walker logging should never block execution.
        }
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
