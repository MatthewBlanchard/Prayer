using System.Collections.Generic;

public sealed record UiSnapshot(
    string SpaceStateMarkdown,
    string? TradeStateMarkdown,
    string? ShipyardStateMarkdown,
    string? CantinaStateMarkdown,
    IReadOnlyList<MissionPromptOption> ActiveMissionPrompts,
    IReadOnlyList<string> Memory,
    IReadOnlyList<string> ExecutionStatusLines,
    string? ControlInput,
    int? CurrentScriptLine,
    ControlModeKind Mode,
    IReadOnlyList<string> Actions,
    string? LastGenerationPrompt,
    IReadOnlyList<BotTab> Bots,
    string? ActiveBotId
);
