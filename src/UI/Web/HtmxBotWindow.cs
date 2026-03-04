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
    private readonly object _lock = new();
    private readonly string _prefix;
    private readonly string _editorBootstrapJson;
    private bool _running;
    private HttpListener? _listener;

    private ChannelWriter<string>? _controlInputWriter;
    private ChannelWriter<string>? _generateScriptWriter;
    private ChannelWriter<bool>? _saveExampleWriter;
    private ChannelWriter<bool>? _executeScriptWriter;
    private ChannelWriter<bool>? _haltNowWriter;
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
        _editorBootstrapJson = BuildEditorBootstrapJson();
        _providers.Add("llamacpp");
        _modelsByProvider["llamacpp"] = new[] { "model" };
    }

    public void SetControlInputWriter(ChannelWriter<string> writer) => _controlInputWriter = writer;
    public void SetGenerateScriptWriter(ChannelWriter<string> writer) => _generateScriptWriter = writer;
    public void SetSaveExampleWriter(ChannelWriter<bool> writer) => _saveExampleWriter = writer;
    public void SetExecuteScriptWriter(ChannelWriter<bool> writer) => _executeScriptWriter = writer;
    public void SetHaltNowWriter(ChannelWriter<bool> writer) => _haltNowWriter = writer;
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
        var path = req.Url?.AbsolutePath ?? "/";

        if (req.HttpMethod == "GET" && path == "/")
        {
            WriteText(ctx.Response, BuildShellHtml(), "text/html; charset=utf-8");
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
            WriteText(ctx.Response, _editorBootstrapJson, "application/json; charset=utf-8");
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
            if (!string.IsNullOrWhiteSpace(prompt))
                _generateScriptWriter?.TryWrite(prompt);
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/control-input")
        {
            var form = ReadForm(req);
            var script = GetValue(form, "script");
            if (!string.IsNullOrWhiteSpace(script))
                _controlInputWriter?.TryWrite(script);
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/execute")
        {
            _executeScriptWriter?.TryWrite(true);
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/halt")
        {
            _haltNowWriter?.TryWrite(true);
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/save-example")
        {
            _saveExampleWriter?.TryWrite(true);
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
        int? currentScriptLine;
        bool activeBotLoopEnabled;
        var commandNames = CommandCatalog.All
            .Select(c => c.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        lock (_lock)
        {
            providers = _providers.ToList();
            selectedProvider = _selectedProvider;
            selectedModel = _selectedModel;
            currentScript = _snapshot.ControlInput ?? "";
            currentScriptLine = _snapshot.CurrentScriptLine;
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
        sb.AppendLine("<script src='https://unpkg.com/htmx.org@1.9.12'></script>");
        sb.AppendLine("<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.css'>");
        sb.AppendLine("<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/theme/material-darker.min.css'>");
        sb.AppendLine("<script src='https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.js'></script>");
        sb.AppendLine("<style>");
        sb.AppendLine("html, body { height: 100%; }");
        sb.AppendLine("body { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; background:#0f1115; color:#d7dae0; margin:0; }");
        sb.AppendLine(".app { padding: 12px; height: 100vh; box-sizing: border-box; }");
        sb.AppendLine(".grid { display:grid; grid-template-columns: 280px 1fr 420px; gap:12px; align-items:stretch; height:100%; min-height:0; }");
        sb.AppendLine(".card { border:1px solid #2a2e38; background:#171a21; padding:10px; border-radius:8px; display:flex; flex-direction:column; min-height:0; }");
        sb.AppendLine("h3 { margin:0 0 8px 0; font-size:14px; color:#8eb8ff; }");
        sb.AppendLine("h4 { margin:10px 0 6px 0; font-size:12px; color:#8eb8ff; }");
        sb.AppendLine("pre { margin:0; white-space:pre-wrap; word-break:break-word; }");
        sb.AppendLine("textarea, select, input, button { width:100%; box-sizing:border-box; background:#0f1115; color:#d7dae0; border:1px solid #2a2e38; border-radius:6px; padding:6px; }");
        sb.AppendLine("button { cursor:pointer; } .row { display:flex; gap:8px; } .row > * { flex:1; } .small { font-size:12px; color:#9ca3b2; } .list { display:flex; flex-direction:column; gap:6px; } .active { border-color:#5ac977; }");
        sb.AppendLine(".sidebar { position:relative; overflow:hidden; }");
        sb.AppendLine(".sidebar-header { display:flex; align-items:center; justify-content:space-between; margin-bottom:8px; }");
        sb.AppendLine(".sidebar-header h3 { margin:0; }");
        sb.AppendLine(".sidebar-actions { display:flex; gap:6px; }");
        sb.AppendLine(".sidebar-llm { display:flex; align-items:center; gap:6px; margin-bottom:8px; }");
        sb.AppendLine(".sidebar-llm-name { flex:1; min-width:0; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }");
        sb.AppendLine(".icon-btn { width:auto; min-width:34px; padding:6px 10px; font-weight:700; line-height:1; }");
        sb.AppendLine(".panel-layer { position:absolute; inset:0; background:rgba(8,10,14,0.86); display:none; align-items:flex-start; justify-content:center; padding:10px; box-sizing:border-box; z-index:10; }");
        sb.AppendLine(".panel-layer.open { display:flex; }");
        sb.AppendLine(".panel-card { width:100%; max-height:100%; overflow-y:auto; border:1px solid #2a2e38; background:#171a21; border-radius:8px; padding:10px; box-sizing:border-box; }");
        sb.AppendLine(".panel-card-header { display:flex; justify-content:space-between; align-items:center; margin-bottom:8px; gap:8px; }");
        sb.AppendLine(".panel-card-header h4 { margin:0; }");
        sb.AppendLine(".panel-card-close { width:auto; min-width:68px; }");
        sb.AppendLine(".tabs { display:flex; gap:6px; margin-bottom:8px; }");
        sb.AppendLine(".tab-btn { width:auto; flex:1; background:#0f1115; border:1px solid #2a2e38; color:#d7dae0; border-radius:6px; padding:6px 8px; font-size:12px; }");
        sb.AppendLine(".tab-btn.active { border-color:#5ac977; color:#e8ffef; }");
        sb.AppendLine(".tab-pane { display:none; }");
        sb.AppendLine(".tab-pane.active { display:block; }");
        sb.AppendLine(".catalog-search { margin:0 0 8px 0; }");
        sb.AppendLine("details.catalog-group { margin-bottom:8px; border:1px solid #2a2e38; border-radius:6px; padding:6px; background:#11141a; }");
        sb.AppendLine("details.catalog-group > summary { cursor:pointer; color:#8eb8ff; }");
        sb.AppendLine(".catalog-list { display:flex; flex-direction:column; gap:4px; margin-top:6px; }");
        sb.AppendLine(".catalog-entry { white-space:pre-wrap; word-break:break-word; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }");
        sb.AppendLine(".CodeMirror { border:1px solid #2a2e38; border-radius:6px; height:190px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size:13px; }");
        sb.AppendLine("#live-script-editor .CodeMirror { height:140px; }");
        sb.AppendLine(".CodeMirror .run-line-active { background: rgba(90, 201, 119, 0.12); }");
        sb.AppendLine(".cm-s-material-darker .cm-atom { color:#ffd166; font-weight:600; }");
        sb.AppendLine(".script-code { white-space:pre-wrap; word-break:break-word; }");
        sb.AppendLine("#state-panel { overflow-y:auto; }");
        sb.AppendLine("#right-panel { overflow-y:auto; }");
        sb.AppendLine("</style></head><body><div class='app'><div class='grid'>");

        sb.AppendLine("<div class='card sidebar'><div class='sidebar-header'><h3>Bots</h3><div class='sidebar-actions'><button id='open-add-bot' class='icon-btn' type='button' title='Add Bot'>+</button></div></div>");
        sb.AppendLine("<div class='sidebar-llm'><div id='llm-summary' class='sidebar-llm-name' hx-get='/partial/llm-summary' hx-trigger='load, every 1000ms' hx-swap='innerHTML'>"
            + BuildLlmSummaryHtml()
            + "</div><button id='open-llm-settings' class='icon-btn' type='button' title='LLM Settings'>⚙</button></div>");
        sb.AppendLine("<div id='bots-panel' hx-get='/partial/bots' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='llm-panel-layer' class='panel-layer' data-layer><div class='panel-card'><div class='panel-card-header'><h4>LLM Settings</h4><button class='panel-card-close' data-close-layer type='button'>Close</button></div>");
        sb.AppendLine("<form hx-post='/api/llm-select' hx-swap='none' class='list'>");
        sb.AppendLine("<label class='small'>Provider</label><select name='provider' hx-get='/partial/models' hx-target='#model-select' hx-swap='outerHTML' hx-trigger='change'>");
        foreach (var provider in providers)
        {
            var selected = provider.Equals(selectedProvider, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
            sb.Append("<option value='").Append(E(provider)).Append("'").Append(selected).Append(">")
                .Append(E(provider)).AppendLine("</option>");
        }
        sb.AppendLine("</select><label class='small'>Model</label>");
        sb.AppendLine(BuildModelSelectHtml(selectedProvider, selectedModel));
        sb.AppendLine("<button type='submit'>Apply LLM</button></form></div></div>");
        sb.AppendLine("<div id='add-bot-panel-layer' class='panel-layer' data-layer><div class='panel-card'><div class='panel-card-header'><h4>Add Bot</h4><button class='panel-card-close' data-close-layer type='button'>Close</button></div><form hx-post='/api/add-bot' hx-swap='none' class='list'>");
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
        sb.AppendLine("<div id='state-pane-space' class='tab-pane active' hx-get='/partial/state?tab=space' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='state-pane-trade' class='tab-pane' hx-get='/partial/state?tab=trade' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='state-pane-shipyard' class='tab-pane' hx-get='/partial/state?tab=shipyard' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='state-pane-cantina' class='tab-pane' hx-get='/partial/state?tab=cantina' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<div id='state-pane-catalog' class='tab-pane' hx-get='/partial/state?tab=catalog' hx-trigger='load' hx-swap='innerHTML'></div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class='card'><h3>Script</h3>");
        sb.AppendLine("<h4>Current Script</h4><div id='live-script-editor'><textarea id='current-script-input' rows='5' readonly>")
            .Append(E(currentScript))
            .AppendLine("</textarea></div>");
        sb.AppendLine("<h4>Script</h4>");
        sb.AppendLine("<form id='script-form' hx-post='/api/control-input' hx-swap='none' class='list'>");
        sb.Append("<textarea id='script-input' name='script' rows='7' placeholder='script'>").Append(E(currentScript)).AppendLine("</textarea>");
        sb.AppendLine("<button type='submit'>Set Script</button></form>");
        sb.AppendLine(
            "<div class='row' style='margin-top:8px;'><form hx-post='/api/execute' hx-swap='none'><button type='submit' title='Execute'>▶️</button></form><form hx-post='/api/halt' hx-swap='none'><button type='submit' title='Halt'>⏹️</button></form><form hx-post='/api/save-example' hx-swap='none'><button type='submit' title='Thumbs Up'>👍</button></form><div id='loop-btn-slot' hx-get='/partial/loop-btn' hx-trigger='load, every 1000ms' hx-swap='innerHTML'>"
            + BuildLoopButtonFormHtml(activeBotLoopEnabled)
            + "</div></div>");
        sb.AppendLine("<h4>Prompt</h4><form hx-post='/api/prompt' hx-swap='none' class='list'><textarea name='prompt' rows='4' placeholder='prompt for script generation'></textarea><button type='submit'>Generate Script</button></form>");
        sb.AppendLine("<div id='right-panel' hx-get='/partial/right' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div></div>");

        sb.AppendLine("<script>");
        sb.AppendLine("document.addEventListener('click', function (e) {");
        sb.AppendLine("  var btn = e.target.closest('.tab-btn');");
        sb.AppendLine("  if (!btn) return;");
        sb.AppendLine("  var panel = document.getElementById('state-panel');");
        sb.AppendLine("  if (!panel) return;");
        sb.AppendLine("  var tab = btn.getAttribute('data-tab');");
        sb.AppendLine("  panel.querySelectorAll('.tab-btn').forEach(function (b) { b.classList.toggle('active', b === btn); });");
        sb.AppendLine("  panel.querySelectorAll('.tab-pane').forEach(function (p) { p.classList.remove('active'); });");
        sb.AppendLine("  var target = document.getElementById('state-pane-' + tab);");
        sb.AppendLine("  if (target) target.classList.add('active');");
        sb.AppendLine("});");
        sb.AppendLine("window.closeAllSidebarLayers = function () {");
        sb.AppendLine("  document.querySelectorAll('.panel-layer.open').forEach(function (layer) { layer.classList.remove('open'); });");
        sb.AppendLine("};");
        sb.AppendLine("window.openSidebarLayer = function (id) {");
        sb.AppendLine("  window.closeAllSidebarLayers();");
        sb.AppendLine("  var layer = document.getElementById(id);");
        sb.AppendLine("  if (layer) layer.classList.add('open');");
        sb.AppendLine("};");
        sb.AppendLine("document.addEventListener('click', function (e) {");
        sb.AppendLine("  var addBtn = e.target.closest('#open-add-bot');");
        sb.AppendLine("  if (addBtn) { window.openSidebarLayer('add-bot-panel-layer'); return; }");
        sb.AppendLine("  var llmBtn = e.target.closest('#open-llm-settings');");
        sb.AppendLine("  if (llmBtn) { window.openSidebarLayer('llm-panel-layer'); return; }");
        sb.AppendLine("  var closeBtn = e.target.closest('[data-close-layer]');");
        sb.AppendLine("  if (closeBtn) { window.closeAllSidebarLayers(); return; }");
        sb.AppendLine("  var openLayer = e.target.closest('.panel-layer.open');");
        sb.AppendLine("  if (openLayer && e.target === openLayer) window.closeAllSidebarLayers();");
        sb.AppendLine("});");
        sb.AppendLine("document.addEventListener('keydown', function (e) { if (e.key === 'Escape') window.closeAllSidebarLayers(); });");
        sb.AppendLine("document.body.addEventListener('htmx:afterRequest', function (e) {");
        sb.AppendLine("  var path = (((e || {}).detail || {}).pathInfo || {}).requestPath || '';");
        sb.AppendLine("  if (path === '/api/add-bot' || path === '/api/llm-select') window.closeAllSidebarLayers();");
        sb.AppendLine("});");
        sb.AppendLine("window.filterCatalogEntries = function (query) {");
        sb.AppendLine("  var q = (query || '').toLowerCase().trim();");
        sb.AppendLine("  document.querySelectorAll('#state-pane-catalog .catalog-entry').forEach(function (row) {");
        sb.AppendLine("    var hay = row.getAttribute('data-search') || '';");
        sb.AppendLine("    row.style.display = q === '' || hay.indexOf(q) >= 0 ? '' : 'none';");
        sb.AppendLine("  });");
        sb.AppendLine("};");
        sb.AppendLine("window.ensureScriptEditor = function () {");
        sb.AppendLine("  if (!window.CodeMirror) return;");
        sb.AppendLine("  var input = document.getElementById('script-input');");
        sb.AppendLine("  var liveInput = document.getElementById('current-script-input');");
        sb.AppendLine("  if ((!input && !liveInput) || (window._scriptEditor && window._liveScriptEditor)) return;");
        sb.Append("  var commands = [");
        for (int i = 0; i < commandNames.Count; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append("'").Append(Js(commandNames[i])).Append("'");
        }
        sb.AppendLine("];");
        sb.AppendLine("  window.buildNameRegex = function (names, lineStartOnly) {");
        sb.AppendLine("    if (!Array.isArray(names) || names.length === 0) return null;");
        sb.AppendLine("    var escaped = names");
        sb.AppendLine("      .filter(function (n) { return typeof n === 'string' && n.trim().length > 0; })");
        sb.AppendLine("      .map(function (n) { return n.trim().replace(/[.*+?^${}()|[\\]\\\\]/g, '\\\\$&'); })");
        sb.AppendLine("      .sort(function (a, b) { return b.length - a.length; });");
        sb.AppendLine("    if (escaped.length === 0) return null;");
        sb.AppendLine("    var core = '(?:' + escaped.join('|') + ')';");
        sb.AppendLine("    var pattern = lineStartOnly ? core + '\\\\b' : '\\\\b' + core + '\\\\b';");
        sb.AppendLine("    return new RegExp(pattern, 'i');");
        sb.AppendLine("  };");
        sb.AppendLine("  window._scriptCommandRegex = window.buildNameRegex(commands, true);");
        sb.AppendLine("  window._scriptSymbolRegex = null;");
        sb.AppendLine("  window.setScriptSymbols = function (nextSymbols) {");
        sb.AppendLine("    window._scriptSymbolRegex = window.buildNameRegex(nextSymbols || [], false);");
        sb.AppendLine("    if (window._scriptEditor) window._scriptEditor.refresh();");
        sb.AppendLine("    if (window._liveScriptEditor) window._liveScriptEditor.refresh();");
        sb.AppendLine("  };");
        sb.AppendLine("  window.loadEditorBootstrap = function () {");
        sb.AppendLine("    if (window._editorBootstrapLoaded) return;");
        sb.AppendLine("    window._editorBootstrapLoaded = true;");
        sb.AppendLine("    fetch('/bootstrap/editor-data', { cache: 'no-store' })");
        sb.AppendLine("      .then(function (res) { return res.ok ? res.json() : null; })");
        sb.AppendLine("      .then(function (data) {");
        sb.AppendLine("        if (!data) return;");
        sb.AppendLine("        if (Array.isArray(data.scriptHighlightNames)) window.setScriptSymbols(data.scriptHighlightNames);");
        sb.AppendLine("        if (data.galaxyMap) window._galaxyMap = data.galaxyMap;");
        sb.AppendLine("      })");
        sb.AppendLine("      .catch(function () { });");
        sb.AppendLine("  };");
        sb.AppendLine("  if (!CodeMirror.modes.spacemolt) {");
        sb.AppendLine("    CodeMirror.defineMode('spacemolt', function () {");
        sb.AppendLine("      return {");
        sb.AppendLine("        startState: function () { return { lineStart: true }; },");
        sb.AppendLine("        token: function (stream, state) {");
        sb.AppendLine("          if (stream.sol()) state.lineStart = true;");
        sb.AppendLine("          if (stream.eatSpace()) return null;");
        sb.AppendLine("          if (state.lineStart && window._scriptCommandRegex && stream.match(window._scriptCommandRegex, true, true)) {");
        sb.AppendLine("            state.lineStart = false;");
        sb.AppendLine("            return 'keyword';");
        sb.AppendLine("          }");
        sb.AppendLine("          if (stream.peek() === ';') {");
        sb.AppendLine("            stream.next();");
        sb.AppendLine("            state.lineStart = false;");
        sb.AppendLine("            return 'operator';");
        sb.AppendLine("          }");
        sb.AppendLine("          if (window._scriptSymbolRegex && stream.match(window._scriptSymbolRegex, true, true)) {");
        sb.AppendLine("            state.lineStart = false;");
        sb.AppendLine("            return 'atom';");
        sb.AppendLine("          }");
        sb.AppendLine("          stream.next();");
        sb.AppendLine("          state.lineStart = false;");
        sb.AppendLine("          return null;");
        sb.AppendLine("        }");
        sb.AppendLine("      };");
        sb.AppendLine("    });");
        sb.AppendLine("  }");
        sb.AppendLine("  if (input && !window._scriptEditor) {");
        sb.AppendLine("    var editor = CodeMirror.fromTextArea(input, {");
        sb.AppendLine("      mode: 'spacemolt',");
        sb.AppendLine("      theme: 'material-darker',");
        sb.AppendLine("      lineNumbers: true,");
        sb.AppendLine("      indentUnit: 2,");
        sb.AppendLine("      tabSize: 2,");
        sb.AppendLine("      indentWithTabs: false");
        sb.AppendLine("    });");
        sb.AppendLine("    window._scriptEditor = editor;");
        sb.AppendLine("    var form = document.getElementById('script-form');");
        sb.AppendLine("    if (form) form.addEventListener('submit', function () { editor.save(); });");
        sb.AppendLine("  }");
        sb.AppendLine("  if (liveInput && !window._liveScriptEditor) {");
        sb.AppendLine("    window._liveScriptEditor = CodeMirror.fromTextArea(liveInput, {");
        sb.AppendLine("      mode: 'spacemolt',");
        sb.AppendLine("      theme: 'material-darker',");
        sb.AppendLine("      lineNumbers: true,");
        sb.AppendLine("      readOnly: true");
        sb.AppendLine("    });");
        sb.AppendLine("    window._liveScriptRunLineHandle = null;");
        sb.AppendLine("    window._liveScriptRunLineNumber = null;");
        sb.AppendLine("  }");
        sb.AppendLine("  window.setLiveScriptRunLine = function (lineNumber) {");
        sb.AppendLine("    var editor = window._liveScriptEditor;");
        sb.AppendLine("    if (!editor) return;");
        sb.AppendLine("    var prev = window._liveScriptRunLineHandle;");
        sb.AppendLine("    if (typeof prev === 'number' && prev >= 0 && prev < editor.lineCount()) {");
        sb.AppendLine("      editor.removeLineClass(prev, 'background', 'run-line-active');");
        sb.AppendLine("    }");
        sb.AppendLine("    var next = (typeof lineNumber === 'number' && lineNumber > 0) ? Math.min(editor.lineCount() - 1, lineNumber - 1) : -1;");
        sb.AppendLine("    if (next >= 0) {");
        sb.AppendLine("      editor.addLineClass(next, 'background', 'run-line-active');");
        sb.AppendLine("      window._liveScriptRunLineHandle = next;");
        sb.AppendLine("    } else {");
        sb.AppendLine("      window._liveScriptRunLineHandle = null;");
        sb.AppendLine("    }");
        sb.AppendLine("    window._liveScriptRunLineNumber = (typeof lineNumber === 'number') ? lineNumber : null;");
        sb.AppendLine("  };");
        sb.AppendLine("  window.syncCurrentScript = function () {");
        sb.AppendLine("    fetch('/partial/current-script', { cache: 'no-store' })");
        sb.AppendLine("      .then(function (res) { return res.ok ? res.json() : null; })");
        sb.AppendLine("      .then(function (state) {");
        sb.AppendLine("        if (!state || !window._liveScriptEditor) return;");
        sb.AppendLine("        var text = typeof state.script === 'string' ? state.script : '';");
        sb.AppendLine("        var current = window._liveScriptEditor.getValue();");
        sb.AppendLine("        if (current !== text) window._liveScriptEditor.setValue(text);");
        sb.AppendLine("        var nextLine = (typeof state.currentScriptLine === 'number') ? state.currentScriptLine : null;");
        sb.AppendLine("        if (window._liveScriptRunLineNumber !== nextLine) window.setLiveScriptRunLine(nextLine);");
        sb.AppendLine("      })");
        sb.AppendLine("      .catch(function () { });");
        sb.AppendLine("  };");
        sb.AppendLine("  window.loadEditorBootstrap();");
        sb.AppendLine("  window.syncCurrentScript();");
        sb.AppendLine("  window.setLiveScriptRunLine(").Append(currentScriptLine?.ToString() ?? "null").AppendLine(");");
        sb.AppendLine("  if (!window._liveScriptPoller) window._liveScriptPoller = setInterval(window.syncCurrentScript, 1000);");
        sb.AppendLine("};");
        sb.AppendLine("document.addEventListener('htmx:afterSwap', function () {");
        sb.AppendLine("  window.ensureScriptEditor();");
        sb.AppendLine("  if (window._scriptEditor) window._scriptEditor.refresh();");
        sb.AppendLine("  if (window._liveScriptEditor) window._liveScriptEditor.refresh();");
        sb.AppendLine("});");
        sb.AppendLine("window.ensureScriptEditor();");
        sb.AppendLine("</script>");
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
            sb.Append("<form hx-post='/api/switch-bot' hx-swap='none'><input type='hidden' name='bot_id' value='")
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
        if (snapshot.ActiveMissionPrompts.Count > 0)
        {
            sb.AppendLine("<h4>Mission Prompts</h4><pre>");
            foreach (var m in snapshot.ActiveMissionPrompts)
                sb.Append("- ").Append(E(m.Label)).Append(": ").Append(E(m.Prompt)).AppendLine();
            sb.AppendLine("</pre>");
        }

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
        var galaxyMap = GalaxyMapSnapshotFile.Load(AppPaths.GalaxyMapFile);

        return JsonSerializer.Serialize(new
        {
            scriptHighlightNames = highlightNames,
            galaxyMap
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

        try
        {
            var map = GalaxyMapSnapshotFile.Load(AppPaths.GalaxyMapFile);
            foreach (var system in map.Systems ?? new List<GalaxySystemInfo>())
                AddName(system?.Id);
        }
        catch
        {
            // Keep startup resilient if cache parsing fails.
        }

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
        return $"<form id='loop-btn-form' hx-post='/api/loop-toggle' hx-swap='none'><button class='{activeClass}' type='submit' title='{title}'>🔁</button></form>";
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
    private static string Js(string value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string EnsureTrailingSlash(string prefix)
        => string.IsNullOrWhiteSpace(prefix)
            ? "http://localhost:5057/"
            : prefix.EndsWith("/", StringComparison.Ordinal) ? prefix : prefix + "/";

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
