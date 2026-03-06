using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        AppPaths.EnsureDirectories();
        AppPaths.ResetDebugLogsOnStartup();

        IAppUi ui = new HtmxBotWindow(
            Environment.GetEnvironmentVariable("UI_PREFIX") ?? "http://localhost:5057/");
        var prayerBaseUrl = Environment.GetEnvironmentVariable("PRAYER_BASE_URL");
        if (string.IsNullOrWhiteSpace(prayerBaseUrl))
            prayerBaseUrl = "http://localhost:5000/";
        var prayerApi = new PrayerApiClient(prayerBaseUrl);
        var savedLlmSelection = await prayerApi.GetDefaultLlmPreferenceAsync();
        var llmCatalog = await prayerApi.GetLlmCatalogAsync();
        var availableProviders = llmCatalog.Providers.Select(p => p.ProviderId).ToList();
        ui.SetAvailableProviders(availableProviders);
        foreach (var provider in llmCatalog.Providers)
            ui.SetProviderModels(provider.ProviderId, provider.Models);

        string currentPlannerProvider = llmCatalog.DefaultProvider;
        string currentPlannerModel = llmCatalog.DefaultModel;
        if (savedLlmSelection != null)
        {
            var matchingProvider = llmCatalog.Providers
                .FirstOrDefault(p => string.Equals(p.ProviderId, savedLlmSelection.Provider, StringComparison.OrdinalIgnoreCase));
            if (matchingProvider != null)
            {
                currentPlannerProvider = matchingProvider.ProviderId;
                currentPlannerModel = string.IsNullOrWhiteSpace(savedLlmSelection.Model)
                    ? matchingProvider.DefaultModel
                    : savedLlmSelection.Model.Trim();
            }
        }
        ui.ConfigureInitialLlmSelection(currentPlannerProvider, currentPlannerModel);

        var channels = ProgramChannels.CreateAndBind(ui);
        var cts = new CancellationTokenSource();

        var botSessions = new Dictionary<string, BotSession>(StringComparer.Ordinal);
        string? activeBotId = null;
        object botLock = new();
        var savedBots = (await prayerApi.GetSavedBotsAsync()).ToList();

        channels.Status.Writer.TryWrite(
            $"Planner LLM: {currentPlannerProvider}/{currentPlannerModel}");
        channels.Status.Writer.TryWrite($"Prayer runtime: {prayerBaseUrl}");

        void LogAuth(string message)
        {
            var line = $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}";
            try
            {
                File.AppendAllText(AppPaths.AuthFlowLogFile, line);
            }
            catch
            {
                // Never crash startup because logging failed.
            }
        }

        BotSession? GetActiveBot()
        {
            lock (botLock)
            {
                if (activeBotId == null)
                    return null;

                return botSessions.TryGetValue(activeBotId, out var session)
                    ? session
                    : null;
            }
        }

        IReadOnlyList<BotTab> GetBotTabs()
        {
            lock (botLock)
            {
                return botSessions.Values
                    .Select(b => new BotTab(b.Id, b.Label))
                    .ToList();
            }
        }

        IReadOnlyList<string> GetExecutionStatusLinesForBot(string? botId)
        {
            lock (botLock)
            {
                if (botId == null || !botSessions.TryGetValue(botId, out var session))
                    return Array.Empty<string>();

                return session.ExecutionStatusLines.ToList();
            }
        }

        string? GetActiveBotId()
        {
            lock (botLock)
            {
                return activeBotId;
            }
        }

        bool GetActiveBotLoopEnabled()
        {
            lock (botLock)
            {
                if (activeBotId == null || !botSessions.TryGetValue(activeBotId, out var session))
                    return false;

                return session.LoopEnabled;
            }
        }

        var snapshotPublisher = new UiSnapshotPublisher(
            channels.UiSnapshots.Writer,
            GetBotTabs,
            GetActiveBotId,
            GetActiveBot,
            GetActiveBotLoopEnabled,
            GetExecutionStatusLinesForBot,
            LogAuth);
        var sessionPollers = new Dictionary<string, (CancellationTokenSource Cts, Task Task)>(StringComparer.Ordinal);

        void StartSessionPoller(BotSession session)
        {
            if (string.IsNullOrWhiteSpace(session.PrayerSessionId))
                return;

            if (sessionPollers.ContainsKey(session.Id))
                return;

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var pollTask = Task.Run(async () =>
            {
                long sinceVersion;
                string prayerSessionId;
                lock (botLock)
                {
                    sinceVersion = session.PrayerStateVersion;
                    prayerSessionId = session.PrayerSessionId ?? "";
                }

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(prayerSessionId))
                            break;

                        var pollResult = await prayerApi.GetRuntimeStateLongPollAsync(
                            prayerSessionId,
                            sinceVersion,
                            waitMs: 1000,
                            linkedCts.Token);

                        if (!pollResult.Changed || pollResult.State == null)
                            continue;

                        sinceVersion = pollResult.StateVersion;

                        bool isActive;
                        lock (botLock)
                        {
                            if (botSessions.TryGetValue(session.Id, out var current))
                            {
                                current.PrayerStateVersion = pollResult.StateVersion;
                                current.LastPrayerState = pollResult.State;
                            }
                            isActive = string.Equals(activeBotId, session.Id, StringComparison.Ordinal);
                        }

                        if (isActive)
                            snapshotPublisher.PublishPrayerSnapshot(session, pollResult.State);
                    }
                    catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogAuth($"prayer_session_poll_failed | {session.Label} | {ex.GetType().Name}: {ex.Message}");
                        try
                        {
                            await Task.Delay(500, linkedCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
            }, linkedCts.Token);

            sessionPollers[session.Id] = (linkedCts, pollTask);
        }

        async Task<(BotSession Session, string Password)> CreateBotSessionAsync(
            string username,
            string flowLabel,
            AddBotMode mode,
            string? password = null,
            string? empire = null,
            string? registrationCode = null)
        {
            var normalizedUsername = username.Trim();
            var label = normalizedUsername;
            var totalTimer = Stopwatch.StartNew();
            LogAuth($"{flowLabel} | {label} | start");

            try
            {
                var authTimer = Stopwatch.StartNew();
                string prayerSessionId;
                string passwordToSave;

                if (mode == AddBotMode.Register)
                {
                    if (string.IsNullOrWhiteSpace(registrationCode) || string.IsNullOrWhiteSpace(empire))
                        throw new ArgumentException("Registration code and empire are required for register mode.");

                    var registerResult = await prayerApi.RegisterSessionAsync(
                        normalizedUsername,
                        empire.Trim().ToLowerInvariant(),
                        registrationCode.Trim(),
                        label);

                    prayerSessionId = registerResult.SessionId;
                    passwordToSave = registerResult.Password;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(password))
                        throw new ArgumentException("Password is required for login mode.");

                    prayerSessionId = await prayerApi.CreateSessionAsync(
                        normalizedUsername,
                        password,
                        label);
                    passwordToSave = password;
                }

                LogAuth($"{flowLabel} | {label} | authenticated_and_session_ready | {authTimer.ElapsedMilliseconds}ms");

                LogAuth($"{flowLabel} | {label} | session_ready | {totalTimer.ElapsedMilliseconds}ms");

                var session = new BotSession(
                    Guid.NewGuid().ToString("N"),
                    label);

                session.PrayerSessionId = prayerSessionId;
                LogAuth($"{flowLabel} | {label} | prayer_session_created | id={session.PrayerSessionId}");
                try
                {
                    await prayerApi.SetSessionLlmAsync(
                        prayerSessionId,
                        currentPlannerProvider,
                        currentPlannerModel);
                }
                catch (Exception ex)
                {
                    channels.Status.Writer.TryWrite(
                        $"[{label}] Session created but LLM apply failed: {ex.Message}");
                    LogAuth(
                        $"{flowLabel} | {label} | llm_apply_failed | provider={currentPlannerProvider} | model={currentPlannerModel} | {ex.GetType().Name}: {ex.Message}");
                }

                return (session, passwordToSave);
            }
            catch (Exception ex)
            {
                LogAuth($"{flowLabel} | {label} | failed | {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        snapshotPublisher.PublishNoBotSnapshot();

        if (savedBots.Count > 0)
        {
            channels.Status.Writer.TryWrite($"Loading {savedBots.Count} saved bot(s)...");
            LogAuth($"startup | begin_autoload | count={savedBots.Count}");
            foreach (var savedBot in savedBots.ToList())
            {
                try
                {
                    channels.Status.Writer.TryWrite($"Auto-login saved bot '{savedBot.Username}'...");
                    LogAuth($"startup | {savedBot.Username} | autologin_begin");
                    var (session, _) = await CreateBotSessionAsync(
                        savedBot.Username,
                        "startup/autologin",
                        AddBotMode.Login,
                        password: savedBot.Password);

                    lock (botLock)
                    {
                        botSessions[session.Id] = session;
                        if (activeBotId == null)
                            activeBotId = session.Id;
                    }
                    StartSessionPoller(session);
                    snapshotPublisher.LogBotTabsIfChanged("startup_autologin_added");
                }
                catch (Exception ex)
                {
                    channels.Status.Writer.TryWrite(
                        $"Failed to auto-login saved bot '{savedBot.Username}': {ex.Message}");
                    LogAuth($"startup | {savedBot.Username} | autologin_failed | {ex.GetType().Name}: {ex.Message}");
                }
            }
            LogAuth("startup | end_autoload");
        }

        snapshotPublisher.PublishActiveSnapshot();

        var botTask = Task.Run(async () =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    while (channels.AddBot.Reader.TryRead(out var request))
                    {
                        var username = request.Username.Trim();
                        if (string.IsNullOrWhiteSpace(username))
                        {
                            channels.Status.Writer.TryWrite("Username is required.");
                            continue;
                        }

                        var display = username;
                        bool isLogin = request.Mode == AddBotMode.Login;
                        channels.Status.Writer.TryWrite(isLogin
                            ? $"Logging in bot '{display}'..."
                            : $"Registering bot '{display}'...");
                        LogAuth(isLogin
                            ? $"manual | {display} | login_begin"
                            : $"manual | {display} | register_begin");

                        try
                        {
                            BotSession session;
                            string passwordToSave;

                            if (request.Mode == AddBotMode.Register)
                            {
                                var registrationCode = request.RegistrationCode?.Trim();
                                var empire = request.Empire?.Trim().ToLowerInvariant();

                                if (string.IsNullOrWhiteSpace(registrationCode) ||
                                    string.IsNullOrWhiteSpace(empire))
                                {
                                    channels.Status.Writer.TryWrite(
                                        "Registration code and empire are required for register mode.");
                                    continue;
                                }

                                (session, passwordToSave) = await CreateBotSessionAsync(
                                    username,
                                    "manual/register",
                                    AddBotMode.Register,
                                    empire: empire,
                                    registrationCode: registrationCode);
                            }
                            else
                            {
                                var password = request.Password ?? "";
                                if (string.IsNullOrWhiteSpace(password))
                                {
                                    channels.Status.Writer.TryWrite("Password is required for login mode.");
                                    continue;
                                }

                                (session, passwordToSave) = await CreateBotSessionAsync(
                                    username,
                                    "manual/login",
                                    AddBotMode.Login,
                                    password: password);
                            }

                            lock (botLock)
                            {
                                botSessions[session.Id] = session;
                                if (activeBotId == null)
                                    activeBotId = session.Id;
                            }
                            StartSessionPoller(session);
                            snapshotPublisher.LogBotTabsIfChanged("manual_add_added");
                            try
                            {
                                await prayerApi.UpsertSavedBotAsync(username, passwordToSave);
                            }
                            catch (Exception ex)
                            {
                                channels.Status.Writer.TryWrite($"Failed to save bot profile: {ex.Message}");
                            }

                            channels.Status.Writer.TryWrite($"Bot loaded: {session.Label}");
                            snapshotPublisher.PublishActiveSnapshot();
                        }
                        catch (Exception ex)
                        {
                            channels.Status.Writer.TryWrite($"Failed to load bot '{display}': {ex.Message}");
                            LogAuth($"manual | {display} | load_failed | {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    while (channels.LlmSelection.Reader.TryRead(out var selection))
                    {
                        var selectedProvider = (selection.Provider ?? string.Empty).Trim();
                        var providerCatalog = llmCatalog.Providers.FirstOrDefault(p =>
                            string.Equals(p.ProviderId, selectedProvider, StringComparison.OrdinalIgnoreCase));
                        if (providerCatalog == null)
                        {
                            channels.Status.Writer.TryWrite(
                                $"Provider '{selectedProvider}' is not configured in this run.");
                            continue;
                        }

                        var selectedModel = string.IsNullOrWhiteSpace(selection.Model)
                            ? providerCatalog.DefaultModel
                            : selection.Model.Trim();
                        selectedProvider = providerCatalog.ProviderId;

                        if (string.Equals(currentPlannerProvider, selectedProvider, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(currentPlannerModel, selectedModel, StringComparison.Ordinal))
                        {
                            channels.Status.Writer.TryWrite(
                                $"Planner LLM already set to {currentPlannerProvider}/{currentPlannerModel}");
                            LogAuth(
                                $"llm_switch_noop | provider={currentPlannerProvider} | model={currentPlannerModel}");
                            continue;
                        }

                        try
                        {
                            var active = GetActiveBot();
                            if (active?.PrayerSessionId != null)
                            {
                                await prayerApi.SetSessionLlmAsync(
                                    active.PrayerSessionId,
                                    selectedProvider,
                                    selectedModel);
                            }
                            currentPlannerProvider = selectedProvider;
                            currentPlannerModel = selectedModel;
                            await prayerApi.SetDefaultLlmPreferenceAsync(
                                currentPlannerProvider,
                                currentPlannerModel);
                            channels.Status.Writer.TryWrite(
                                $"Planner LLM set to {currentPlannerProvider}/{currentPlannerModel}");
                            LogAuth(
                                $"llm_switch | provider={currentPlannerProvider} | model={currentPlannerModel}");
                        }
                        catch (Exception ex)
                        {
                            channels.Status.Writer.TryWrite(
                                $"LLM switch failed ({selectedProvider}/{selectedModel}): {ex.Message}");
                            LogAuth(
                                $"llm_switch_failed | provider={selectedProvider} | model={selectedModel} | {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    while (channels.SwitchBot.Reader.TryRead(out var botId))
                    {
                        BotSession? switched = null;
                        lock (botLock)
                        {
                            if (botSessions.TryGetValue(botId, out var existing))
                            {
                                activeBotId = botId;
                                switched = existing;
                            }
                        }

                        if (switched == null)
                        {
                            channels.Status.Writer.TryWrite("Selected bot no longer exists.");
                            continue;
                        }

                        channels.Status.Writer.TryWrite($"Switched to {switched.Label}");
                        if (switched.LastPrayerState != null)
                            snapshotPublisher.PublishPrayerSnapshot(switched, switched.LastPrayerState);
                        else
                            snapshotPublisher.PublishActiveSnapshot();
                    }

                    while (channels.LoopUpdates.Reader.TryRead(out var update))
                    {
                        BotSession? active;
                        bool enabled;
                        string? prayerSessionId = null;

                        lock (botLock)
                        {
                            active = activeBotId != null && botSessions.TryGetValue(activeBotId, out var session)
                                ? session
                                : null;

                            if (active == null)
                            {
                                enabled = false;
                            }
                            else
                            {
                                enabled = update.Enabled ?? !active.LoopEnabled;
                                active.LoopEnabled = enabled;
                                prayerSessionId = active.PrayerSessionId;
                            }
                        }

                        if (active == null)
                        {
                            channels.Status.Writer.TryWrite("No active bot selected.");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(prayerSessionId))
                        {
                            channels.Status.Writer.TryWrite($"[{active.Label}] Prayer session is not available.");
                            continue;
                        }

                        try
                        {
                            await prayerApi.SetLoopEnabledAsync(prayerSessionId, enabled);
                        }
                        catch (Exception ex)
                        {
                            channels.Status.Writer.TryWrite($"[{active.Label}] Loop update failed: {ex.Message}");
                            LogAuth($"loop_update_failed | {active.Label} | {ex.GetType().Name}: {ex.Message}");
                            continue;
                        }

                        channels.Status.Writer.TryWrite($"[{active.Label}] Loop {(enabled ? "enabled" : "disabled")}");
                        snapshotPublisher.PublishActiveSnapshot();
                    }

                    while (channels.RuntimeCommands.Reader.TryRead(out var request))
                    {
                        BotSession? target;
                        string? prayerSessionId = null;
                        lock (botLock)
                        {
                            target = !string.IsNullOrWhiteSpace(request.BotId) &&
                                     botSessions.TryGetValue(request.BotId, out var byId)
                                ? byId
                                : null;
                            if (target != null)
                                prayerSessionId = target.PrayerSessionId;
                        }

                        if (target == null)
                        {
                            channels.Status.Writer.TryWrite("Selected bot no longer exists.");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(prayerSessionId))
                        {
                            channels.Status.Writer.TryWrite($"[{target.Label}] Prayer session is not available.");
                            continue;
                        }

                        try
                        {
                            await prayerApi.SendRuntimeCommandAsync(
                                prayerSessionId,
                                request.Command,
                                request.Argument);

                            if (string.Equals(request.Command, RuntimeCommandNames.ExecuteScript, StringComparison.Ordinal))
                                channels.Status.Writer.TryWrite($"Restarting script for {target.Label}");
                        }
                        catch (Exception ex)
                        {
                            channels.Status.Writer.TryWrite($"[{target.Label}] Runtime command failed: {ex.Message}");
                            LogAuth($"runtime_command_failed | {target.Label} | {request.Command} | {ex.GetType().Name}: {ex.Message}");
                        }
                    }

                    await Task.Delay(50, cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                LogAuth($"bot_coordinator | failed | {ex.GetType().Name}: {ex.Message}");
            }
        }, cts.Token);

        var uiRenderTask = Task.Run(async () =>
        {
            string lastRenderedTabSignature = "";
            try
            {
                while (await channels.UiSnapshots.Reader.WaitToReadAsync(cts.Token))
                {
                    UiSnapshot snapshot = await channels.UiSnapshots.Reader.ReadAsync(cts.Token);
                    while (channels.UiSnapshots.Reader.TryRead(out var newer))
                        snapshot = newer;

                    var renderedLabels = string.Join(",", snapshot.Bots.Select(b => b.Label));
                    var renderedSignature = $"{snapshot.Bots.Count}|{snapshot.ActiveBotId}|{renderedLabels}";
                    if (renderedSignature != lastRenderedTabSignature)
                    {
                        lastRenderedTabSignature = renderedSignature;
                        LogAuth(
                            $"ui_render_dispatch | tabs_changed | count={snapshot.Bots.Count} | active={snapshot.ActiveBotId ?? "(null)"} | labels=[{renderedLabels}]");
                    }

                    ui.Render(
                        snapshot.SpaceStateMarkdown,
                        snapshot.TradeStateMarkdown,
                        snapshot.ShipyardStateMarkdown,
                        snapshot.CantinaStateMarkdown,
                        snapshot.CatalogStateMarkdown,
                        snapshot.ActiveMissionPrompts,
                        snapshot.Memory,
                        snapshot.ExecutionStatusLines,
                        snapshot.ControlInput,
                        snapshot.CurrentScriptLine,
                        snapshot.LastGenerationPrompt,
                        snapshot.Bots,
                        snapshot.ActiveBotId,
                        snapshot.ActiveBotLoopEnabled
                    );
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }, cts.Token);

        ui.Run();

        cts.Cancel();
        var pollerHandles = sessionPollers.Values.ToList();
        foreach (var (pollerCts, _) in pollerHandles)
            pollerCts.Cancel();
        channels.UiSnapshots.Writer.TryComplete();

        List<BotSession> sessionsToDispose;
        lock (botLock)
        {
            sessionsToDispose = botSessions.Values.ToList();
        }

        foreach (var session in sessionsToDispose)
        {
            if (!string.IsNullOrWhiteSpace(session.PrayerSessionId))
            {
                try
                {
                    await prayerApi.DeleteSessionAsync(session.PrayerSessionId);
                }
                catch
                {
                    // Best effort cleanup.
                }
            }
        }

        await Task.WhenAll(
            botTask.ContinueWith(_ => { }),
            uiRenderTask.ContinueWith(_ => { }),
            Task.WhenAll(pollerHandles.Select(p => p.Task.ContinueWith(_ => { })))
        );
    }

}
