using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;

public sealed class UiSnapshotPublisher
{
    private readonly ChannelWriter<UiSnapshot> _uiWriter;
    private readonly Func<IReadOnlyList<BotSession>> _getAllBots;
    private readonly Func<string?> _getDefaultBotId;
    private readonly Func<IReadOnlyList<BotMapMarker>> _getBotMapMarkers;
    private readonly Func<IReadOnlyList<BotRouteOverlay>> _getBotRoutes;
    private readonly Action<string> _logAuth;
    private string _lastLoggedBotTabSignature = "";

    public UiSnapshotPublisher(
        ChannelWriter<UiSnapshot> uiWriter,
        Func<IReadOnlyList<BotSession>> getAllBots,
        Func<string?> getDefaultBotId,
        Func<IReadOnlyList<BotMapMarker>> getBotMapMarkers,
        Func<IReadOnlyList<BotRouteOverlay>> getBotRoutes,
        Action<string> logAuth)
    {
        _uiWriter = uiWriter;
        _getAllBots = getAllBots;
        _getDefaultBotId = getDefaultBotId;
        _getBotMapMarkers = getBotMapMarkers;
        _getBotRoutes = getBotRoutes;
        _logAuth = logAuth;
    }

    public void LogBotTabsIfChanged(string context)
    {
        var bots = _getAllBots();
        var defaultBotId = _getDefaultBotId();
        var labels = string.Join(",", bots.Select(b => b.Label));
        var signature = $"{bots.Count}|{defaultBotId}|{labels}";
        if (signature == _lastLoggedBotTabSignature)
            return;

        _lastLoggedBotTabSignature = signature;
        _logAuth(
            $"{context} | tabs_changed | count={bots.Count} | default={defaultBotId ?? "(null)"} | labels=[{labels}]");
    }

    public void PublishSnapshot()
    {
        var sessions = _getAllBots();
        var defaultBotId = _getDefaultBotId();
        var tabs = sessions.Select(s => new BotTab(s.Id, s.Label, s.ColorHex)).ToList();

        var botStates = new Dictionary<string, BotStateEntry>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            var entry = BuildBotStateEntry(session);
            botStates[session.Id] = entry;
        }

        LogBotTabsIfChanged("publish_snapshot");

        _uiWriter.TryWrite(new UiSnapshot(
            botStates,
            _getBotRoutes(),
            tabs,
            _getBotMapMarkers(),
            defaultBotId));
    }

    private static BotStateEntry BuildBotStateEntry(BotSession session)
    {
        var prayerState = session.LastPrayerState;
        if (prayerState?.State == null)
        {
            return new BotStateEntry(
                BotId: session.Id,
                SpaceModel: null,
                SpaceConnectedSystems: Array.Empty<string>(),
                TradeModel: null,
                ShipyardModel: null,
                CatalogModel: null,
                ActiveMissionPrompts: Array.Empty<MissionPromptOption>(),
                AvailableMissionPrompts: Array.Empty<MissionPromptOption>(),
                Memory: prayerState?.Memory ?? Array.Empty<string>(),
                ExecutionStatusLines: prayerState?.ExecutionStatusLines ?? Array.Empty<string>(),
                ControlInput: prayerState?.ControlInput,
                CurrentScriptLine: prayerState?.CurrentScriptLine,
                LastGenerationPrompt: prayerState?.LastGenerationPrompt,
                CurrentTick: prayerState?.CurrentTick,
                LastSpaceMoltPostUtc: prayerState?.LastSpaceMoltPostUtc,
                ActiveRoute: prayerState?.ActiveRoute,
                CraftingModel: null,
                SkillsModel: null);
        }

        var uiState = AppUiStateBuilder.BuildUiState(prayerState.State);

        return new BotStateEntry(
            BotId: session.Id,
            SpaceModel: uiState.SpaceModel,
            SpaceConnectedSystems: prayerState.State.Systems ?? Array.Empty<string>(),
            TradeModel: uiState.TradeModel,
            ShipyardModel: uiState.ShipyardModel,
            CatalogModel: uiState.CatalogModel,
            ActiveMissionPrompts: MissionPromptBuilder.BuildOptions(prayerState.State),
            AvailableMissionPrompts: MissionPromptBuilder.BuildAvailableOptions(prayerState.State),
            Memory: prayerState.Memory,
            ExecutionStatusLines: prayerState.ExecutionStatusLines,
            ControlInput: prayerState.ControlInput,
            CurrentScriptLine: prayerState.CurrentScriptLine,
            LastGenerationPrompt: prayerState.LastGenerationPrompt,
            CurrentTick: prayerState.CurrentTick,
            LastSpaceMoltPostUtc: prayerState.LastSpaceMoltPostUtc,
            ActiveRoute: prayerState.ActiveRoute,
            CraftingModel: uiState.CraftingModel,
            SkillsModel: uiState.SkillsModel);
    }
}
