using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

class Program
{
    private const int ScriptGenerationMaxAttempts = 3;
    private const int ExecutionStatusHistoryLimit = 4;

    static async Task Main(string[] args)
    {
        AppPaths.EnsureDirectories();
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

        var ui = new BotWindow();

        var orderedProviderIds = new List<string>();
        if (providersById.ContainsKey("openai"))
            orderedProviderIds.Add("openai");
        if (providersById.ContainsKey("groq"))
            orderedProviderIds.Add("groq");
        if (providersById.ContainsKey("llamacpp"))
            orderedProviderIds.Add("llamacpp");
        foreach (var providerId in providersById.Keys)
        {
            if (!orderedProviderIds.Contains(providerId, StringComparer.OrdinalIgnoreCase))
                orderedProviderIds.Add(providerId);
        }

        ui.SetAvailableProviders(orderedProviderIds);
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
        int globalStopTriggered = 0;

        var botSessions = new Dictionary<string, BotSession>(StringComparer.Ordinal);
        string? activeBotId = null;
        object botLock = new();
        var savedBotStore = new SavedBotStore();
        var savedBots = savedBotStore.Load();

        channels.Status.Writer.TryWrite(
            $"Planner LLM: {currentPlannerProvider}/{currentPlannerModel}");

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

        bool IsActiveBot(BotSession bot)
        {
            lock (botLock)
            {
                return activeBotId == bot.Id;
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

        var snapshotPublisher = new UiSnapshotPublisher(
            channels.UiSnapshots.Writer,
            GetBotTabs,
            GetActiveBotId,
            GetActiveBot,
            IsActiveBot,
            GetExecutionStatusLinesForBot,
            LogAuth);

        void AppendExecutionStatus(BotSession bot, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            bool shouldRefresh = false;
            lock (botLock)
            {
                bot.ExecutionStatusLines.Add(message.Trim());
                if (bot.ExecutionStatusLines.Count > ExecutionStatusHistoryLimit)
                    bot.ExecutionStatusLines.RemoveAt(0);
                shouldRefresh = activeBotId == bot.Id;
            }

            if (shouldRefresh)
                snapshotPublisher.PublishActiveSnapshot();
        }

        void TriggerGlobalStop(string reason)
        {
            if (Interlocked.Exchange(ref globalStopTriggered, 1) != 0)
                return;

            channels.Status.Writer.TryWrite($"Global stop: {reason}");
            LogAuth($"global_stop | {reason}");
            cts.Cancel();

            lock (botLock)
            {
                foreach (var session in botSessions.Values)
                    session.WorkerCts.Cancel();
            }
        }

        async Task<(BotSession Session, string Password)> CreateBotSessionAsync(
            string username,
            string flowLabel,
            Func<SpaceMoltHttpClient, Task<string>> authenticateAsync)
        {
            var normalizedUsername = username.Trim();
            var label = normalizedUsername;
            var totalTimer = Stopwatch.StartNew();
            LogAuth($"{flowLabel} | {label} | start");

            var agent = new SpaceMoltAgent(
                commandLlm,
                planningLlm,
                scriptExampleRag);
            agent.Halt("Awaiting script input");
            agent.SetStatusWriter(channels.Status.Writer);

            var client = new SpaceMoltHttpClient();
            client.DebugContext = label;

            try
            {
                var sessionTimer = Stopwatch.StartNew();
                await client.CreateSessionAsync();
                LogAuth($"{flowLabel} | {label} | session_created | {sessionTimer.ElapsedMilliseconds}ms");

                var authTimer = Stopwatch.StartNew();
                var password = await authenticateAsync(client);
                LogAuth($"{flowLabel} | {label} | authenticated | {authTimer.ElapsedMilliseconds}ms");

                try
                {
                    var mapTimer = Stopwatch.StartNew();
                    await client.GetMapSnapshotAsync(forceRefresh: false)
                        .WaitAsync(TimeSpan.FromSeconds(15));
                    LogAuth($"{flowLabel} | {label} | map_warmup_ok | {mapTimer.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    channels.Status.Writer.TryWrite(
                        $"Map warm-up skipped for '{label}': {ex.Message}");
                    LogAuth($"{flowLabel} | {label} | map_warmup_skipped | {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    var itemsTimer = Stopwatch.StartNew();
                    var itemCatalogById = await client.GetFullItemCatalogByIdAsync(forceRefresh: false)
                        .WaitAsync(TimeSpan.FromSeconds(20));
                    LogAuth(
                        $"{flowLabel} | {label} | item_catalog_warmup_ok | count={itemCatalogById.Count} | {itemsTimer.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    channels.Status.Writer.TryWrite(
                        $"Item catalog warm-up skipped for '{label}': {ex.Message}");
                    LogAuth($"{flowLabel} | {label} | item_catalog_warmup_skipped | {ex.GetType().Name}: {ex.Message}");
                }

                try
                {
                    var shipsTimer = Stopwatch.StartNew();
                    var shipCatalogById = await client.GetFullShipCatalogByIdAsync(forceRefresh: false)
                        .WaitAsync(TimeSpan.FromSeconds(20));
                    LogAuth(
                        $"{flowLabel} | {label} | ship_catalog_warmup_ok | count={shipCatalogById.Count} | {shipsTimer.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    channels.Status.Writer.TryWrite(
                        $"Ship catalog warm-up skipped for '{label}': {ex.Message}");
                    LogAuth($"{flowLabel} | {label} | ship_catalog_warmup_skipped | {ex.GetType().Name}: {ex.Message}");
                }

                LogAuth($"{flowLabel} | {label} | session_ready | {totalTimer.ElapsedMilliseconds}ms");

                return (
                    new BotSession(
                        Guid.NewGuid().ToString("N"),
                        label,
                        agent,
                        client),
                    password);
            }
            catch (Exception ex)
            {
                LogAuth($"{flowLabel} | {label} | failed | {ex.GetType().Name}: {ex.Message}");
                client.Dispose();
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
                        async client =>
                        {
                            await client.LoginAsync(savedBot.Username.Trim(), savedBot.Password);
                            return savedBot.Password;
                        });

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

        Task RunBotLoopAsync(BotSession bot, CancellationToken token)
        {
            var runtime = new BotRuntime(
                bot.Label,
                bot.Agent,
                bot.Client,
                bot.ControlInputQueue.Reader,
                bot.GenerateScriptQueue.Reader,
                bot.SaveExampleQueue.Reader,
                () => ui.LoopEnabled,
                () => bot.LatestState,
                state => bot.LatestState = state,
                () => bot.LastHaltedSnapshotAt,
                value => bot.LastHaltedSnapshotAt = value,
                state => snapshotPublisher.PublishSnapshot(bot, state),
                message => AppendExecutionStatus(bot, message),
                LogAuth,
                TriggerGlobalStop,
                ScriptGenerationMaxAttempts);

            return runtime.RunAsync(token);
        }

        void StartBotWorker(BotSession session)
        {
            if (session.WorkerTask != null)
                return;

            session.WorkerTask = Task.Run(() => RunBotLoopAsync(session, session.WorkerCts.Token), session.WorkerCts.Token);
            LogAuth($"bot_worker | {session.Label} | started");
        }

        lock (botLock)
        {
            foreach (var session in botSessions.Values)
                StartBotWorker(session);
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
                                    client => client.RegisterAsync(username, empire, registrationCode));
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
                                    async client =>
                                    {
                                        await client.LoginAsync(username, password);
                                        return password;
                                    });
                            }

                            lock (botLock)
                            {
                                botSessions[session.Id] = session;
                                if (activeBotId == null)
                                    activeBotId = session.Id;
                            }
                            snapshotPublisher.LogBotTabsIfChanged("manual_add_added");
                            UpsertSavedBot(username, passwordToSave);
                            StartBotWorker(session);

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

                    while (channels.ControlInput.Reader.TryRead(out var newInput))
                    {
                        var active = GetActiveBot();
                        if (active == null)
                        {
                            channels.Status.Writer.TryWrite("No active bot selected.");
                            continue;
                        }

                        active.ControlInputQueue.Writer.TryWrite(newInput);
                    }

                    while (channels.GenerateScript.Reader.TryRead(out var generationInput))
                    {
                        var active = GetActiveBot();
                        if (active == null)
                        {
                            channels.Status.Writer.TryWrite("No active bot selected.");
                            continue;
                        }

                        active.GenerateScriptQueue.Writer.TryWrite(generationInput);
                    }

                    while (channels.SaveExample.Reader.TryRead(out _))
                    {
                        var active = GetActiveBot();
                        if (active == null)
                        {
                            channels.Status.Writer.TryWrite("No active bot selected.");
                            continue;
                        }

                        active.SaveExampleQueue.Writer.TryWrite(true);
                    }

                    while (channels.ExecuteScript.Reader.TryRead(out _))
                    {
                        var active = GetActiveBot();
                        if (active == null)
                        {
                            channels.Status.Writer.TryWrite("No active bot selected.");
                            continue;
                        }

                        var script = active.Agent.CurrentControlInput;
                        if (string.IsNullOrWhiteSpace(script))
                        {
                            channels.Status.Writer.TryWrite("No script loaded.");
                            continue;
                        }

                        active.ControlInputQueue.Writer.TryWrite(script);
                        channels.Status.Writer.TryWrite($"Restarting script for {active.Label}");
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
                        snapshot.ActiveMissionPrompts,
                        snapshot.Memory,
                        snapshot.ExecutionStatusLines,
                        snapshot.ControlInput,
                        snapshot.CurrentScriptLine,
                        snapshot.LastGenerationPrompt,
                        snapshot.Bots,
                        snapshot.ActiveBotId
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
        List<Task> workerTasks;
        lock (botLock)
        {
            sessionsToDispose = botSessions.Values.ToList();
            workerTasks = sessionsToDispose
                .Where(s => s.WorkerTask != null)
                .Select(s => s.WorkerTask!)
                .ToList();
        }

        foreach (var session in sessionsToDispose)
            session.WorkerCts.Cancel();

        foreach (var session in sessionsToDispose)
            session.Client.Dispose();

        await Task.WhenAll(
            botTask.ContinueWith(_ => { }),
            Task.WhenAll(workerTasks).ContinueWith(_ => { }),
            uiRenderTask.ContinueWith(_ => { })
        );
    }

}
