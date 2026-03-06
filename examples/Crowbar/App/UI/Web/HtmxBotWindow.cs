using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading.Channels;

public sealed partial class HtmxBotWindow : IAppUi
{
    private static readonly Lazy<string> UiCssAsset = new(() => ReadUiAsset("ui.css"));
    private static readonly Lazy<string> UiJsAsset = new(() => ReadUiAsset("ui.js"));
    private static readonly string UiHttpErrorLogFile = Path.Combine(AppPaths.LogDir, "ui_http_errors.log");
    private static readonly string UiHttpTraceLogFile = Path.Combine(AppPaths.LogDir, "ui_http_trace.log");

    private readonly object _lock = new();
    private readonly string _prefix;
    private readonly string _routeBasePath;
    private bool _running;
    private HttpListener? _listener;

    private ChannelWriter<RuntimeCommandRequest>? _runtimeCommandWriter;
    private ChannelWriter<string>? _switchBotWriter;
    private ChannelWriter<AddBotRequest>? _addBotWriter;
    private ChannelWriter<LlmProviderSelection>? _llmSelectionWriter;
    private Func<string, string, Task<string>>? _generateScriptHandler;

    private string _selectedProvider = "llamacpp";
    private string _selectedModel = "model";
    private readonly List<string> _providers = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _modelsByProvider =
        new(StringComparer.OrdinalIgnoreCase);
    private UiSnapshot _snapshot = new(
        null,
        Array.Empty<string>(),
        null,
        null,
        null,
        Array.Empty<MissionPromptOption>(),
        Array.Empty<MissionPromptOption>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        null,
        null,
        Array.Empty<BotTab>(),
        null);

    public HtmxBotWindow(string prefix = "http://localhost:5057/")
    {
        _prefix = EnsureTrailingSlash(prefix);
        _routeBasePath = GetRouteBasePath(_prefix);
        _providers.Add("llamacpp");
        _modelsByProvider["llamacpp"] = new[] { "model" };
    }

    public void SetRuntimeCommandWriter(ChannelWriter<RuntimeCommandRequest> writer) => _runtimeCommandWriter = writer;
    public void SetSwitchBotWriter(ChannelWriter<string> writer) => _switchBotWriter = writer;
    public void SetAddBotWriter(ChannelWriter<AddBotRequest> writer) => _addBotWriter = writer;
    public void SetLlmSelectionWriter(ChannelWriter<LlmProviderSelection> writer) => _llmSelectionWriter = writer;
    public void SetGenerateScriptHandler(Func<string, string, Task<string>> handler) => _generateScriptHandler = handler;

