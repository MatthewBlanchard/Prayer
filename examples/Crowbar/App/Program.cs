using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    private static readonly string[] BotColorPalette =
    {
        "#ff6b6b", "#ffd166", "#06d6a0", "#4cc9f0", "#90be6d",
        "#f94144", "#43aa8b", "#577590", "#f8961e", "#3a86ff",
        "#ff9f1c", "#2ec4b6", "#e76f51", "#8ecae6", "#a7c957",
        "#ff595e", "#1982c4", "#6a4c93", "#f9844a", "#00bbf9"
    };

    private static string DeriveBotColorHex(string key)
    {
        var normalized = (key ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            return BotColorPalette[0];

        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in normalized)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            var index = (int)(hash % (uint)BotColorPalette.Length);
            return BotColorPalette[index];
        }
    }

    static async Task Main(string[] args)
    {
        AppPaths.EnsureDirectories();
        AppPaths.ResetDebugLogsOnStartup();

        var sink = new ChannelLogSink();
        LogSink.SetInstance(sink);
        sink.Start();

        var htmxUi = new HtmxBotWindow(
            Environment.GetEnvironmentVariable("UI_PREFIX") ?? "http://localhost:5057/");
        IAppUi ui = htmxUi;
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
        string? defaultBotId = null;
        object botLock = new();
        var savedBots = (await prayerApi.GetSavedBotsAsync()).ToList();

        channels.Status.Writer.TryWrite(
            $"Planner LLM: {currentPlannerProvider}/{currentPlannerModel}");
        channels.Status.Writer.TryWrite($"Prayer runtime: {prayerBaseUrl}");

        void LogAuth(string message)
        {
            var line = $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}";
            LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.AuthFlow, line, AppPaths.AuthFlowLogFile));
        }

        IReadOnlyList<BotSession> GetAllBots()
        {
            lock (botLock)
            {
                return botSessions.Values.ToList();
            }
        }

        string? GetDefaultBotId()
        {
            lock (botLock)
            {
                return defaultBotId;
            }
        }

        IReadOnlyList<BotMapMarker> GetBotMapMarkers()
        {
            lock (botLock)
            {
                return botSessions.Values
                    .Select(session =>
                    {
                        var systemId = (session.LastPrayerState?.State?.System ?? string.Empty).Trim();
                        if (string.IsNullOrWhiteSpace(systemId))
                            return null;

                        return new BotMapMarker(
                            session.Id,
                            session.Label,
                            systemId,
                            session.ColorHex);
                    })
                    .Where(marker => marker != null)
                    .Cast<BotMapMarker>()
                    .ToList();
            }
        }

        IReadOnlyList<BotRouteOverlay> GetBotRoutes()
        {
            lock (botLock)
            {
                return botSessions.Values
                    .Select(session =>
                    {
                        var activeRoute = session.LastActiveRoute;
                        var currentSystem = (session.LastPrayerState?.State?.System ?? string.Empty).Trim();
                        if (activeRoute == null || string.IsNullOrWhiteSpace(currentSystem))
                            return null;

                        var hops = (activeRoute.Hops ?? Array.Empty<string>())
                            .Where(h => !string.IsNullOrWhiteSpace(h))
                            .Select(h => h.Trim())
                            .ToList();
                        if (hops.Count == 0)
                            return null;

                        return new BotRouteOverlay(
                            session.Id,
                            session.Label,
                            session.ColorHex,
                            currentSystem,
                            string.IsNullOrWhiteSpace(activeRoute.Target) ? null : activeRoute.Target.Trim(),
                            hops,
                            activeRoute.TotalJumps,
                            activeRoute.EstimatedFuel,
                            activeRoute.FuelAvailable,
                            session.LastPrayerState?.State?.Ship?.Speed);
                    })
                    .Where(route => route != null)
                    .Cast<BotRouteOverlay>()
                    .ToList();
            }
        }

        htmxUi.SetGenerateScriptHandler(async (botId, prompt) =>
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new InvalidOperationException("Prompt is required.");

            BotSession? target;
            string? prayerSessionId = null;
            lock (botLock)
            {
                target = botSessions.TryGetValue(botId, out var byId)
                    ? byId
                    : null;
                if (target != null)
                    prayerSessionId = target.PrayerSessionId;
            }

            if (target == null)
                throw new InvalidOperationException("Selected bot no longer exists.");

            if (string.IsNullOrWhiteSpace(prayerSessionId))
                throw new InvalidOperationException($"[{target.Label}] Prayer session is not available.");

            channels.Status.Writer.TryWrite($"Generating script draft for {target.Label}...");
            var generatedScript = await prayerApi.GenerateScriptAsync(prayerSessionId, prompt);
            channels.Status.Writer.TryWrite($"Generated script draft for {target.Label}. Review and set script to apply.");
            return generatedScript;
        });

        var snapshotPublisher = new UiSnapshotPublisher(
            channels.UiSnapshots.Writer,
            GetAllBots,
            GetDefaultBotId,
            GetBotMapMarkers,
            GetBotRoutes,
            LogAuth);
        var sessionPollers = new Dictionary<string, (CancellationTokenSource Cts, Task Task)>(StringComparer.Ordinal);
        var routePollers = new Dictionary<string, (CancellationTokenSource Cts, Task Task)>(StringComparer.Ordinal);

        void StartSessionPoller(BotSession session)
        {
            if (string.IsNullOrWhiteSpace(session.PrayerSessionId))
            {
                LogAuth($"session_poller_skipped | {session.Label} | reason=no_prayer_session_id");
                return;
            }

            if (sessionPollers.ContainsKey(session.Id))
            {
                LogAuth($"session_poller_skipped | {session.Label} | reason=already_registered");
                return;
            }

            LogAuth($"session_poller_starting | {session.Label} | session={session.PrayerSessionId}");
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

                LogAuth($"session_poller_started | {session.Label} | session={prayerSessionId} | since_version={sinceVersion}");

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(prayerSessionId))
                        {
                            LogAuth($"session_poller_exit | {session.Label} | reason=empty_session_id");
                            break;
                        }

                        var pollResult = await prayerApi.GetRuntimeStateLongPollAsync(
                            prayerSessionId,
                            sinceVersion,
                            waitMs: 1000,
                            linkedCts.Token);

                        if (!pollResult.Changed || pollResult.State == null)
                            continue;

                        LogAuth($"session_poller_state_changed | {session.Label} | version={pollResult.StateVersion}");
                        sinceVersion = pollResult.StateVersion;

                        lock (botLock)
                        {
                            if (botSessions.TryGetValue(session.Id, out var current))
                            {
                                current.PrayerStateVersion = pollResult.StateVersion;
                                current.LastPrayerState = pollResult.State;
                            }
                        }

                        snapshotPublisher.PublishSnapshot();
                    }
                    catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        LogAuth($"session_poller_poll_failed | {session.Label} | {ex.GetType().Name}: {ex.Message}");
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

        void StartRoutePoller(BotSession session)
        {
            if (string.IsNullOrWhiteSpace(session.PrayerSessionId))
                return;

            if (routePollers.ContainsKey(session.Id))
                return;

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            var pollTask = Task.Run(async () =>
            {
                string prayerSessionId;
                lock (botLock)
                    prayerSessionId = session.PrayerSessionId ?? "";

                while (!linkedCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(prayerSessionId))
                            break;

                        var route = await prayerApi.GetActiveRouteAsync(prayerSessionId, linkedCts.Token);

                        bool changed;
                        lock (botLock)
                        {
                            if (!botSessions.TryGetValue(session.Id, out var current))
                                break;

                            var prev = current.LastActiveRoute;
                            changed = prev?.Target != route?.Target ||
                                      (prev?.Hops?.Count ?? 0) != (route?.Hops?.Count ?? 0);
                            if (changed)
                                current.LastActiveRoute = route;
                        }

                        if (changed)
                            snapshotPublisher.PublishSnapshot();

                        await Task.Delay(500, linkedCts.Token);
                    }
                    catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        try { await Task.Delay(500, linkedCts.Token); }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }, linkedCts.Token);

            routePollers[session.Id] = (linkedCts, pollTask);
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

                    LogAuth($"{flowLabel} | {label} | http_register_session | begin");
                    var registerResult = await prayerApi.RegisterSessionAsync(
                        normalizedUsername,
                        empire.Trim().ToLowerInvariant(),
                        registrationCode.Trim(),
                        label);
                    LogAuth($"{flowLabel} | {label} | http_register_session | ok | {authTimer.ElapsedMilliseconds}ms");

                    prayerSessionId = registerResult.SessionId;
                    passwordToSave = registerResult.Password;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(password))
                        throw new ArgumentException("Password is required for login mode.");

                    LogAuth($"{flowLabel} | {label} | http_create_session | begin");
                    prayerSessionId = await prayerApi.CreateSessionAsync(
                        normalizedUsername,
                        password,
                        label);
                    LogAuth($"{flowLabel} | {label} | http_create_session | ok | {authTimer.ElapsedMilliseconds}ms | session={prayerSessionId}");
                    passwordToSave = password;
                }

                LogAuth($"{flowLabel} | {label} | session_ready | {totalTimer.ElapsedMilliseconds}ms");

                var session = new BotSession(
                    Guid.NewGuid().ToString("N"),
                    label,
                    DeriveBotColorHex(normalizedUsername));

                session.PrayerSessionId = prayerSessionId;
                LogAuth($"{flowLabel} | {label} | prayer_session_created | bot_id={session.Id} | prayer_session={session.PrayerSessionId}");
                LogAuth($"{flowLabel} | {label} | http_set_llm | begin | provider={currentPlannerProvider} | model={currentPlannerModel}");
                try
                {
                    await prayerApi.SetSessionLlmAsync(
                        prayerSessionId,
                        currentPlannerProvider,
                        currentPlannerModel);
                    LogAuth($"{flowLabel} | {label} | http_set_llm | ok");
                }
                catch (Exception ex)
                {
                    channels.Status.Writer.TryWrite(
                        $"[{label}] Session created but LLM apply failed: {ex.Message}");
                    LogAuth(
                        $"{flowLabel} | {label} | http_set_llm | failed | provider={currentPlannerProvider} | model={currentPlannerModel} | {ex.GetType().Name}: {ex.Message}");
                }

                return (session, passwordToSave);
            }
            catch (Exception ex)
            {
                LogAuth($"{flowLabel} | {label} | failed | {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        snapshotPublisher.PublishSnapshot();

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
                        if (defaultBotId == null)
                            defaultBotId = session.Id;
                    }
                    StartSessionPoller(session);
                    StartRoutePoller(session);
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

        snapshotPublisher.PublishSnapshot();

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
                                if (defaultBotId == null)
                                    defaultBotId = session.Id;
                            }
                            StartSessionPoller(session);
                            StartRoutePoller(session);
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
                            snapshotPublisher.PublishSnapshot();
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
                            // Apply new LLM to all active sessions.
                            List<BotSession> allSessions;
                            lock (botLock)
                            {
                                allSessions = botSessions.Values
                                    .Where(s => !string.IsNullOrWhiteSpace(s.PrayerSessionId))
                                    .ToList();
                            }

                            foreach (var session in allSessions)
                            {
                                try
                                {
                                    await prayerApi.SetSessionLlmAsync(
                                        session.PrayerSessionId!,
                                        selectedProvider,
                                        selectedModel);
                                }
                                catch (Exception ex)
                                {
                                    channels.Status.Writer.TryWrite(
                                        $"[{session.Label}] LLM apply failed: {ex.Message}");
                                }
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

                    while (channels.RuntimeCommands.Reader.TryRead(out var request))
                    {
                        var argSuffix = string.IsNullOrWhiteSpace(request.Argument) ? "" : $" | arg={request.Argument}";
                        LogAuth($"runtime_command_received | bot_id={request.BotId} | command={request.Command}{argSuffix}");

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
                            LogAuth($"runtime_command_drop | bot_id={request.BotId} | command={request.Command} | reason=bot_not_found");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(prayerSessionId))
                        {
                            channels.Status.Writer.TryWrite($"[{target.Label}] Prayer session is not available.");
                            LogAuth($"runtime_command_drop | bot={target.Label} | command={request.Command} | reason=no_prayer_session");
                            continue;
                        }

                        try
                        {
                            await prayerApi.SendRuntimeCommandAsync(
                                prayerSessionId,
                                request.Command,
                                request.Argument);

                            LogAuth($"runtime_command_sent | bot={target.Label} | command={request.Command} | session={prayerSessionId}{argSuffix}");
                            if (string.Equals(request.Command, RuntimeCommandNames.ExecuteScript, StringComparison.Ordinal))
                                channels.Status.Writer.TryWrite($"Restarting script for {target.Label}");
                        }
                        catch (Exception ex)
                        {
                            channels.Status.Writer.TryWrite($"[{target.Label}] Runtime command failed: {ex.Message}");
                            LogAuth($"runtime_command_failed | bot={target.Label} | command={request.Command} | session={prayerSessionId} | {ex.GetType().Name}: {ex.Message}");
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
                    var renderedSignature = $"{snapshot.Bots.Count}|{snapshot.DefaultBotId}|{renderedLabels}";
                    if (renderedSignature != lastRenderedTabSignature)
                    {
                        lastRenderedTabSignature = renderedSignature;
                        LogAuth(
                            $"ui_render_dispatch | tabs_changed | count={snapshot.Bots.Count} | default={snapshot.DefaultBotId ?? "(null)"} | labels=[{renderedLabels}]");
                    }

                    ui.Render(
                        snapshot.BotStates,
                        snapshot.BotRoutes,
                        snapshot.Bots,
                        snapshot.BotMapMarkers,
                        snapshot.DefaultBotId);
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
        var routePollerHandles = routePollers.Values.ToList();
        foreach (var (pollerCts, _) in pollerHandles)
            pollerCts.Cancel();
        foreach (var (pollerCts, _) in routePollerHandles)
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
            Task.WhenAll(pollerHandles.Select(p => p.Task.ContinueWith(_ => { }))),
            Task.WhenAll(routePollerHandles.Select(p => p.Task.ContinueWith(_ => { })))
        );

        await sink.DrainAndStopAsync();
    }

}
