using System.Collections.Generic;

public sealed record UiSnapshot(
    IReadOnlyDictionary<string, BotStateEntry> BotStates,
    IReadOnlyList<BotRouteOverlay> BotRoutes,
    IReadOnlyList<BotTab> Bots,
    IReadOnlyList<BotMapMarker> BotMapMarkers,
    string? DefaultBotId
);
