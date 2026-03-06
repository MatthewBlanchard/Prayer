using System.Collections.Generic;

public sealed record UiSnapshot(
    string SpaceStateMarkdown,
    IReadOnlyList<string> SpaceConnectedSystems,
    string? TradeStateMarkdown,
    string? ShipyardStateMarkdown,
    string? MissionsStateMarkdown,
    string? CatalogStateMarkdown,
    IReadOnlyList<MissionPromptOption> ActiveMissionPrompts,
    IReadOnlyList<MissionPromptOption> AvailableMissionPrompts,
    IReadOnlyList<string> Memory,
    IReadOnlyList<string> ExecutionStatusLines,
    string? ControlInput,
    int? CurrentScriptLine,
    string? LastGenerationPrompt,
    IReadOnlyList<BotTab> Bots,
    string? ActiveBotId
);
