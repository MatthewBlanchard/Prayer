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
        var llamaCppBaseUrl = Environment.GetEnvironmentVariable("LLAMACPP_BASE_URL")
            ?? "http://localhost:8080";
        var llamaCppModel = Environment.GetEnvironmentVariable("LLAMACPP_MODEL")
            ?? "model";
        ILLMClient commandLlm = new LlamaCppClient(llamaCppBaseUrl, llamaCppModel);

        var openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var groqApiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        var openAiDefaultModel = Environment.GetEnvironmentVariable("OPENAI_MODEL")
            ?? "gpt-4o-mini";
        var groqDefaultModel = Environment.GetEnvironmentVariable("GROQ_MODEL")
            ?? "llama-3.3-70b-versatile";

        var providersById = new Dictionary<string, ILLMProvider>(StringComparer.OrdinalIgnoreCase)
        {
            ["llamacpp"] = new LlamaCppProvider(llamaCppBaseUrl, llamaCppModel)
        };

        if (!string.IsNullOrWhiteSpace(openAiApiKey))
            providersById["openai"] = new OpenAIProvider(openAiApiKey, openAiDefaultModel);
        if (!string.IsNullOrWhiteSpace(groqApiKey))
            providersById["groq"] = new GroqProvider(groqApiKey, groqDefaultModel);

        static string NormalizeProvider(string? provider)
        {
            var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();
            return normalized switch
            {
                "openai" => "openai",
                "groq" => "groq",
                "llamacpp" => "llamacpp",
                _ => "llamacpp"
            };
        }

        string ResolveDefaultModel(string provider)
        {
            var normalizedProvider = NormalizeProvider(provider);
            if (providersById.TryGetValue(normalizedProvider, out var providerInstance))
                return providerInstance.DefaultModel;
            return "model";
        }

        ILLMClient CreatePlanningClient(string provider, string model)
        {
            var normalizedProvider = NormalizeProvider(provider);
            if (!providersById.TryGetValue(normalizedProvider, out var llmProvider))
                throw new InvalidOperationException(
                    $"Provider '{normalizedProvider}' is not configured.");

            var normalizedModel = string.IsNullOrWhiteSpace(model)
                ? llmProvider.DefaultModel
                : model.Trim();

            return llmProvider.CreateClient(normalizedModel);
        }

        var savedLlmSelectionStore = new SavedLlmSelectionStore();
        var savedLlmSelection = savedLlmSelectionStore.Load();

        var requestedProvider = NormalizeProvider(Environment.GetEnvironmentVariable("LLM_PROVIDER"));
        var initialProvider = providersById.ContainsKey(requestedProvider)
            ? requestedProvider
            : providersById.ContainsKey("openai")
                ? "openai"
                : providersById.ContainsKey("groq")
                    ? "groq"
                    : "llamacpp";
        var initialModel = Environment.GetEnvironmentVariable("LLM_MODEL")
            ?? (initialProvider == "groq"
                ? Environment.GetEnvironmentVariable("GROQ_MODEL")
                : initialProvider == "openai"
                    ? Environment.GetEnvironmentVariable("OPENAI_MODEL")
                    : Environment.GetEnvironmentVariable("LLAMACPP_MODEL"))
            ?? ResolveDefaultModel(initialProvider);
        if (savedLlmSelection != null)
        {
            var savedProvider = NormalizeProvider(savedLlmSelection.Provider);
            if (providersById.ContainsKey(savedProvider))
            {
                initialProvider = savedProvider;
                initialModel = string.IsNullOrWhiteSpace(savedLlmSelection.Model)
                    ? ResolveDefaultModel(savedProvider)
                    : savedLlmSelection.Model.Trim();
            }
        }

        ILLMClient initialPlanningClient = CreatePlanningClient(initialProvider, initialModel);
        var planningLlm = new SwappableLlmClient(initialPlanningClient);
        string currentPlannerProvider = initialProvider;
        string currentPlannerModel = initialModel;

        PromptScriptRag? scriptExampleRag = null;
        if (!string.IsNullOrWhiteSpace(openAiApiKey))
        {
            var openAiEmbeddingModel = Environment.GetEnvironmentVariable("OPENAI_EMBEDDING_MODEL")
                ?? "text-embedding-3-small";
            scriptExampleRag = new PromptScriptRag(openAiApiKey, openAiEmbeddingModel);
        }

        IAppUi ui = new HtmxBotWindow(
            Environment.GetEnvironmentVariable("UI_PREFIX") ?? "http://localhost:5057/");
        var prayerBaseUrl = Environment.GetEnvironmentVariable("PRAYER_BASE_URL");
        if (string.IsNullOrWhiteSpace(prayerBaseUrl))
            throw new InvalidOperationException("PRAYER_BASE_URL is required (legacy in-process runtime path is disabled).");
        var prayerApi = new PrayerApiClient(prayerBaseUrl);
        var orderedProviderIds = new List<string> { "openai", "groq", "llamacpp" };
        foreach (var providerId in providersById.Keys)
        {
            if (!orderedProviderIds.Contains(providerId, StringComparer.OrdinalIgnoreCase))
                orderedProviderIds.Add(providerId);
        }

        ui.SetAvailableProviders(orderedProviderIds);
        ui.SetProviderModels("openai", new[] { openAiDefaultModel });
        ui.SetProviderModels("groq", new[] { groqDefaultModel });
        ui.SetProviderModels("llamacpp", new[] { llamaCppModel });
        foreach (var provider in providersById.Values)
            ui.SetProviderModels(provider.ProviderId, new[] { provider.DefaultModel });
        ui.ConfigureInitialLlmSelection(initialProvider, initialModel);

        async Task LoadRemoteModelCatalogsAsync()
        {
            foreach (var provider in providersById.Values)
            {
                try
                {
                    var models = await provider.ListModelsAsync();
                    if (models.Count > 0)
                        ui.SetProviderModels(provider.ProviderId, models);
                }
                catch
                {
                    // Keep UI responsive with defaults if API discovery fails.
                }
            }
        }

        await LoadRemoteModelCatalogsAsync();

        var channels = ProgramChannels.CreateAndBind(ui);
        var cts = new CancellationTokenSource();

        var botSessions = new Dictionary<string, BotSession>(StringComparer.Ordinal);
        string? activeBotId = null;
        object botLock = new();
        var savedBotStore = new SavedBotStore();
        var savedBots = savedBotStore.Load();

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

                return (session, passwordToSave);
            }
            catch (Exception ex)
            {
                LogAuth($"{flowLabel} | {label} | failed | {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        void UpsertSavedBot(string username, string password)
        {
            savedBotStore.Upsert(savedBots, username, password);
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
                            snapshotPublisher.LogBotTabsIfChanged("manual_add_added");
                            UpsertSavedBot(username, passwordToSave);

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
                        var selectedProvider = NormalizeProvider(selection.Provider);
                        if (!providersById.TryGetValue(selectedProvider, out var provider))
                        {
                            channels.Status.Writer.TryWrite(
                                $"Provider '{selectedProvider}' is not configured in this run.");
                            continue;
                        }

                        var selectedModel = string.IsNullOrWhiteSpace(selection.Model)
                            ? provider.DefaultModel
                            : selection.Model.Trim();

                        if (string.Equals(currentPlannerProvider, selectedProvider, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(currentPlannerModel, selectedModel, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        try
                        {
                            var updatedClient = provider.CreateClient(selectedModel);
                            planningLlm.SetInner(updatedClient);
                            currentPlannerProvider = selectedProvider;
                            currentPlannerModel = selectedModel;
                            savedLlmSelectionStore.Save(currentPlannerProvider, currentPlannerModel);
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

                    var activePrayerBot = GetActiveBot();
                    if (activePrayerBot?.PrayerSessionId != null)
                    {
                        try
                        {
                            var prayerSnapshot = await prayerApi.GetRuntimeStateAsync(activePrayerBot.PrayerSessionId);
                            snapshotPublisher.PublishPrayerSnapshot(activePrayerBot, prayerSnapshot);
                        }
                        catch (Exception ex)
                        {
                            LogAuth($"prayer_ui_poll_failed | {activePrayerBot.Label} | {ex.GetType().Name}: {ex.Message}");
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
            uiRenderTask.ContinueWith(_ => { })
        );
    }

}
