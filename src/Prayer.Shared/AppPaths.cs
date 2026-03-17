using System.IO;
using System.Linq;
using System.Text;

public static class AppPaths
{
    public const string LogDir = "log";
    public const string CacheDir = "cache";
    public const string MarketsDir = "markets";
    public static readonly string ShipyardsDir = Path.Combine(CacheDir, "shipyards");
    public static readonly string CatalogsDir = Path.Combine(CacheDir, "catalogs");

    public static readonly string LlmLogFile = Path.Combine(LogDir, "llm.log");
    public static readonly string PlannerPromptLogFile = Path.Combine(LogDir, "planner_prompts.log");
    public static readonly string OpenAiErrorsLogFile = Path.Combine(LogDir, "openai_errors.log");
    public static readonly string HttpBadRequestLogFile = Path.Combine(LogDir, "http_badrequest.log");
    public static readonly string PathfindLogFile = Path.Combine(LogDir, "pathfind.log");
    public static readonly string SpaceMoltApiLogFile = Path.Combine(LogDir, "spacemolt_api.log");
    public static readonly string SpaceMoltApiStatsLogFile = Path.Combine(LogDir, "spacemolt_api_stats.log");
    public static readonly string AuthFlowLogFile = Path.Combine(LogDir, "auth_flow.log");
    public static readonly string AnalyzeMarketLogFile = Path.Combine(LogDir, "analyze_market.log");
    public static readonly string ItemCatalogLogFile = Path.Combine(LogDir, "item_catalog.log");
    public static readonly string CommandExecutionLogFile = Path.Combine(LogDir, "command_execution.log");
    public static readonly string ScriptNormalizationLogFile = Path.Combine(LogDir, "script_normalization.log");
    public static readonly string ScriptWriterContextLogFile = Path.Combine(LogDir, "script_writer_context.log");
    public static readonly string PromptGenerationPairsLogFile = Path.Combine(LogDir, "prompt_generation_pairs.log");
    public static readonly string AstWalkerLogFile = Path.Combine(LogDir, "ast_walker.log");
    public static readonly string ScriptCommandFailuresLogFile = Path.Combine(LogDir, "script_command_failures.log");
    public static readonly string RuntimeHostLogFile = Path.Combine(LogDir, "runtime_host.log");
    public static readonly string AutonomousGenerationLogFile = Path.Combine(LogDir, "autonomous_generation.log");
    public static readonly string GoArgValidationLogFile = Path.Combine(LogDir, "go_arg_validation.log");
    public static readonly string GoArgValidationMapDumpLogFile = Path.Combine(LogDir, "go_arg_validation_mapdump.log");
    public static readonly string UiHttpErrorLogFile = Path.Combine(LogDir, "ui_http_errors.log");
    public static readonly string UiHttpTraceLogFile = Path.Combine(LogDir, "ui_http_trace.log");
    public static readonly string ScriptGenerationExamplesFile = Path.Combine(CacheDir, "script_generation_examples.json");
    public static readonly string SeedScriptGenerationExamplesFile = Path.Combine("seed", "script_generation_examples.json");
    public static readonly string SavedBotsFile = Path.Combine(CacheDir, "saved_bots.json");
    public static readonly string SavedLlmSelectionFile = Path.Combine(CacheDir, "saved_llm_selection.json");
    public static readonly string SpaceMoltSessionsFile = Path.Combine(CacheDir, "spacemolt_sessions.json");
    public static readonly string ItemCatalogByIdCacheFile = Path.Combine(CacheDir, "item_catalog_by_id.json");
    public static readonly string ShipCatalogByIdCacheFile = Path.Combine(CacheDir, "ship_catalog_by_id.json");
    public static readonly string RecipeCatalogByIdCacheFile = Path.Combine(CacheDir, "recipe_catalog_by_id.json");
    public static readonly string ExplorationGalaxyStateFile = Path.Combine(CacheDir, "exploration_galaxy_state.json");
    public static readonly string AgentCheckpointsDir = Path.Combine(CacheDir, "agent_checkpoints");

    public static readonly string GalaxyMapFile = Path.Combine(CacheDir, "galaxy_map.json");
    public static readonly string GalaxyKnownPoisFile = Path.Combine(CacheDir, "known_pois.json");

    private static readonly string[] DebugLogsToResetOnStartup =
    {
        LlmLogFile,
        PlannerPromptLogFile,
        PathfindLogFile,
        SpaceMoltApiLogFile,
        CommandExecutionLogFile,
        ScriptNormalizationLogFile,
        ScriptWriterContextLogFile,
        PromptGenerationPairsLogFile,
        AstWalkerLogFile,
        ScriptCommandFailuresLogFile,
        RuntimeHostLogFile,
        AutonomousGenerationLogFile,
        GoArgValidationLogFile,
        GoArgValidationMapDumpLogFile
    };

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(AgentCheckpointsDir);
        Directory.CreateDirectory(MarketsDir);
        Directory.CreateDirectory(ShipyardsDir);
        Directory.CreateDirectory(CatalogsDir);
    }

    public static void ResetDebugLogsOnStartup()
    {
        foreach (var path in DebugLogsToResetOnStartup)
        {
            try
            {
                File.WriteAllText(path, string.Empty);
            }
            catch
            {
                // Startup log reset is best-effort.
            }
        }
    }

    public static string GetAgentCheckpointFile(string botLabel)
    {
        var normalized = string.IsNullOrWhiteSpace(botLabel)
            ? "default"
            : botLabel.Trim().ToLowerInvariant();

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            builder.Append(invalid.Contains(ch) ? '_' : ch);

        var safeName = builder.ToString();
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "default";

        return Path.Combine(AgentCheckpointsDir, $"{safeName}.json");
    }
}
