using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;

public sealed class UiSnapshotPublisher
{
    private readonly ChannelWriter<UiSnapshot> _uiWriter;
    private readonly Func<IReadOnlyList<BotTab>> _getBotTabs;
    private readonly Func<string?> _getActiveBotId;
    private readonly Func<BotSession?> _getActiveBot;
    private readonly Func<string?, IReadOnlyList<string>> _getExecutionStatusLinesForBot;
    private readonly Action<string> _logAuth;
    private string _lastLoggedBotTabSignature = "";

    public UiSnapshotPublisher(
        ChannelWriter<UiSnapshot> uiWriter,
        Func<IReadOnlyList<BotTab>> getBotTabs,
        Func<string?> getActiveBotId,
        Func<BotSession?> getActiveBot,
        Func<string?, IReadOnlyList<string>> getExecutionStatusLinesForBot,
        Action<string> logAuth)
    {
        _uiWriter = uiWriter;
        _getBotTabs = getBotTabs;
        _getActiveBotId = getActiveBotId;
        _getActiveBot = getActiveBot;
        _getExecutionStatusLinesForBot = getExecutionStatusLinesForBot;
        _logAuth = logAuth;
    }

    public void LogBotTabsIfChanged(string context, IReadOnlyList<BotTab>? tabs = null, string? activeId = null)
    {
        var currentTabs = tabs ?? _getBotTabs();
        var currentActiveId = activeId ?? _getActiveBotId();
        var labels = string.Join(",", currentTabs.Select(t => t.Label));
        var signature = $"{currentTabs.Count}|{currentActiveId}|{labels}";
        if (signature == _lastLoggedBotTabSignature)
            return;

        _lastLoggedBotTabSignature = signature;
        _logAuth(
            $"{context} | tabs_changed | count={currentTabs.Count} | active={currentActiveId ?? "(null)"} | labels=[{labels}]");
    }

    public void PublishNoBotSnapshot(string? message = null)
    {
        var tabs = _getBotTabs();
        var activeBotId = _getActiveBotId();
        LogBotTabsIfChanged("publish_no_bot_snapshot", tabs, activeBotId);
        _uiWriter.TryWrite(new UiSnapshot(
            null,
            Array.Empty<string>(),
            null,
            null,
            null,
            Array.Empty<MissionPromptOption>(),
            Array.Empty<MissionPromptOption>(),
            Array.Empty<string>(),
            _getExecutionStatusLinesForBot(activeBotId),
            null,
            null,
            null,
            null,
            null,
            tabs,
            activeBotId,
            null));
    }

    public void PublishActiveSnapshot(string? noStateMessage = null)
    {
        var active = _getActiveBot();
        if (active == null)
        {
            PublishNoBotSnapshot();
            return;
        }

        PublishNoBotSnapshot(noStateMessage ?? $"Bot '{active.Label}' loaded; initial state unavailable.");
    }

    public void PublishPrayerSnapshot(BotSession bot, AppPrayerRuntimeState snapshot)
    {
        if (snapshot.State == null)
        {
            PublishNoBotSnapshot($"Bot '{bot.Label}' loaded; initial state unavailable.");
            return;
        }

        var tabs = _getBotTabs();
        var activeBotId = _getActiveBotId();
        var uiState = AppUiStateBuilder.BuildUiState(snapshot.State);
        var missionPrompts = MissionPromptBuilder.BuildOptions(snapshot.State);
        var availableMissionPrompts = MissionPromptBuilder.BuildAvailableOptions(snapshot.State);
        LogBotTabsIfChanged("publish_prayer_snapshot", tabs, activeBotId);
        _uiWriter.TryWrite(new UiSnapshot(
            uiState.SpaceModel,
            snapshot.State.Systems ?? Array.Empty<string>(),
            uiState.TradeModel,
            uiState.ShipyardModel,
            uiState.CatalogModel,
            missionPrompts,
            availableMissionPrompts,
            snapshot.Memory,
            snapshot.ExecutionStatusLines,
            snapshot.ControlInput,
            snapshot.CurrentScriptLine,
            snapshot.LastGenerationPrompt,
            snapshot.CurrentTick,
            snapshot.LastSpaceMoltPostUtc,
            tabs,
            activeBotId,
            uiState.CraftingModel));
    }
}
