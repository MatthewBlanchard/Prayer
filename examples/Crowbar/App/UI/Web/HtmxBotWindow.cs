using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

public sealed class HtmxBotWindow : IAppUi
{
    private static readonly Lazy<string> UiCssAsset = new(() => ReadUiAsset("ui.css"));
    private static readonly Lazy<string> UiJsAsset = new(() => ReadUiAsset("ui.js"));

    private readonly object _lock = new();
    private readonly string _prefix;
    private readonly string _routeBasePath;
    private bool _running;
    private HttpListener? _listener;

    private ChannelWriter<RuntimeCommandRequest>? _runtimeCommandWriter;
    private ChannelWriter<LoopUpdate>? _loopUpdateWriter;
    private ChannelWriter<string>? _switchBotWriter;
    private ChannelWriter<AddBotRequest>? _addBotWriter;
    private ChannelWriter<LlmProviderSelection>? _llmSelectionWriter;

    private string _selectedProvider = "llamacpp";
    private string _selectedModel = "model";
    private readonly List<string> _providers = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _modelsByProvider =
        new(StringComparer.OrdinalIgnoreCase);
    private UiSnapshot _snapshot = new(
        "No bot logged in. Use Add Bot below.",
        null,
        null,
        null,
        null,
        Array.Empty<MissionPromptOption>(),
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        null,
        null,
        Array.Empty<BotTab>(),
        null,
        false);

    public HtmxBotWindow(string prefix = "http://localhost:5057/")
    {
        _prefix = EnsureTrailingSlash(prefix);
        _routeBasePath = GetRouteBasePath(_prefix);
        _providers.Add("llamacpp");
        _modelsByProvider["llamacpp"] = new[] { "model" };
    }

