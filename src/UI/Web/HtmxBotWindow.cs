using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Channels;

public sealed class HtmxBotWindow : IAppUi
{
    private readonly object _lock = new();
    private readonly string _prefix;
    private bool _loopEnabled;
    private bool _running;
    private HttpListener? _listener;

    private ChannelWriter<string>? _controlInputWriter;
    private ChannelWriter<string>? _generateScriptWriter;
    private ChannelWriter<bool>? _saveExampleWriter;
    private ChannelWriter<bool>? _executeScriptWriter;
    private ChannelWriter<bool>? _haltNowWriter;
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
        _providers.Add("llamacpp");
        _modelsByProvider["llamacpp"] = new[] { "model" };
    }

    public bool LoopEnabled
    {
        get
        {
            lock (_lock) return _loopEnabled;
        }
    }

    public void SetControlInputWriter(ChannelWriter<string> writer) => _controlInputWriter = writer;
    public void SetGenerateScriptWriter(ChannelWriter<string> writer) => _generateScriptWriter = writer;
    public void SetSaveExampleWriter(ChannelWriter<bool> writer) => _saveExampleWriter = writer;
    public void SetExecuteScriptWriter(ChannelWriter<bool> writer) => _executeScriptWriter = writer;
    public void SetHaltNowWriter(ChannelWriter<bool> writer) => _haltNowWriter = writer;
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
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
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
                spaceStateMarkdown,
                tradeStateMarkdown,
                shipyardStateMarkdown,
                cantinaStateMarkdown,
                activeMissionPrompts,
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
            WriteText(ctx.Response, BuildStateHtml(), "text/html; charset=utf-8");
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
            lock (_lock)
            {
                _loopEnabled = string.Equals(GetValue(form, "loop"), "on", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetValue(form, "loop"), "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(GetValue(form, "loop"), "1", StringComparison.OrdinalIgnoreCase);
            }
            WriteNoContent(ctx.Response);
            return;
        }

        if (req.HttpMethod == "POST" && path == "/api/loop-toggle")
        {
            bool loopEnabled;
            lock (_lock)
            {
                _loopEnabled = !_loopEnabled;
                loopEnabled = _loopEnabled;
            }

            WriteText(ctx.Response, BuildLoopButtonFormHtml(loopEnabled), "text/html; charset=utf-8");
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

        lock (_lock)
        {
            providers = _providers.ToList();
            selectedProvider = _selectedProvider;
            selectedModel = _selectedModel;
            currentScript = _snapshot.ControlInput ?? "";
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
        sb.AppendLine("#state-panel { overflow-y:auto; }");
        sb.AppendLine("#right-panel { overflow-y:auto; }");
        sb.AppendLine("</style></head><body><div class='app'><div class='grid'>");

        sb.AppendLine("<div class='card'><h3>Control</h3>");
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
        sb.AppendLine("<button type='submit'>Apply LLM</button></form>");
        sb.AppendLine("<h4>Bots</h4><div id='bots-panel' hx-get='/partial/bots' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("<h4>Add Bot</h4><form hx-post='/api/add-bot' hx-swap='none' class='list'>");
        sb.AppendLine("<select name='mode'><option value='login'>login</option><option value='register'>register</option></select>");
        sb.AppendLine("<input name='username' placeholder='username'><input name='password' placeholder='password'><input name='registration_code' placeholder='registration code'><input name='empire' placeholder='empire (for register)'>");
        sb.AppendLine("<button type='submit'>Add Bot</button></form></div>");

        sb.AppendLine("<div id='state-panel' class='card' hx-get='/partial/state' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");

        sb.AppendLine("<div class='card'><h3>Script</h3>");
        sb.AppendLine("<form hx-post='/api/control-input' hx-swap='none' class='list'>");
        sb.Append("<textarea name='script' rows='7' placeholder='script'>").Append(E(currentScript)).AppendLine("</textarea>");
        sb.AppendLine("<button type='submit'>Set Script</button></form>");
        lock (_lock)
        {
            sb.AppendLine("<div class='row' style='margin-top:8px;'><form hx-post='/api/execute' hx-swap='none'><button type='submit' title='Execute'>▶️</button></form><form hx-post='/api/halt' hx-swap='none'><button type='submit' title='Halt'>⏹️</button></form><form hx-post='/api/save-example' hx-swap='none'><button type='submit' title='Thumbs Up'>👍</button></form>" + BuildLoopButtonFormHtml(_loopEnabled) + "</div>");
        }
        sb.AppendLine("<h4>Prompt</h4><form hx-post='/api/prompt' hx-swap='none' class='list'><textarea name='prompt' rows='4' placeholder='prompt for script generation'></textarea><button type='submit'>Generate Script</button></form>");
        sb.AppendLine("<div id='right-panel' hx-get='/partial/right' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div></div>");

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

    private string BuildStateHtml()
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;
        var sb = new StringBuilder();
        sb.AppendLine("<h3>State</h3><pre>").Append(E(snapshot.SpaceStateMarkdown)).AppendLine("</pre>");
        if (!string.IsNullOrWhiteSpace(snapshot.TradeStateMarkdown))
            sb.AppendLine("<h4>Trade</h4><pre>" + E(snapshot.TradeStateMarkdown!) + "</pre>");
        if (!string.IsNullOrWhiteSpace(snapshot.ShipyardStateMarkdown))
            sb.AppendLine("<h4>Shipyard</h4><pre>" + E(snapshot.ShipyardStateMarkdown!) + "</pre>");
        if (!string.IsNullOrWhiteSpace(snapshot.CantinaStateMarkdown))
            sb.AppendLine("<h4>Cantina</h4><pre>" + E(snapshot.CantinaStateMarkdown!) + "</pre>");
        return sb.ToString();
    }

    private string BuildRightPanelHtml()
    {
        UiSnapshot snapshot;
        bool loopEnabled;
        lock (_lock)
        {
            snapshot = _snapshot;
            loopEnabled = _loopEnabled;
        }
        var sb = new StringBuilder();
        sb.AppendLine($"<div class='small'>Loop: {(loopEnabled ? "ON" : "OFF")}</div>");
        sb.AppendLine("<h4>Current Script (live)</h4><pre>").Append(E(snapshot.ControlInput ?? "(none)")).AppendLine("</pre>");
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

    private static string BuildLoopButtonFormHtml(bool loopEnabled)
    {
        var activeClass = loopEnabled ? " active" : "";
        var title = loopEnabled ? "Loop On" : "Loop Off";
        return $"<form id='loop-btn-form' hx-post='/api/loop-toggle' hx-target='this' hx-swap='outerHTML'><button class='{activeClass}' type='submit' title='{title}'>🔁</button></form>";
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
