public enum LogKind
{
    LlmLog,
    LlmError,
    PlannerPrompt,
    HttpBadRequest,
    Pathfind,
    SpaceMoltApi,
    SpaceMoltApiStats,
    AuthFlow,
    AnalyzeMarket,
    ItemCatalog,
    CommandExecution,
    ScriptNormalization,
    ScriptWriterContext,
    PromptGenerationPairs,
    AstWalker,
    RuntimeHost,
    AutonomousGeneration,
    GoArgValidation,
    GoArgValidationMapDump,
    UiHttpError,
    UiHttpTrace,
    ScriptCommandFailure,
    Override
}

public sealed record LogEvent(DateTime TimestampUtc, LogKind Kind, string Message, string FilePath);