    public void SetRuntimeCommandWriter(ChannelWriter<RuntimeCommandRequest> writer) => _runtimeCommandWriter = writer;
    public void SetLoopUpdateWriter(ChannelWriter<LoopUpdate> writer) => _loopUpdateWriter = writer;
    public void SetSwitchBotWriter(ChannelWriter<string> writer) => _switchBotWriter = writer;
    public void SetAddBotWriter(ChannelWriter<AddBotRequest> writer) => _addBotWriter = writer;
    public void SetLlmSelectionWriter(ChannelWriter<LlmProviderSelection> writer) => _llmSelectionWriter = writer;

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
        string spaceStateMarkdown,
        string? tradeStateMarkdown,
        string? shipyardStateMarkdown,
        string? cantinaStateMarkdown,
        string? catalogStateMarkdown,
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
        IReadOnlyList<string> memory,
        IReadOnlyList<string> executionStatusLines,
        string? controlInput,
        int? currentScriptLine,
        string? lastGenerationPrompt,
        IReadOnlyList<BotTab> bots,
        string? activeBotId,
        bool activeBotLoopEnabled)
    {
        lock (_lock)
        {
            _snapshot = new UiSnapshot(
                spaceStateMarkdown,
                tradeStateMarkdown,
                shipyardStateMarkdown,
                cantinaStateMarkdown,
                catalogStateMarkdown,
                activeMissionPrompts,
                memory,
                executionStatusLines,
                controlInput,
                currentScriptLine,
                lastGenerationPrompt,
                bots,
                activeBotId,
                activeBotLoopEnabled);
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

            try
            {
                HandleRequest(ctx);
            }
            catch (Exception ex)
            {
                WriteText(ctx.Response, $"Internal server error: {ex.Message}", "text/plain", 500);
            }
            finally
            {
                ctx.Response.OutputStream.Close();
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

        if (req.HttpMethod == "GET" && path == "/partial/right")
        {
            WriteText(ctx.Response, BuildRightPanelHtml(), "text/html; charset=utf-8");
            return;
        }

        if (req.HttpMethod == "GET" && path == "/partial/loop-btn")
        {
            WriteText(ctx.Response, BuildLoopButtonHtml(), "text/html; charset=utf-8");
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
            if (!string.IsNullOrWhiteSpace(prompt) && !string.IsNullOrWhiteSpace(activeBotId))
            {
                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    activeBotId!,
                    RuntimeCommandNames.GenerateScript,
                    prompt));
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/prompt-active-missions")
        {
            UiSnapshot snapshot;
            lock (_lock) snapshot = _snapshot;

            var prompt = BuildActiveMissionObjectivesPrompt(
                snapshot.ActiveMissionPrompts,
                snapshot.CantinaStateMarkdown);
            if (!string.IsNullOrWhiteSpace(snapshot.ActiveBotId))
            {
                _runtimeCommandWriter?.TryWrite(new RuntimeCommandRequest(
                    snapshot.ActiveBotId!,
                    RuntimeCommandNames.GenerateScript,
                    prompt));
            }

            WriteNoContent(ctx.Response);
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
            string? activeBotId;
            lock (_lock) activeBotId = _snapshot.ActiveBotId;
            if (!string.IsNullOrWhiteSpace(activeBotId))
            {
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

        if (req.HttpMethod == "POST" && path == "/api/loop")
        {
            var form = ReadForm(req);
            var enabled = string.Equals(GetValue(form, "loop"), "on", StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetValue(form, "loop"), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetValue(form, "loop"), "1", StringComparison.OrdinalIgnoreCase);
            _loopUpdateWriter?.TryWrite(new LoopUpdate(enabled));
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/loop-toggle")
        {
            _loopUpdateWriter?.TryWrite(new LoopUpdate(null));
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

    private string BuildShellHtml()
    {
        List<string> providers;
        string selectedProvider;
        string selectedModel;
        string currentScript;
        bool activeBotLoopEnabled;

        lock (_lock)
        {
            providers = _providers.ToList();
            selectedProvider = _selectedProvider;
            selectedModel = _selectedModel;
            currentScript = _snapshot.ControlInput ?? "";
            activeBotLoopEnabled = _snapshot.ActiveBotLoopEnabled;
        }

        if (!providers.Contains(selectedProvider, StringComparer.OrdinalIgnoreCase))
            selectedProvider = providers.FirstOrDefault() ?? "llamacpp";

        if (!_modelsByProvider.TryGetValue(selectedProvider, out var models) || models.Count == 0)
            models = new[] { "model" };
        if (!models.Contains(selectedModel, StringComparer.Ordinal))
            selectedModel = models[0];

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang='en'><head>");
        sb.AppendLine("<meta charset='utf-8'>");
        sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'>");
        sb.AppendLine("<title>Servator (HTMX)</title>");
        sb.Append("<base href='").Append(E(Url(""))).AppendLine("'>");
        sb.AppendLine("<script src='https://unpkg.com/htmx.org@1.9.12'></script>");
        sb.AppendLine("<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.css'>");
        sb.AppendLine("<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/theme/material-darker.min.css'>");
        sb.AppendLine("<script src='https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.js'></script>");
        sb.AppendLine("<style>");
        sb.AppendLine(UiCssAsset.Value);
        sb.AppendLine("</style>");
        sb.AppendLine("<link rel='stylesheet' href='assets/ui.css'>");
        sb.AppendLine("</head><body><div class='app'><div class='grid'>");

        sb.AppendLine("<div class='card sidebar'><div class='sidebar-header'><h3>Bots</h3><div class='sidebar-actions'><button id='open-add-bot' class='icon-btn' type='button' title='Add Bot'>+</button></div></div>");
        sb.AppendLine("<div class='sidebar-llm'><div id='llm-summary' class='sidebar-llm-name' hx-get='partial/llm-summary' hx-trigger='load, every 1000ms' hx-swap='innerHTML'>"
            + BuildLlmSummaryHtml()
            + "</div><button id='open-llm-settings' class='icon-btn' type='button' title='LLM Settings'>⚙</button></div>");
        sb.AppendLine("<div id='bots-panel' hx-get='partial/bots' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='llm-panel-layer' class='panel-layer' data-layer><div class='panel-card'><div class='panel-card-header'><h4>LLM Settings</h4><button class='panel-card-close' data-close-layer type='button'>Close</button></div>");
        sb.AppendLine("<form hx-post='api/llm-select' hx-swap='none' class='list'>");
        sb.AppendLine("<label class='small'>Provider</label><select name='provider' hx-get='partial/models' hx-target='#model-select' hx-swap='outerHTML' hx-trigger='change'>");
        foreach (var provider in providers)
        {
            var selected = provider.Equals(selectedProvider, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
            sb.Append("<option value='").Append(E(provider)).Append("'").Append(selected).Append(">")
                .Append(E(provider)).AppendLine("</option>");
        }
        sb.AppendLine("</select><label class='small'>Model</label>");
        sb.AppendLine(BuildModelSelectHtml(selectedProvider, selectedModel));
        sb.AppendLine("<button type='submit'>Apply LLM</button></form></div></div>");
        sb.AppendLine("<div id='add-bot-panel-layer' class='panel-layer' data-layer><div class='panel-card'><div class='panel-card-header'><h4>Add Bot</h4><button class='panel-card-close' data-close-layer type='button'>Close</button></div><form hx-post='api/add-bot' hx-swap='none' class='list'>");
        sb.AppendLine("<select name='mode'><option value='login'>login</option><option value='register'>register</option></select>");
        sb.AppendLine("<input name='username' placeholder='username'><input name='password' placeholder='password'><input name='registration_code' placeholder='registration code'><input name='empire' placeholder='empire (for register)'>");
        sb.AppendLine("<button type='submit'>Add Bot</button></form></div></div></div>");

        sb.AppendLine("<div id='state-panel' class='card'>");
        sb.AppendLine("<h3>State</h3>");
        sb.AppendLine("<div class='tabs'>");
        sb.AppendLine("<button type='button' class='tab-btn active' data-tab='space'>Space</button>");
        sb.AppendLine("<button type='button' class='tab-btn' data-tab='trade'>Trade</button>");
        sb.AppendLine("<button type='button' class='tab-btn' data-tab='shipyard'>Shipyard</button>");
        sb.AppendLine("<button type='button' class='tab-btn' data-tab='cantina'>Cantina</button>");
        sb.AppendLine("<button type='button' class='tab-btn' data-tab='catalog'>Catalog</button>");
        sb.AppendLine("</div>");
        sb.AppendLine("<div class='tab-content'>");
        sb.AppendLine("<div id='state-pane-space' class='tab-pane active' hx-get='partial/state?tab=space' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='state-pane-trade' class='tab-pane' hx-get='partial/state?tab=trade' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='state-pane-shipyard' class='tab-pane' hx-get='partial/state?tab=shipyard' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='state-pane-cantina' class='tab-pane' hx-get='partial/state?tab=cantina' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='state-pane-catalog' class='tab-pane' hx-get='partial/state?tab=catalog' hx-trigger='load' hx-swap='innerHTML'></div>");
        sb.AppendLine("</div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h3>Script</h3>");
        sb.AppendLine("<h4>Current Script</h4><div id='live-script-editor'><textarea id='current-script-input' rows='5' readonly>")
            .Append(E(currentScript))
            .AppendLine("</textarea></div>");
        sb.AppendLine("<h4>Script</h4>");
        sb.AppendLine("<form id='script-form' hx-post='api/control-input' hx-swap='none' class='list'>");
        sb.Append("<textarea id='script-input' name='script' rows='7' placeholder='script'>").Append(E(currentScript)).AppendLine("</textarea>");
        sb.AppendLine("<button type='submit'>Set Script</button></form>");
        sb.AppendLine(
            "<div class='row' style='margin-top:8px;'><form hx-post='api/execute' hx-swap='none'><button type='submit' title='Execute'>▶️</button></form><form hx-post='api/halt' hx-swap='none'><button type='submit' title='Halt'>⏹️</button></form><form hx-post='api/save-example' hx-swap='none'><button type='submit' title='Thumbs Up'>👍</button></form><div id='loop-btn-slot' hx-get='partial/loop-btn' hx-trigger='load, every 1000ms' hx-swap='innerHTML'>"
            + BuildLoopButtonFormHtml(activeBotLoopEnabled)
            + "</div></div>");
        sb.AppendLine("<h4>Prompt</h4><form hx-post='api/prompt' hx-swap='none' class='list'><textarea name='prompt' rows='4' placeholder='prompt for script generation'></textarea><button type='submit'>Generate Script</button></form>");
        sb.AppendLine("<form hx-post='api/prompt-active-missions' hx-swap='none'><button type='submit'>Generate From Active Mission Objectives</button></form>");
        sb.AppendLine("<div id='right-panel' hx-get='partial/right' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div></div>");
        sb.AppendLine("<script>");
        sb.AppendLine(UiJsAsset.Value);
        sb.AppendLine("</script>");
        sb.AppendLine("<script src='assets/ui.js'></script>");
        sb.AppendLine("</div></div></body></html>");
        return sb.ToString();
    }

    private string BuildBotsHtml()
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;

        var sb = new StringBuilder();
        sb.AppendLine("<div class='list'>");
        foreach (var bot in snapshot.Bots)
        {
            var activeClass = bot.Id == snapshot.ActiveBotId ? " active" : "";
            sb.Append("<form hx-post='api/switch-bot' hx-swap='none'><input type='hidden' name='bot_id' value='")
                .Append(E(bot.Id)).Append("'><button class='").Append(activeClass).Append("' type='submit'>")
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
                sb.Append("<pre>").Append(E(snapshot.TradeStateMarkdown ?? "(trade unavailable)")).AppendLine("</pre>");
                break;
            case "shipyard":
                sb.Append("<pre>").Append(E(snapshot.ShipyardStateMarkdown ?? "(shipyard unavailable)")).AppendLine("</pre>");
                break;
            case "cantina":
                sb.Append("<pre>").Append(E(snapshot.CantinaStateMarkdown ?? "(cantina unavailable)")).AppendLine("</pre>");
                break;
            case "catalog":
                AppendCatalogHtml(sb, snapshot.CatalogStateMarkdown);
                break;
            default:
                sb.Append("<pre>").Append(E(snapshot.SpaceStateMarkdown)).AppendLine("</pre>");
                break;
        }
        return sb.ToString();
    }

    private static GalaxyMapSnapshot LoadGalaxyMapFromCache()
    {
        try
        {
            if (File.Exists(AppPaths.GalaxyMapFile))
            {
                var raw = File.ReadAllText(AppPaths.GalaxyMapFile);
                var parsed = JsonSerializer.Deserialize<GalaxyMapSnapshot>(raw);
                if (parsed != null)
                    return parsed;
            }
        }
        catch
        {
            // Best-effort only.
        }

        return new GalaxyMapSnapshot();
    }

    private void AppendCatalogHtml(StringBuilder sb, string? catalogState)
    {
        var raw = catalogState ?? "(catalog unavailable)";
        var lines = raw.Replace("\r\n", "\n").Split('\n');

        int itemsIndex = Array.IndexOf(lines, "ITEMS");
        int shipsIndex = Array.IndexOf(lines, "SHIPS");

        if (itemsIndex < 0 || shipsIndex < 0 || shipsIndex <= itemsIndex)
        {
            sb.Append("<pre>").Append(E(raw)).AppendLine("</pre>");
            return;
        }

        var itemsBody = string.Join("\n", lines.Skip(itemsIndex + 1).Take(shipsIndex - itemsIndex - 1)).Trim();
        var shipsBody = string.Join("\n", lines.Skip(shipsIndex + 1)).Trim();
        var itemLines = itemsBody.Length == 0
            ? Array.Empty<string>()
            : itemsBody.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var shipLines = shipsBody.Length == 0
            ? Array.Empty<string>()
            : shipsBody.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        sb.AppendLine("<input class='catalog-search' type='search' placeholder='Search catalog...' oninput='window.filterCatalogEntries(this.value)'>");
        sb.AppendLine($"<details class='catalog-group' open><summary>Items ({itemLines.Length})</summary>");
        sb.AppendLine("<div class='catalog-list'>");
        if (itemLines.Length == 0)
        {
            sb.AppendLine("<div class='catalog-entry' data-search=''>- (no item catalog entries)</div>");
        }
        else
        {
            foreach (var line in itemLines)
            {
                sb.Append("<div class='catalog-entry' data-search='")
                    .Append(E(line.ToLowerInvariant()))
                    .Append("'>")
                    .Append(E(line))
                    .AppendLine("</div>");
            }
        }
        sb.AppendLine("</div></details>");

        sb.AppendLine($"<details class='catalog-group'><summary>Ships ({shipLines.Length})</summary>");
        sb.AppendLine("<div class='catalog-list'>");
        if (shipLines.Length == 0)
        {
            sb.AppendLine("<div class='catalog-entry' data-search=''>- (no ship catalog entries)</div>");
        }
        else
        {
            foreach (var line in shipLines)
            {
                sb.Append("<div class='catalog-entry' data-search='")
                    .Append(E(line.ToLowerInvariant()))
                    .Append("'>")
                    .Append(E(line))
                    .AppendLine("</div>");
            }
        }
        sb.AppendLine("</div></details>");
    }

    private string BuildRightPanelHtml()
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var sb = new StringBuilder();
        sb.AppendLine($"<div class='small'>Loop: {(snapshot.ActiveBotLoopEnabled ? "ON" : "OFF")}</div>");
        sb.AppendLine("<h4>Execution</h4><pre>");
        foreach (var line in snapshot.ExecutionStatusLines)
            sb.Append(E(line)).AppendLine();
        sb.AppendLine("</pre>");

        sb.AppendLine("<h4>Memory</h4><pre>");
        foreach (var line in snapshot.Memory)
            sb.Append(E(line)).AppendLine();
        sb.AppendLine("</pre>");
        return sb.ToString();
    }

    private string BuildLoopButtonHtml()
    {
        bool loopEnabled;
        lock (_lock) loopEnabled = _snapshot.ActiveBotLoopEnabled;
        return BuildLoopButtonFormHtml(loopEnabled);
    }

    private static string BuildActiveMissionObjectivesPrompt(
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
        string? cantinaStateMarkdown)
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

    private static string BuildLoopButtonFormHtml(bool loopEnabled)
    {
        var activeClass = loopEnabled ? " active" : "";
        var title = loopEnabled ? "Loop On" : "Loop Off";
        return $"<form id='loop-btn-form' hx-post='api/loop-toggle' hx-swap='none'><button class='{activeClass}' type='submit' title='{title}'>🔁</button></form>";
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
        => Uri.UnescapeDataString((value ?? "").Replace("+", " "));

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
