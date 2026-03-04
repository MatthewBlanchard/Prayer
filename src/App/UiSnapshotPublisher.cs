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
    private readonly Func<BotSession, bool> _isActiveBot;
    private readonly Func<string?, IReadOnlyList<string>> _getExecutionStatusLinesForBot;
    private readonly Action<string> _logAuth;
    private string _lastLoggedBotTabSignature = "";

    public UiSnapshotPublisher(
        ChannelWriter<UiSnapshot> uiWriter,
        Func<IReadOnlyList<BotTab>> getBotTabs,
        Func<string?> getActiveBotId,
        Func<BotSession?> getActiveBot,
        Func<BotSession, bool> isActiveBot,
        Func<string?, IReadOnlyList<string>> getExecutionStatusLinesForBot,
        Action<string> logAuth)
    {
        _uiWriter = uiWriter;
        _getBotTabs = getBotTabs;
        _getActiveBotId = getActiveBotId;
        _getActiveBot = getActiveBot;
        _isActiveBot = isActiveBot;
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
            message ?? "No bot logged in. Use + to add one.",
            null,
            null,
            null,
            Array.Empty<MissionPromptOption>(),
            Array.Empty<string>(),
            _getExecutionStatusLinesForBot(activeBotId),
            null,
            null,
            ControlModeKind.ScriptMode,
            new List<string> { "(load a bot with +)" },
            null,
            tabs,
            activeBotId));
    }

    public void PublishSnapshotForBot(BotSession bot, GameState state)
    {
        var tabs = _getBotTabs();
        var activeBotId = _getActiveBotId();
        var uiState = bot.Agent.BuildUiState(state);
        var missionPrompts = MissionPromptBuilder.BuildOptions(state);
        LogBotTabsIfChanged("publish_snapshot", tabs, activeBotId);
        _uiWriter.TryWrite(new UiSnapshot(
            uiState.SpaceStateMarkdown,
            uiState.TradeStateMarkdown,
            uiState.ShipyardStateMarkdown,
            uiState.CantinaStateMarkdown,
            missionPrompts,
            bot.Agent.GetMemoryList(),
            _getExecutionStatusLinesForBot(activeBotId),
            bot.Agent.CurrentControlInput,
            bot.Agent.CurrentScriptLine,
            bot.Agent.CurrentControlModeKind,
            bot.Agent.GetAvailableActions(state),
            bot.Agent.LastScriptGenerationPrompt,
            tabs,
            activeBotId));
    }

    public void PublishActiveSnapshot(string? noStateMessage = null)
    {
        var active = _getActiveBot();
        if (active == null)
        {
            PublishNoBotSnapshot();
            return;
        }

        if (active.LatestState != null)
        {
            PublishSnapshotForBot(active, active.LatestState);
            return;
        }

        PublishNoBotSnapshot(noStateMessage ?? $"Bot '{active.Label}' loaded; initial state unavailable.");
    }

    public void PublishSnapshot(BotSession bot, GameState state)
    {
        bot.LatestState = state;
        if (_isActiveBot(bot))
            PublishSnapshotForBot(bot, state);
        else
            PublishActiveSnapshot();
    }
}