    public void ConfigureInitialLlmSelection(string provider, string model)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(provider))
                _selectedProvider = provider.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(model))
                _selectedModel = model.Trim();
        }
    }

    public void SetAvailableProviders(IReadOnlyList<string> providers)
    {
        lock (_lock)
        {
            _providers.Clear();
            foreach (var provider in providers ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(provider))
                    continue;

                var normalized = provider.Trim().ToLowerInvariant();
                if (_providers.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                    continue;

                _providers.Add(normalized);
                if (!_modelsByProvider.ContainsKey(normalized))
                    _modelsByProvider[normalized] = new[] { "model" };
            }

            if (_providers.Count == 0)
                _providers.Add("llamacpp");
        }
    }

    public void SetProviderModels(string provider, IReadOnlyList<string> models)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedProvider))
            return;

        var normalizedModels = (models ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedModels.Count == 0)
            return;

        lock (_lock)
        {
            _modelsByProvider[normalizedProvider] = normalizedModels;
            if (!_providers.Contains(normalizedProvider, StringComparer.OrdinalIgnoreCase))
                _providers.Add(normalizedProvider);
        }
    }

    public void Render(
        SpaceUiModel? spaceModel,
        IReadOnlyList<string> spaceConnectedSystems,
        TradeUiModel? tradeModel,
        ShipyardUiModel? shipyardModel,
        CatalogUiModel? catalogModel,
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
        IReadOnlyList<MissionPromptOption> availableMissionPrompts,
        IReadOnlyList<string> memory,
        IReadOnlyList<string> executionStatusLines,
        string? controlInput,
        int? currentScriptLine,
        string? lastGenerationPrompt,
        IReadOnlyList<BotTab> bots,
        string? activeBotId)
    {
        lock (_lock)
        {
            _snapshot = new UiSnapshot(
                spaceModel,
                spaceConnectedSystems,
                tradeModel,
                shipyardModel,
                catalogModel,
                activeMissionPrompts,
                availableMissionPrompts,
                memory,
                executionStatusLines,
                controlInput,
                currentScriptLine,
                lastGenerationPrompt,
                bots,
                activeBotId);
        }
    }

    public void Run()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _running = true;

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            StopListener();
        };

        Console.WriteLine($"HTMX UI listening on {_prefix}");

        while (_running)
        {
            HttpListenerContext? ctx = null;
            try
            {
                ctx = _listener.GetContext();
            }
            catch
            {
                if (!_running)
                    break;
            }

            if (ctx == null)
                continue;

            _ = Task.Run(() => HandleRequestSafely(ctx));
        }
    }

    private void HandleRequestSafely(HttpListenerContext ctx)
    {
        var started = DateTime.UtcNow;
        var requestPath = ctx.Request?.Url?.AbsolutePath ?? "/";
        try
        {
            HandleRequest(ctx);
        }
        catch (Exception ex)
        {
            try
            {
                LogUiHttpError("request_unhandled_exception", ex, ctx.Request);
            }
            catch
            {
                // Never throw from error logging.
            }
            WriteText(ctx.Response, $"Internal server error: {ex.Message}", "text/plain", 500);
        }
        finally
        {
            try
            {
                if (requestPath.EndsWith("/api/prompt", StringComparison.OrdinalIgnoreCase) ||
                    requestPath.EndsWith("/api/prompt-active-missions", StringComparison.OrdinalIgnoreCase))
                {
                    LogUiHttpTrace(
                        "request_complete",
                        ctx.Request,
                        ctx.Response?.StatusCode ?? 0,
                        (DateTime.UtcNow - started).TotalMilliseconds);
                }
            }
            catch
            {
                // Never throw from trace logging.
            }

            try
            {
                ctx.Response.OutputStream.Close();
            }
            catch
            {
                // Listener may already be stopping.
            }
        }
    }

    private void HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var path = NormalizeRoutePath(req.Url?.AbsolutePath ?? "/");

        if (req.HttpMethod == "GET" && path == "/")
        {
            WriteText(ctx.Response, BuildShellHtml(), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/assets/ui.css")
        {
            WriteText(ctx.Response, UiCssAsset.Value, "text/css; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/assets/ui.js")
        {
            WriteText(ctx.Response, UiJsAsset.Value, "application/javascript; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/bots")
        {
            WriteText(ctx.Response, BuildBotsHtml(), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/state")
        {
            var tab = req.QueryString["tab"];
            WriteText(ctx.Response, BuildStateHtml(tab), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/state-strip")
        {
            WriteText(ctx.Response, BuildStateStripHtml(), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/right")
        {
            WriteText(ctx.Response, BuildRightPanelHtml(), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/models")
        {
            var provider = (req.QueryString["provider"] ?? "").Trim().ToLowerInvariant();
            WriteText(ctx.Response, BuildModelSelectHtml(provider), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/llm-summary")
        {
            WriteText(ctx.Response, BuildLlmSummaryHtml(), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/bootstrap/editor-data")
        {
            WriteText(ctx.Response, BuildEditorBootstrapJson(), "application/json; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/current-script")
        {
            WriteText(ctx.Response, BuildCurrentScriptStateJson(), "application/json; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/prompt")
        {
            var form = ReadForm(req);
            var prompt = GetValue(form, "prompt");
            string? activeBotId;
            lock (_lock) activeBotId = _snapshot.ActiveBotId;
            LogUiHttpTrace(
                "prompt_request_received",
                req,
                statusCode: null,
                elapsedMs: null,
                details: $"prompt_len={(prompt ?? string.Empty).Length} active_bot={(string.IsNullOrWhiteSpace(activeBotId) ? "none" : "present")}");
            if (string.IsNullOrWhiteSpace(prompt))
            {
                WriteText(ctx.Response, "Prompt is required.", "text/plain; charset=utf-8", 400);
                return;
            }

            if (string.IsNullOrWhiteSpace(activeBotId))
            {
                WriteText(ctx.Response, "No active bot selected.", "text/plain; charset=utf-8", 400);
                return;
            }

            if (_generateScriptHandler == null)
            {
                WriteText(ctx.Response, "Script generation is not configured.", "text/plain; charset=utf-8", 500);
                return;
            }

            try
            {
                var generatedScript = _generateScriptHandler(activeBotId!, prompt).GetAwaiter().GetResult();
                LogUiHttpTrace(
                    "prompt_request_generated",
                    req,
                    statusCode: null,
                    elapsedMs: null,
                    details: $"script_len={(generatedScript ?? string.Empty).Length}");
                WriteText(ctx.Response, generatedScript, "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                LogUiHttpError("prompt_request_failed", ex, req);
                WriteText(ctx.Response, ex.Message, "text/plain; charset=utf-8", 400);
            }
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/prompt-active-missions")
        {
            UiSnapshot snapshot;
            lock (_lock) snapshot = _snapshot;

            var prompt = BuildActiveMissionObjectivesPrompt(
                snapshot.ActiveMissionPrompts);
            LogUiHttpTrace(
                "prompt_active_missions_request_received",
                req,
                statusCode: null,
                elapsedMs: null,
                details: $"prompt_len={(prompt ?? string.Empty).Length} active_bot={(string.IsNullOrWhiteSpace(snapshot.ActiveBotId) ? "none" : "present")}");

            if (string.IsNullOrWhiteSpace(snapshot.ActiveBotId))
            {
                WriteText(ctx.Response, "No active bot selected.", "text/plain; charset=utf-8", 400);
                return;
            }

            if (_generateScriptHandler == null)
            {
                WriteText(ctx.Response, "Script generation is not configured.", "text/plain; charset=utf-8", 500);
                return;
            }

            try
            {
                var generatedScript = _generateScriptHandler(snapshot.ActiveBotId!, prompt).GetAwaiter().GetResult();
                LogUiHttpTrace(
                    "prompt_active_missions_generated",
                    req,
                    statusCode: null,
                    elapsedMs: null,
                    details: $"script_len={(generatedScript ?? string.Empty).Length}");
                WriteText(ctx.Response, generatedScript, "text/plain; charset=utf-8");
            }
            catch (Exception ex)
            {
                LogUiHttpError("prompt_active_missions_failed", ex, req);
                WriteText(ctx.Response, ex.Message, "text/plain; charset=utf-8", 400);
            }
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/control-input")
        {
            var form = ReadForm(req);
            var script = GetValue(form, "script");
            string? activeBotId;
            lock (_lock) activeBotId = _snapshot.ActiveBotId;
            if (!string.IsNullOrWhiteSpace(script) && !string.IsNullOrWhiteSpace(activeBotId))
            {
                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    activeBotId!,
                    RuntimeCommandNames.SetScript,
                    script));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/execute")
        {
            string? activeBotId;
            lock (_lock) activeBotId = _snapshot.ActiveBotId;
            if (!string.IsNullOrWhiteSpace(activeBotId))
            {
                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    activeBotId!,
                    RuntimeCommandNames.ExecuteScript));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/halt")
        {
            string? targetBotId;
            lock (_lock) targetBotId = _snapshot.ActiveBotId;
            if (!string.IsNullOrWhiteSpace(targetBotId))
            {
                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    targetBotId!,
                    RuntimeCommandNames.Halt));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/save-example")
        {
            var form = ReadForm(req);
            var script = GetValue(form, "script");
            string? activeBotId;
            lock (_lock) activeBotId = _snapshot.ActiveBotId;
            if (!string.IsNullOrWhiteSpace(activeBotId))
            {
                if (!string.IsNullOrWhiteSpace(script))
                {
                    _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                        activeBotId!,
                        RuntimeCommandNames.SetScript,
                        script));
                }

                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    activeBotId!,
                    RuntimeCommandNames.SaveExample));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/switch-bot")
        {
            var form = ReadForm(req);
            var botId = GetValue(form, "bot_id");
            if (!string.IsNullOrWhiteSpace(botId))
                _switchBotWriter?.TryWrite(botId);
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/llm-select")
        {
            var form = ReadForm(req);
            var provider = (GetValue(form, "provider") ?? "").Trim().ToLowerInvariant();
            var model = (GetValue(form, "model") ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(provider))
            {
                lock (_lock)
                {
                    _selectedProvider = provider;
                    if (!string.IsNullOrWhiteSpace(model))
                        _selectedModel = model;
                }
                _llmSelectionWriter?.TryWrite(new LlmProviderSelection(provider, model));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/add-bot")
        {
            var form = ReadForm(req);
            var mode = (GetValue(form, "mode") ?? "login").Trim().ToLowerInvariant();
            var username = (GetValue(form, "username") ?? "").Trim();
            var password = (GetValue(form, "password") ?? "").Trim();
            var regCode = (GetValue(form, "registration_code") ?? "").Trim();
            var empire = (GetValue(form, "empire") ?? "").Trim().ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(username))
            {
                if (mode == "register")
                {
                    _addBotWriter?.TryWrite(new AddBotRequest(
                        AddBotMode.Register,
                        username,
                        RegistrationCode: regCode,
                        Empire: empire));
                }
                else
                {
                    _addBotWriter?.TryWrite(new AddBotRequest(
                        AddBotMode.Login,
                        username,
                        Password: password));
                }
            }

            WriteNoContent(ctx.Response);
            return;
        }

        WriteText(ctx.Response, "Not found", "text/plain", 404);
    }

    private string BuildBotsHtml()
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;

        var sb = new StringBuilder();
        sb.AppendLine("<div class='list bot-list'>");
        foreach (var bot in snapshot.Bots)
        {
            var activeClass = bot.Id == snapshot.ActiveBotId ? " active" : "";
            sb.Append("<form hx-post='api/switch-bot' hx-swap='none'><input type='hidden' name='bot_id' value='")
                .Append(E(bot.Id)).Append("'><button class='bot-btn").Append(activeClass).Append("' type='submit'>")
                .Append(E(bot.Label)).AppendLine("</button></form>");
        }
        if (snapshot.Bots.Count == 0)
            sb.AppendLine("<div class='small'>(no bots loaded)</div>");
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    private string BuildStateHtml(string? tab)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var sb = new StringBuilder();
        var normalizedTab = (tab ?? "space").Trim().ToLowerInvariant();
        switch (normalizedTab)
        {
            case "trade":
                sb.Append(TradeTabRenderer.Build(snapshot.TradeModel));
                break;
            case "shipyard":
                sb.Append(ShipyardTabRenderer.Build(snapshot.ShipyardModel));
                break;
            case "missions":
                sb.Append(MissionsTabRenderer.Build(snapshot.ActiveMissionPrompts, snapshot.AvailableMissionPrompts));
                break;
            case "map":
                sb.Append(MapTabRenderer.Build(snapshot.SpaceModel));
                break;
            default:
                sb.Append(SpaceTabRenderer.Build(snapshot.SpaceModel, snapshot.SpaceConnectedSystems));
                break;
        }
        return sb.ToString();
    }

    private string BuildStateStripHtml()
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        return SpaceTabRenderer.BuildStateStrip(snapshot.SpaceModel);
    }

    private string BuildRightPanelHtml()
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-panel script-block'><div class='space-panel-title'>Execution</div><pre class='log-pre'>");
        foreach (var line in snapshot.ExecutionStatusLines)
            sb.Append(E(line)).AppendLine();
        sb.AppendLine("</pre></section>");
        return sb.ToString();
    }

    private static string BuildActiveMissionObjectivesPrompt(
        IReadOnlyList<MissionPromptOption> activeMissionPrompts)
    {
        var sb = new StringBuilder();
        bool wroteAnyLine = false;
        if (activeMissionPrompts != null && activeMissionPrompts.Count > 0)
        {
            foreach (var mission in activeMissionPrompts)
            {
                var objective = (mission.Prompt ?? string.Empty).Trim();
                var issuingPoi = (mission.IssuingPoiId ?? string.Empty).Trim();

                if (!string.IsNullOrWhiteSpace(objective))
                {
                    sb.Append("- ").AppendLine(objective);
                    wroteAnyLine = true;
                }

                if (!string.IsNullOrWhiteSpace(issuingPoi))
                {
                    sb.Append("- finish_quest: Go to ").AppendLine(issuingPoi);
                    wroteAnyLine = true;
                }
            }
        }

        if (!wroteAnyLine)
        {
            sb.AppendLine("- (no active mission objectives)");
        }

        return sb.ToString().Trim();
    }

    private string BuildLlmSummaryHtml()
    {
        string provider;
        string model;
        lock (_lock)
        {
            provider = _selectedProvider;
            model = _selectedModel;
        }

        return $"<span class='small'>LLM: {E(provider)}/{E(model)}</span>";
    }

    private static string BuildEditorBootstrapJson()
    {
        var highlightNames = LoadScriptHighlightNamesFromCache()
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var (systemNames, poiNames) = LoadMapNameHintsFromCache();

        return JsonSerializer.Serialize(new
        {
            commandNames = KnownCommandNames
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            scriptHighlightNames = highlightNames,
            systemHighlightNames = systemNames,
            poiHighlightNames = poiNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
        });
    }

    private static IReadOnlyCollection<string> LoadScriptHighlightNamesFromCache()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddName(string? value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                names.Add(trimmed);
        }

        var (systemNames, _) = LoadMapNameHintsFromCache();
        foreach (var system in systemNames)
            AddName(system);

        AddCatalogueNamesFromCache(AppPaths.ItemCatalogByIdCacheFile, names);
        AddCatalogueNamesFromCache(AppPaths.ShipCatalogByIdCacheFile, names);
        return names;
    }

    private static void AddCatalogueNamesFromCache(string path, HashSet<string> names)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            JsonElement entries;
            if (root.TryGetProperty("Entries", out var wrappedEntries) &&
                wrappedEntries.ValueKind == JsonValueKind.Object)
            {
                entries = wrappedEntries;
            }
            else
            {
                entries = root;
            }

            foreach (var entry in entries.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                    continue;

                if (!TryGetStringPropertyCaseInsensitive(entry.Value, "name", out var name))
                    continue;

                var trimmed = name.Trim();
                if (trimmed.Length > 0)
                    names.Add(trimmed);
            }
        }
        catch
        {
            // Ignore malformed cache files.
        }
    }

    private static readonly string[] KnownCommandNames =
    {
        "mine", "survey", "go", "accept_mission", "abandon_mission", "dock", "repair",
        "sell", "buy", "cancel_buy", "cancel_sell", "retrieve", "stash",
        "switch_ship", "install_mod", "uninstall_mod", "buy_ship", "buy_listed_ship",
        "commission_quote", "commission_ship", "commission_status", "sell_ship",
        "list_ship_for_sale", "wait", "halt"
    };

    private static (List<string> Systems, HashSet<string> Pois) LoadMapNameHintsFromCache()
    {
        var systems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pois = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(AppPaths.GalaxyMapFile))
            {
                using var mapDoc = JsonDocument.Parse(File.ReadAllText(AppPaths.GalaxyMapFile));
                if (TryGetArrayPropertyCaseInsensitive(mapDoc.RootElement, "systems", out var systemsElement))
                {
                    foreach (var system in systemsElement.EnumerateArray())
                    {
                        if (TryGetStringPropertyCaseInsensitive(system, "id", out var systemId) ||
                            TryGetStringPropertyCaseInsensitive(system, "system_id", out systemId))
                            systems.Add(systemId.Trim());

                        if (TryGetArrayPropertyCaseInsensitive(system, "pois", out var poisElement))
                        {
                            foreach (var poi in poisElement.EnumerateArray())
                            {
                                if (TryGetStringPropertyCaseInsensitive(poi, "id", out var poiId))
                                    pois.Add(poiId.Trim());
                                if (TryGetStringPropertyCaseInsensitive(poi, "name", out var poiName))
                                    pois.Add(poiName.Trim());
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Best-effort only.
        }

        try
        {
            if (File.Exists(AppPaths.GalaxyKnownPoisFile))
            {
                using var knownDoc = JsonDocument.Parse(File.ReadAllText(AppPaths.GalaxyKnownPoisFile));
                JsonElement knownPoisArray = default;
                bool hasKnownPoisArray =
                    knownDoc.RootElement.ValueKind == JsonValueKind.Array ||
                    TryGetArrayPropertyCaseInsensitive(knownDoc.RootElement, "pois", out knownPoisArray);

                if (knownDoc.RootElement.ValueKind == JsonValueKind.Array)
                    knownPoisArray = knownDoc.RootElement;

                if (hasKnownPoisArray && knownPoisArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var poi in knownPoisArray.EnumerateArray())
                    {
                        if (TryGetStringPropertyCaseInsensitive(poi, "id", out var poiId))
                            pois.Add(poiId.Trim());
                        if (TryGetStringPropertyCaseInsensitive(poi, "name", out var poiName))
                            pois.Add(poiName.Trim());
                        if (TryGetStringPropertyCaseInsensitive(poi, "systemId", out var systemId))
                            systems.Add(systemId.Trim());
                    }
                }
            }
        }
        catch
        {
            // Best-effort only.
        }

        return (systems.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList(), pois);
    }

    private static bool TryGetStringPropertyCaseInsensitive(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var prop in element.EnumerateObject())
        {
            if (!prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind != JsonValueKind.String)
                return false;

            value = prop.Value.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool TryGetArrayPropertyCaseInsensitive(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var prop in element.EnumerateObject())
        {
            if (!prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (prop.Value.ValueKind != JsonValueKind.Array)
                return false;

            value = prop.Value;
            return true;
        }

        return false;
    }

    private string BuildCurrentScriptStateJson()
    {
        string script;
        int? currentScriptLine;
        lock (_lock)
        {
            script = _snapshot.ControlInput ?? string.Empty;
            currentScriptLine = _snapshot.CurrentScriptLine;
        }

        return JsonSerializer.Serialize(new
        {
            script,
            currentScriptLine
        });
    }

    private string BuildModelSelectHtml(string provider, string? preferredModel = null)
    {
        var normalizedProvider = string.IsNullOrWhiteSpace(provider)
            ? "llamacpp"
            : provider.Trim().ToLowerInvariant();

        IReadOnlyList<string> models;
        lock (_lock)
        {
            if (!_modelsByProvider.TryGetValue(normalizedProvider, out models!) || models.Count == 0)
                models = new[] { "model" };
        }

        var selectedModel = preferredModel;
        if (string.IsNullOrWhiteSpace(selectedModel) || !models.Contains(selectedModel, StringComparer.Ordinal))
            selectedModel = models[0];

        var sb = new StringBuilder();
        sb.AppendLine("<select id='model-select' name='model'>");
        foreach (var model in models)
        {
            var selected = model.Equals(selectedModel, StringComparison.Ordinal) ? " selected" : "";
            sb.Append("<option value='").Append(E(model)).Append("'").Append(selected).Append(">")
                .Append(E(model)).AppendLine("</option>");
        }
        sb.AppendLine("</select>");
        return sb.ToString();
    }

    private static Dictionary<string, string> ReadForm(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
        var body = reader.ReadToEnd();
        return ParseForm(body);
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (body ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = pair.IndexOf('=');
            var key = idx < 0 ? pair : pair[..idx];
            var value = idx < 0 ? "" : pair[(idx + 1)..];
            result[UrlDecode(key)] = UrlDecode(value);
        }
        return result;
    }

    private static string? GetValue(Dictionary<string, string> form, string key)
        => form.TryGetValue(key, out var value) ? value : null;

    private static string UrlDecode(string value)
    {
        var normalized = (value ?? "").Replace("+", " ");
        try
        {
            return WebUtility.UrlDecode(normalized) ?? "";
        }
        catch
        {
            return normalized;
        }
    }

    private static void LogUiHttpError(string context, Exception ex, HttpListenerRequest? request)
    {
        var method = request?.HttpMethod ?? "(unknown)";
        var path = request?.Url?.AbsolutePath ?? "(unknown)";
        var rawUrl = request?.RawUrl ?? "(unknown)";
        var line = $"[{DateTime.UtcNow:O}] {context} | method={method} | path={path} | raw={rawUrl}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}";
        File.AppendAllText(UiHttpErrorLogFile, line);
    }

    private static void LogUiHttpTrace(
        string context,
        HttpListenerRequest? request,
        int? statusCode,
        double? elapsedMs,
        string? details = null)
    {
        var method = request?.HttpMethod ?? "(unknown)";
        var path = request?.Url?.AbsolutePath ?? "(unknown)";
        var rawUrl = request?.RawUrl ?? "(unknown)";
        var status = statusCode.HasValue ? statusCode.Value.ToString() : "-";
        var elapsed = elapsedMs.HasValue ? elapsedMs.Value.ToString("F1") : "-";
        var suffix = string.IsNullOrWhiteSpace(details) ? "" : $" | {details}";
        var line = $"[{DateTime.UtcNow:O}] {context} | method={method} | path={path} | raw={rawUrl} | status={status} | elapsed_ms={elapsed}{suffix}{Environment.NewLine}";
        File.AppendAllText(UiHttpTraceLogFile, line);
    }

    private static string E(string value) => WebUtility.HtmlEncode(value ?? "");

    private static string ReadUiAsset(string fileName)
    {
        if (TryReadEmbeddedAsset(fileName, out var embedded))
            return embedded;

        var filePath = FindUiAssetPath(fileName);
        if (filePath != null)
            return File.ReadAllText(filePath, Encoding.UTF8);

        return fileName.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            ? "body{margin:0;font-family:ui-monospace,monospace;background:#0f1115;color:#d7dae0}"
            : "console.error('Missing UI asset: " + fileName.Replace("'", "\\'", StringComparison.Ordinal) + "');";
    }

    private static bool TryReadEmbeddedAsset(string fileName, out string content)
    {
        content = string.Empty;
        var assembly = typeof(HtmxBotWindow).Assembly;
        var suffix = $".UI.Web.Assets.{fileName}";
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal));
        if (resourceName == null)
            return false;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return false;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        content = reader.ReadToEnd();
        return true;
    }

    private static string? FindUiAssetPath(string fileName)
    {
        var searchRoots = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory()
        };

        foreach (var root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            DirectoryInfo? current;
            try
            {
                current = new DirectoryInfo(root);
            }
            catch
            {
                continue;
            }

            for (var depth = 0; depth < 8 && current != null; depth++)
            {
                var candidates = new[]
                {
                    Path.Combine(current.FullName, "examples", "Crowbar", "App", "UI", "Web", "Assets", fileName),
                    Path.Combine(current.FullName, "src", "UI", "Web", "Assets", fileName)
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }

    private static string EnsureTrailingSlash(string prefix)
        => string.IsNullOrWhiteSpace(prefix)
            ? "http://localhost:5057/"
            : prefix.EndsWith("/", StringComparison.Ordinal) ? prefix : prefix + "/";

    private string NormalizeRoutePath(string absolutePath)
    {
        var path = string.IsNullOrWhiteSpace(absolutePath) ? "/" : absolutePath;
        if (string.IsNullOrEmpty(_routeBasePath))
            return path;

        if (path.Equals(_routeBasePath, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(_routeBasePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        var prefix = _routeBasePath + "/";
        if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return path[_routeBasePath.Length..];

        return path;
    }

    private static string GetRouteBasePath(string prefix)
    {
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri))
            return string.Empty;

        var path = uri.AbsolutePath ?? "/";
        if (string.IsNullOrWhiteSpace(path) || path == "/")
            return string.Empty;

        var normalized = path.TrimEnd('/');
        return normalized.StartsWith("/", StringComparison.Ordinal)
            ? normalized
            : "/" + normalized;
    }

    private string Url(string relativePath)
    {
        var trimmed = (relativePath ?? string.Empty).Trim().TrimStart('/');
        if (trimmed.Length == 0)
            return string.IsNullOrEmpty(_routeBasePath) ? "/" : _routeBasePath + "/";

        if (string.IsNullOrEmpty(_routeBasePath))
            return "/" + trimmed;

        return _routeBasePath + "/" + trimmed;
    }

    private static void WriteText(HttpListenerResponse response, string body, string contentType, int status = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteNoContent(HttpListenerResponse response)
    {
        response.StatusCode = 204;
        response.ContentLength64 = 0;
    }

    private void StopListener()
    {
        _running = false;
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
            // Shutdown best effort.
        }
    }
}
