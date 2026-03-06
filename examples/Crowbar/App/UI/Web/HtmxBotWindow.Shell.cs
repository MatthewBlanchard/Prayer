using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public sealed partial class HtmxBotWindow
{
    private const string HtmxCdn = "https://unpkg.com/htmx.org@1.9.12";
    private const string CodeMirrorCssCdn = "https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.css";
    private const string CodeMirrorThemeCdn = "https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/theme/material-darker.min.css";
    private const string CodeMirrorJsCdn = "https://cdnjs.cloudflare.com/ajax/libs/codemirror/5.65.16/codemirror.min.js";

    private sealed record StateTabDefinition(
        string Id,
        string Label,
        string Trigger,
        bool ActiveOnLoad = false);

    private static readonly IReadOnlyList<StateTabDefinition> StateTabs = new[]
    {
        new StateTabDefinition("space", "Space", "load, every 1000ms", ActiveOnLoad: true),
        new StateTabDefinition("missions", "Missions", "load, every 1000ms"),
        new StateTabDefinition("trade", "Trade", "load, every 1000ms"),
        new StateTabDefinition("shipyard", "Shipyard", "load, every 1000ms"),
        new StateTabDefinition("catalog", "Catalog", "load"),
    };

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
        sb.Append("<base href='").Append(E(Url(""))).AppendLine("'>");
        sb.AppendLine($"<script src='{HtmxCdn}'></script>");
        sb.AppendLine($"<link rel='stylesheet' href='{CodeMirrorCssCdn}'>");
        sb.AppendLine($"<link rel='stylesheet' href='{CodeMirrorThemeCdn}'>");
        sb.AppendLine($"<script src='{CodeMirrorJsCdn}'></script>");
        sb.AppendLine("<style>");
        sb.AppendLine(UiCssAsset.Value);
        sb.AppendLine("</style>");
        sb.AppendLine("<link rel='stylesheet' href='assets/ui.css'>");
        sb.AppendLine("</head><body><div class='app'><div class='grid'>");

        AppendSidebarShellHtml(sb, providers, selectedProvider, selectedModel);
        AppendStateShellHtml(sb);
        AppendScriptShellHtml(sb, currentScript);

        sb.AppendLine("<script>");
        sb.AppendLine(UiJsAsset.Value);
        sb.AppendLine("</script>");
        sb.AppendLine("<script src='assets/ui.js'></script>");
        sb.AppendLine("</div></div></body></html>");
        return sb.ToString();
    }

    private void AppendSidebarShellHtml(
        StringBuilder sb,
        IReadOnlyList<string> providers,
        string selectedProvider,
        string selectedModel)
    {
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
    }

    private void AppendStateShellHtml(StringBuilder sb)
    {
        sb.AppendLine(
            $$"""
            <div class='state-column'>
              <div id='state-strip-inline' hx-get='partial/state-strip' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>
              <div id='state-panel' class='card'>
                <div class='tabs' role='tablist' aria-label='State Sections'>
            {{BuildStateTabsHtml()}}
                </div>
                <div class='tab-content'>
            {{BuildStatePanesHtml()}}
                </div>
              </div>
            </div>
            """);
    }

    private static string BuildStateTabsHtml()
    {
        return string.Join(
            Environment.NewLine,
            StateTabs.Select(tab =>
            {
                string tabId = $"tab-{tab.Id}";
                string paneId = $"state-pane-{tab.Id}";
                string selected = tab.ActiveOnLoad ? "true" : "false";
                string tabindex = tab.ActiveOnLoad ? "0" : "-1";
                return $"<button id='{tabId}' type='button' class='tab-btn' role='tab' data-tab='{tab.Id}' aria-selected='{selected}' aria-controls='{paneId}' tabindex='{tabindex}'>{E(tab.Label)}</button>";
            }));
    }

    private static string BuildStatePanesHtml()
    {
        return string.Join(
            Environment.NewLine,
            StateTabs.Select(tab =>
            {
                string paneId = $"state-pane-{tab.Id}";
                string tabId = $"tab-{tab.Id}";
                string activeClass = tab.ActiveOnLoad ? " active" : "";
                string hidden = tab.ActiveOnLoad ? "" : " hidden";
                return $"<div id='{paneId}' class='tab-pane{activeClass}' role='tabpanel' aria-labelledby='{tabId}'{hidden} hx-get='partial/state?tab={tab.Id}' hx-trigger='{tab.Trigger}' hx-swap='innerHTML'></div>";
            }));
    }

    private void AppendScriptShellHtml(StringBuilder sb, string currentScript)
    {
        sb.AppendLine("<div class='card'><h3>Script</h3>");
        sb.AppendLine("<h4>Current Script</h4><div id='live-script-editor'><textarea id='current-script-input' rows='5' readonly>")
            .Append(E(currentScript))
            .AppendLine("</textarea></div>");
        sb.AppendLine("<h4>Script</h4>");
        sb.AppendLine("<form id='script-form' hx-post='api/control-input' hx-swap='none' class='list'>");
        sb.Append("<textarea id='script-input' name='script' rows='7' placeholder='script'>").Append(E(currentScript)).AppendLine("</textarea>");
        sb.AppendLine("<button type='submit'>Set Script</button></form>");
        sb.AppendLine(
            "<div class='row' style='margin-top:8px;'><form hx-post='api/execute' hx-swap='none'><button id='execute-btn' class='execute-btn' type='submit' title='Execute'>▶️</button></form><form hx-post='api/halt' hx-swap='none'><button type='submit' title='Halt'>⏹️</button></form><form hx-post='api/save-example' hx-swap='none'><button type='submit' title='Thumbs Up'>👍</button></form></div>");
        sb.AppendLine("<h4>Prompt</h4><form id='prompt-form' hx-post='api/prompt' hx-swap='none' hx-on::after-request='window.handlePromptAfterRequest(event)' class='list'><textarea name='prompt' rows='4' placeholder='prompt for script generation'></textarea><button type='submit'>Generate Script</button></form>");
        sb.AppendLine("<form id='prompt-missions-form' hx-post='api/prompt-active-missions' hx-swap='none' hx-on::after-request='window.handlePromptAfterRequest(event)'><button type='submit'>Generate From Active Mission Objectives</button></form>");
        sb.AppendLine("<div id='right-panel' hx-get='partial/right' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div></div>");
    }
}
