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
    private static readonly Lazy<string> UiJsAsset = new(() => string.Concat(
        ReadUiAsset("ui-core.js"),
        ReadUiAsset("ui-tick-bar.js"),
        ReadUiAsset("ui-script-editor.js"),
        ReadUiAsset("ui-execution.js"),
        ReadUiAsset("ui-nav.js"),
        ReadUiAsset("ui-catalogs.js"),
        ReadUiAsset("ui-galaxy-map.js"),
        ReadUiAsset("ui-state.js"),
        ReadUiAsset("ui-init.js")));

    private readonly object _lock = new();
    private readonly string _prefix;
    private readonly string _routeBasePath;
    private bool _running;
    private HttpListener? _listener;

    private ChannelWriter<RuntimeCommandRequest>? _runtimeCommandWriter;
    private ChannelWriter<AddBotRequest>? _addBotWriter;
    private ChannelWriter<LlmProviderSelection>? _llmSelectionWriter;
    private Func<string, string, Task<string>>? _generateScriptHandler;
    private Func<string, string, bool>? _roleplayStartHandler;  // botId, persona → isRunning
    private Action<string>? _roleplayStopHandler;               // botId
    private Func<string, (bool IsRunning, string? Status, string? Persona)>? _roleplayStatusHandler;
    private Func<string, IReadOnlyList<string>>? _roleplayTraceHandler;

    private string _selectedProvider = "llamacpp";
    private string _selectedModel = "model";
    private readonly List<string> _providers = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _modelsByProvider =
        new(StringComparer.OrdinalIgnoreCase);
    private UiSnapshot _snapshot = new(
        new Dictionary<string, BotStateEntry>(StringComparer.Ordinal),
        Array.Empty<BotRouteOverlay>(),
        Array.Empty<BotTab>(),
        Array.Empty<BotMapMarker>(),
        null);

    public HtmxBotWindow(string prefix = "http://localhost:5057/")
    {
        _prefix = EnsureTrailingSlash(prefix);
        _routeBasePath = GetRouteBasePath(_prefix);
        _providers.Add("llamacpp");
        _modelsByProvider["llamacpp"] = new[] { "model" };
    }

    public void SetRuntimeCommandWriter(ChannelWriter<RuntimeCommandRequest> writer) => _runtimeCommandWriter = writer;
    public void SetAddBotWriter(ChannelWriter<AddBotRequest> writer) => _addBotWriter = writer;
    public void SetLlmSelectionWriter(ChannelWriter<LlmProviderSelection> writer) => _llmSelectionWriter = writer;
    public void SetGenerateScriptHandler(Func<string, string, Task<string>> handler) => _generateScriptHandler = handler;
    public void SetRoleplayStartHandler(Func<string, string, bool> handler) => _roleplayStartHandler = handler;
    public void SetRoleplayStopHandler(Action<string> handler) => _roleplayStopHandler = handler;
    public void SetRoleplayStatusHandler(Func<string, (bool IsRunning, string? Status, string? Persona)> handler) => _roleplayStatusHandler = handler;
    public void SetRoleplayTraceHandler(Func<string, IReadOnlyList<string>> handler) => _roleplayTraceHandler = handler;

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
        IReadOnlyDictionary<string, BotStateEntry> botStates,
        IReadOnlyList<BotRouteOverlay> botRoutes,
        IReadOnlyList<BotTab> bots,
        IReadOnlyList<BotMapMarker> botMapMarkers,
        string? defaultBotId)
    {
        lock (_lock)
        {
            _snapshot = new UiSnapshot(botStates, botRoutes, bots, botMapMarkers, defaultBotId);
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
                if (ShouldTraceUiRequest(requestPath))
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
                ctx.Response?.OutputStream?.Close();
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
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildBotsHtml(botId), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/state")
        {
            var tab = req.QueryString["tab"];
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildStateHtml(tab, botId), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/state-tabs")
        {
            var botId = req.QueryString["bot_id"];
            UiSnapshot snapshot;
            lock (_lock) snapshot = _snapshot;
            WriteText(ctx.Response, BuildStateTabsHtml(snapshot, botId), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/state-strip")
        {
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildStateStripHtml(botId), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/right")
        {
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildRightPanelHtml(botId), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/tick-status")
        {
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildTickStatusHtml(botId), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/tick-status-data")
        {
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildTickStatusDataJson(botId), "application/json; charset=utf-8");
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
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildCurrentScriptStateJson(botId), "application/json; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/map-data")
        {
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildMapDataJson(botId), "application/json; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/prompt")
        {
            var form = ReadForm(req);
            var prompt = GetValue(form, "prompt");
            var botId = GetValue(form, "bot_id");
            LogUiHttpTrace(
                "prompt_request_received",
                req,
                statusCode: null,
                elapsedMs: null,
                details: $"prompt_len={(prompt ?? string.Empty).Length} bot={(string.IsNullOrWhiteSpace(botId) ? "none" : "present")}");
            if (string.IsNullOrWhiteSpace(prompt))
            {
                WriteText(ctx.Response, "Prompt is required.", "text/plain; charset=utf-8", 400);
                return;
            }

            if (string.IsNullOrWhiteSpace(botId))
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
                var generatedScript = _generateScriptHandler(botId!, prompt).GetAwaiter().GetResult();
                LogUiHttpTrace(
                    "prompt_request_generated",
                    req,
                    statusCode: null,
                    elapsedMs: null,
                    details: $"script_len={(generatedScript ?? string.Empty).Length}");
                WriteText(ctx.Response, generatedScript ?? string.Empty, "text/plain; charset=utf-8");
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
            var form = ReadForm(req);
            var botId = GetValue(form, "bot_id");
            UiSnapshot snapshot;
            lock (_lock) snapshot = _snapshot;

            if (string.IsNullOrWhiteSpace(botId))
            {
                WriteText(ctx.Response, "No active bot selected.", "text/plain; charset=utf-8", 400);
                return;
            }

            snapshot.BotStates.TryGetValue(botId!, out var botState);
            var activeMissions = botState?.ActiveMissionPrompts ?? Array.Empty<MissionPromptOption>();
            var prompt = BuildActiveMissionObjectivesPrompt(activeMissions);
            LogUiHttpTrace(
                "prompt_active_missions_request_received",
                req,
                statusCode: null,
                elapsedMs: null,
                details: $"prompt_len={(prompt ?? string.Empty).Length} bot={botId}");

            if (_generateScriptHandler == null)
            {
                WriteText(ctx.Response, "Script generation is not configured.", "text/plain; charset=utf-8", 500);
                return;
            }

            try
            {
                var generatedScript = _generateScriptHandler(botId!, prompt!).GetAwaiter().GetResult();
                LogUiHttpTrace(
                    "prompt_active_missions_generated",
                    req,
                    statusCode: null,
                    elapsedMs: null,
                    details: $"script_len={(generatedScript ?? string.Empty).Length}");
                WriteText(ctx.Response, generatedScript ?? string.Empty, "text/plain; charset=utf-8");
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
            var botId = GetValue(form, "bot_id");
            if (!string.IsNullOrWhiteSpace(script) && !string.IsNullOrWhiteSpace(botId))
            {
                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    botId!,
                    RuntimeCommandNames.SetScript,
                    script));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/execute")
        {
            var form = ReadForm(req);
            var botId = GetValue(form, "bot_id");
            if (!string.IsNullOrWhiteSpace(botId))
            {
                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    botId!,
                    RuntimeCommandNames.ExecuteScript));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/halt")
        {
            var form = ReadForm(req);
            var botId = GetValue(form, "bot_id");
            if (!string.IsNullOrWhiteSpace(botId))
            {
                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    botId!,
                    RuntimeCommandNames.Halt));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/save-example")
        {
            var form = ReadForm(req);
            var script = GetValue(form, "script");
            var botId = GetValue(form, "bot_id");
            if (!string.IsNullOrWhiteSpace(botId))
            {
                if (!string.IsNullOrWhiteSpace(script))
                {
                    _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                        botId!,
                        RuntimeCommandNames.SetScript,
                        script));
                }

                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    botId!,
                    RuntimeCommandNames.SaveExample));
            }
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

        if (req.HttpMethod == "GET" && path == "/partial/roleplay")
        {
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildRoleplayPaneHtml(botId), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/roleplay-status")
        {
            var botId = req.QueryString["bot_id"];
            WriteText(ctx.Response, BuildRoleplayStatusHtml(botId), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/roleplay-start")
        {
            var form = ReadForm(req);
            var botId = GetValue(form, "bot_id");
            var persona = GetValue(form, "persona");
            if (string.IsNullOrWhiteSpace(botId))
            {
                WriteText(ctx.Response, "No active bot selected.", "text/plain; charset=utf-8", 400);
                return;
            }
            if (string.IsNullOrWhiteSpace(persona))
            {
                WriteText(ctx.Response, "Persona is required.", "text/plain; charset=utf-8", 400);
                return;
            }
            if (_roleplayStartHandler == null)
            {
                WriteText(ctx.Response, "Roleplay is not configured.", "text/plain; charset=utf-8", 500);
                return;
            }
            _roleplayStartHandler(botId!, persona!);
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/roleplay-stop")
        {
            var form = ReadForm(req);
            var botId = GetValue(form, "bot_id");
            if (!string.IsNullOrWhiteSpace(botId))
                _roleplayStopHandler?.Invoke(botId!);
            WriteNoContent(ctx.Response);
            return;
        }

        WriteText(ctx.Response, "Not found", "text/plain", 404);
    }

    private string BuildBotsHtml(string? activeBotId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;

        var sb = new StringBuilder();
        sb.AppendLine("<div class='list bot-list'>");
        foreach (var bot in snapshot.Bots)
        {
            var activeClass = bot.Id == activeBotId ? " active" : "";
            sb.Append("<button class='bot-btn").Append(activeClass).Append("' onclick='window.selectBot(\"")
                .Append(E(bot.Id)).Append("\")'>")
                .Append("<span class='bot-color-dot' style='background:")
                .Append(E(bot.ColorHex))
                .Append("'></span><span>")
                .Append(E(bot.Label))
                .AppendLine("</span></button>");
        }
        if (snapshot.Bots.Count == 0)
            sb.AppendLine("<div class='small'>(no bots loaded)</div>");
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    private BotStateEntry? GetBotState(UiSnapshot snapshot, string? botId)
    {
        var id = botId ?? snapshot.DefaultBotId;
        if (id == null) return null;
        snapshot.BotStates.TryGetValue(id, out var state);
        return state;
    }

    private string BuildStateHtml(string? tab, string? botId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var botState = GetBotState(snapshot, botId);
        var sb = new StringBuilder();
        var normalizedTab = (tab ?? "map").Trim().ToLowerInvariant();
        switch (normalizedTab)
        {
            case "trade":
                sb.Append(TradeTabRenderer.Build(botState?.TradeModel));
                break;
            case "shipyard":
                sb.Append(ShipyardTabRenderer.Build(botState?.ShipyardModel));
                break;
            case "crafting":
                sb.Append(CraftingTabRenderer.Build(botState?.CraftingModel));
                break;
            case "skills":
                sb.Append(SkillsTabRenderer.Build(botState?.SkillsModel));
                break;
            case "missions":
                sb.Append(MissionsTabRenderer.Build(
                    botState?.ActiveMissionPrompts ?? Array.Empty<MissionPromptOption>(),
                    botState?.AvailableMissionPrompts ?? Array.Empty<MissionPromptOption>()));
                break;
            case "map":
            case "space":
            default:
                // Merge known systems across all bots so galaxy rendering is not tied
                // to whichever bot is currently selected.
                var allKnownSystems = BuildMergedKnownSystems(snapshot);
                sb.Append(MapTabRenderer.Build(botState?.SpaceModel, snapshot.BotMapMarkers, snapshot.BotRoutes, botId, allKnownSystems));
                break;
        }
        return sb.ToString();
    }

    private string BuildMapDataJson(string? botId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var botState = GetBotState(snapshot, botId);
        var allKnownSystems = BuildMergedKnownSystems(snapshot);
        return MapTabRenderer.BuildMapPayloadJson(
            botState?.SpaceModel,
            snapshot.BotMapMarkers,
            snapshot.BotRoutes,
            botId,
            allKnownSystems);
    }

    private static IReadOnlyList<SpaceUiSystemNode> BuildMergedKnownSystems(UiSnapshot snapshot)
    {
        return snapshot.BotStates.Values
            .Where(state => state?.SpaceModel?.LocalSystems != null)
            .SelectMany(state => state!.SpaceModel!.LocalSystems)
            .Where(system => system != null && !string.IsNullOrWhiteSpace(system.Id))
            .GroupBy(system => system.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToList();
                var withCoords = entries.FirstOrDefault(s => s.X.HasValue && s.Y.HasValue);
                var primary = withCoords ?? entries[0];
                var empire = entries
                    .Select(s => s.Empire)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
                var connections = entries
                    .SelectMany(s => s.Connections ?? Array.Empty<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new SpaceUiSystemNode(
                    Id: primary.Id,
                    X: primary.X,
                    Y: primary.Y,
                    Empire: empire,
                    IsStronghold: entries.Any(s => s.IsStronghold),
                    HasStation: entries.Any(s => s.HasStation),
                    IsCurrent: entries.Any(s => s.IsCurrent),
                    Connections: connections,
                    HasKnownPois: entries.Any(s => s.HasKnownPois),
                    IsExplored: entries.Any(s => s.IsExplored));
            })
            .OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string BuildStateStripHtml(string? botId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        return SpaceTabRenderer.BuildStateStrip(GetBotState(snapshot, botId)?.SpaceModel);
    }

    private string BuildRightPanelHtml(string? botId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var botState = GetBotState(snapshot, botId);
        var sb = new StringBuilder();
        sb.AppendLine("<section class='space-panel script-block'><div class='space-panel-title'>Execution</div><pre class='log-pre'>");
        foreach (var line in botState?.ExecutionStatusLines ?? Array.Empty<string>())
            sb.Append(E(line)).AppendLine();
        sb.AppendLine("</pre></section>");
        return sb.ToString();
    }

    private string BuildRoleplayPaneHtml(string? botId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var id = botId ?? snapshot.DefaultBotId;
        var botState = GetBotState(snapshot, botId);

        bool isRunning = false;
        string? status = null;
        string? persona = null;
        IReadOnlyList<string> toolTrace = Array.Empty<string>();

        if (id != null && _roleplayStatusHandler != null)
            (isRunning, status, persona) = _roleplayStatusHandler(id);
        if (id != null && _roleplayTraceHandler != null)
            toolTrace = _roleplayTraceHandler(id);
        else if (botState?.ExecutionStatusLines != null)
            toolTrace = botState.ExecutionStatusLines;

        return RoleplayTabRenderer.Build(isRunning, status ?? "", persona ?? "", toolTrace);
    }

    private string BuildRoleplayStatusHtml(string? botId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var id = botId ?? snapshot.DefaultBotId;
        var botState = GetBotState(snapshot, botId);

        bool isRunning = false;
        string? status = null;
        IReadOnlyList<string> toolTrace = Array.Empty<string>();

        if (id != null && _roleplayStatusHandler != null)
            (isRunning, status, _) = _roleplayStatusHandler(id);
        if (id != null && _roleplayTraceHandler != null)
            toolTrace = _roleplayTraceHandler(id);
        else if (botState?.ExecutionStatusLines != null)
            toolTrace = botState.ExecutionStatusLines;

        return RoleplayTabRenderer.BuildStatusInner(isRunning, status, toolTrace);
    }

    private string BuildTickStatusHtml(string? botId)
    {
        var tickData = BuildTickStatusData(botId);

        var sb = new StringBuilder();
        sb.Append("<div class='tick-status-shell' data-current-tick='")
            .Append(E(tickData.TickText))
            .Append("' data-last-post-utc='")
            .Append(E(tickData.LastPostIso))
            .AppendLine("'>");
        sb.AppendLine("<div class='tick-status-track'><div class='tick-status-fill' id='tick-status-fill'></div></div>");
        sb.Append("<div class='tick-status-meta'><span class='tick-meta-main'>Next in --</span><span class='tick-meta-post'>Last Prayer POST: ")
            .Append(E(tickData.LastPostText))
            .AppendLine("</span></div>");
        sb.AppendLine("</div>");
        return sb.ToString();
    }

    private string BuildTickStatusDataJson(string? botId)
    {
        var tickData = BuildTickStatusData(botId);
        return JsonSerializer.Serialize(new
        {
            currentTick = tickData.TickText,
            lastPostUtc = tickData.LastPostIso,
            lastPostText = tickData.LastPostText
        });
    }

    private (string TickText, string LastPostIso, string LastPostText) BuildTickStatusData(string? botId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var botState = GetBotState(snapshot, botId);

        var tickText = botState?.CurrentTick.HasValue == true
            ? botState.CurrentTick!.Value.ToString()
            : "N/A";
        var lastPostIso = botState?.LastSpaceMoltPostUtc.HasValue == true
            ? botState.LastSpaceMoltPostUtc!.Value.ToUniversalTime().ToString("O")
            : string.Empty;
        var lastPostText = botState?.LastSpaceMoltPostUtc.HasValue == true
            ? $"{Math.Max(0, (int)(DateTime.UtcNow - botState.LastSpaceMoltPostUtc!.Value).TotalSeconds)}s ago"
            : "n/a";

        return (tickText, lastPostIso, lastPostText);
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
        var payload = new Dictionary<string, object?>
        {
            ["commandNames"] = KnownCommandNames
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ["scriptHighlightNames"] = highlightNames,
            ["systemHighlightNames"] = systemNames,
            ["poiHighlightNames"] = poiNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
        };

        if (TryLoadGalaxyMapFromCache(out var galaxyMap))
            payload["galaxyMap"] = galaxyMap;

        return JsonSerializer.Serialize(payload);
    }

    private static bool TryLoadGalaxyMapFromCache(out JsonElement galaxyMap)
    {
        galaxyMap = default;
        try
        {
            if (!File.Exists(AppPaths.GalaxyMapFile))
                return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.GalaxyMapFile));
            galaxyMap = doc.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
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

    private string BuildCurrentScriptStateJson(string? botId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var botState = GetBotState(snapshot, botId);
        var script = botState?.ControlInput ?? string.Empty;
        var currentScriptLine = botState?.CurrentScriptLine;

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
        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.UiHttpError, line, AppPaths.UiHttpErrorLogFile));
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
        LogSink.Instance.Enqueue(new LogEvent(DateTime.UtcNow, LogKind.UiHttpTrace, line, AppPaths.UiHttpTraceLogFile));
    }

    private static bool ShouldTraceUiRequest(string? requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
            return false;

        return requestPath.StartsWith("/partial/", StringComparison.OrdinalIgnoreCase) ||
               requestPath.EndsWith("/api/prompt", StringComparison.OrdinalIgnoreCase) ||
               requestPath.EndsWith("/api/prompt-active-missions", StringComparison.OrdinalIgnoreCase);
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
