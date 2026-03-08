using System;
using System.Collections.Generic;

public sealed record UiSnapshot(
    SpaceUiModel? SpaceModel,
    IReadOnlyList<string> SpaceConnectedSystems,
    TradeUiModel? TradeModel,
    ShipyardUiModel? ShipyardModel,
    CatalogUiModel? CatalogModel,
    IReadOnlyList<MissionPromptOption> ActiveMissionPrompts,
    IReadOnlyList<MissionPromptOption> AvailableMissionPrompts,
    IReadOnlyList<string> Memory,
    IReadOnlyList<string> ExecutionStatusLines,
    string? ControlInput,
    int? CurrentScriptLine,
    string? LastGenerationPrompt,
    int? CurrentTick,
    DateTime? LastSpaceMoltPostUtc,
    IReadOnlyList<BotTab> Bots,
    string? ActiveBotId,
    CraftingUiModel? CraftingModel = null
);
