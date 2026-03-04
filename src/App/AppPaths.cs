using System.IO;

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
    public static readonly string AuthFlowLogFile = Path.Combine(LogDir, "auth_flow.log");
    public static readonly string AnalyzeMarketLogFile = Path.Combine(LogDir, "analyze_market.log");
    public static readonly string ItemCatalogLogFile = Path.Combine(LogDir, "item_catalog.log");
    public static readonly string CommandExecutionLogFile = Path.Combine(LogDir, "command_execution.log");
    public static readonly string FuzzyMatchLogFile = Path.Combine(LogDir, "fuzzy_match.log");
    public static readonly string ScriptNormalizationLogFile = Path.Combine(LogDir, "script_normalization.log");
    public static readonly string ScriptWriterContextLogFile = Path.Combine(LogDir, "script_writer_context.log");
    public static readonly string ScriptGenerationExamplesFile = Path.Combine(CacheDir, "script_generation_examples.json");
    public static readonly string SavedBotsFile = Path.Combine(CacheDir, "saved_bots.json");
    public static readonly string ItemCatalogByIdCacheFile = Path.Combine(CacheDir, "item_catalog_by_id.json");
    public static readonly string ShipCatalogByIdCacheFile = Path.Combine(CacheDir, "ship_catalog_by_id.json");

    public static readonly string GalaxyMapFile = Path.Combine(CacheDir, "galaxy_map.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(MarketsDir);
        Directory.CreateDirectory(ShipyardsDir);
        Directory.CreateDirectory(CatalogsDir);
    }
}
