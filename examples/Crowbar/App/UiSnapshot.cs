using System.Collections.Generic;

public sealed record UiSnapshot(
    string SpaceStateMarkdown,
    string? TradeStateMarkdown,
    string? ShipyardStateMarkdown,
    string? CantinaStateMarkdown,
    string? CatalogStateMarkdown,
    IReadOnlyList<MissionPromptOption> ActiveMissionPrompts,
    IReadOnlyList<string> Memory,
    IReadOnlyList<string> ExecutionStatusLines,
    string? ControlInput,
    int? CurrentScriptLine,
    string? LastGenerationPrompt,
    IReadOnlyList<BotTab> Bots,
    string? ActiveBotId,
    bool ActiveBotLoopEnabled
);
