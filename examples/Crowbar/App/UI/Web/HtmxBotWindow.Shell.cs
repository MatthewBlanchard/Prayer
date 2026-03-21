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
        bool ActiveOnLoad = false,
        bool RequiresDocked = false);

    private static readonly IReadOnlyList<StateTabDefinition> StateTabs = new[]
    {
        new StateTabDefinition("map", "Map", "load, every 1000ms", ActiveOnLoad: true),
        new StateTabDefinition("shipyard", "Ship", "load, every 1000ms"),
        new StateTabDefinition("skills", "Skills", "load, every 1000ms"),
        new StateTabDefinition("missions", "Missions", "load, every 1000ms"),
        new StateTabDefinition("trade", "Trade", "load, every 1000ms"),
        new StateTabDefinition("crafting", "Crafting", "load, every 1000ms", RequiresDocked: true),
    };

    private string BuildShellHtml()
    {
        List<string> providers;
        string selectedProvider;
        string selectedModel;
        string currentScript;
        string? defaultBotId;

        lock (_lock)
        {
            providers = _providers.ToList();
            selectedProvider = _selectedProvider;
            selectedModel = _selectedModel;
            defaultBotId = _snapshot.DefaultBotId;
            currentScript = GetBotState(_snapshot, null)?.ControlInput ?? "";
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
        AppendStateShellHtml(sb, defaultBotId);
        AppendScriptShellHtml(sb, currentScript);

        sb.AppendLine("<div id='self-destruct-confirm' class='confirm-layer'><div class='confirm-card'><div class='confirm-card-title'>Self Destruct</div><div class='confirm-card-body'>Are you sure? This will destroy your ship and cannot be undone.</div><div class='confirm-card-actions'><button class='confirm-btn-cancel' onclick='window.closeSelfDestructConfirm()'>Cancel</button><button class='confirm-btn-danger' onclick='window.executeSelfDestruct()'>Self Destruct</button></div></div></div>");
        sb.AppendLine("<script>");
        sb.Append("window._activeBotId = ").Append(defaultBotId != null ? $"'{E(defaultBotId)}'" : "null").AppendLine(";");
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
        sb.AppendLine("<div class='card sidebar'>");
        sb.AppendLine("<div class='sidebar-llm'><div id='llm-summary' class='sidebar-llm-name' hx-get='partial/llm-summary' hx-trigger='load, every 1000ms' hx-swap='innerHTML'>"
            + BuildLlmSummaryHtml()
            + "</div><button id='open-llm-settings' class='icon-btn' type='button' title='LLM Settings'>⚙</button></div>");
        sb.AppendLine("<div class='sidebar-header'><h3>Bots</h3><div class='sidebar-actions'><button id='open-add-bot' class='icon-btn' type='button' title='Add Bot'>+</button></div></div>");
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

    private void AppendStateShellHtml(StringBuilder sb, string? defaultBotId)
    {
        UiSnapshot snapshot;
        lock (_lock) snapshot = _snapshot;

        sb.AppendLine(
            $$"""
            <div class='state-column'>
              <div id='state-strip-inline' hx-get='partial/state-strip' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>
              <div id='state-panel' class='card'>
                <div id='state-tabs' class='tabs' role='tablist' aria-label='State Sections' hx-get='partial/state-tabs' hx-trigger='load, every 1000ms' hx-swap='innerHTML'>
            {{BuildStateTabsHtml(snapshot, defaultBotId)}}
                </div>
                <div class='tab-content'>
            {{BuildStatePanesHtml()}}
                </div>
              </div>
              <div id='tick-status' class='tick-status' hx-get='partial/tick-status' hx-trigger='load' hx-swap='innerHTML'></div>
            </div>
            """);
    }

    private static string BuildStateTabsHtml(UiSnapshot snapshot, string? botId)
    {
        var visibleTabs = GetVisibleStateTabs(snapshot, botId);
        return string.Join(
            Environment.NewLine,
            visibleTabs.Select(tab =>
            {
                string tabId = $"tab-{tab.Id}";
                string paneId = $"state-pane-{tab.Id}";
                string selected = tab.ActiveOnLoad ? "true" : "false";
                string tabindex = tab.ActiveOnLoad ? "0" : "-1";
                return $"<button id='{tabId}' type='button' class='tab-btn' role='tab' data-tab='{tab.Id}' aria-selected='{selected}' aria-controls='{paneId}' tabindex='{tabindex}'>{E(tab.Label)}</button>";
            }));
    }

    private static IReadOnlyList<StateTabDefinition> GetVisibleStateTabs(UiSnapshot snapshot, string? botId)
    {
        var isDocked = IsDocked(snapshot, botId);
        return StateTabs
            .Where(tab => !tab.RequiresDocked || isDocked)
            .ToArray();
    }

    private static bool IsDocked(UiSnapshot snapshot, string? botId)
    {
        var id = botId ?? snapshot.DefaultBotId;
        if (id == null) return false;
        snapshot.BotStates.TryGetValue(id, out var state);
        return state?.SpaceModel != null &&
               string.Equals((state.SpaceModel.Docked ?? string.Empty).Trim(), "true", StringComparison.OrdinalIgnoreCase);
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
        sb.AppendLine("<div class='card script-column'>");

        // Tab bar: Script | Roleplay | Skills
        sb.AppendLine("<div class='tabs' role='tablist' aria-label='Right Column'>");
        sb.AppendLine("<button id='right-tab-script' type='button' class='tab-btn' role='tab' data-right-tab='script' aria-selected='true' aria-controls='right-pane-script' tabindex='0'>Script</button>");
        sb.AppendLine("<button id='right-tab-roleplay' type='button' class='tab-btn' role='tab' data-right-tab='roleplay' aria-selected='false' aria-controls='right-pane-roleplay' tabindex='-1'>Roleplay</button>");
        sb.AppendLine("<button id='right-tab-skills' type='button' class='tab-btn' role='tab' data-right-tab='skills' aria-selected='false' aria-controls='right-pane-skills' tabindex='-1'>Skills</button>");
        sb.AppendLine("</div>");

        // Script pane
        sb.AppendLine("<div id='right-pane-script' class='tab-pane active' role='tabpanel' aria-labelledby='right-tab-script'>");
        sb.AppendLine("<div id='override-indicator' class='override-indicator' hidden></div>");
        sb.AppendLine("<section class='space-panel script-block'><div class='space-panel-title'>Current Script</div><div id='live-script-editor'><textarea id='current-script-input' rows='5' readonly>")
            .Append(E(currentScript))
            .AppendLine("</textarea></div></section>");
        sb.AppendLine("<section class='space-panel script-block'><div class='space-panel-title'>Edit Script</div>");
        sb.AppendLine("<form id='script-form' hx-post='api/control-input' hx-swap='none' class='list'>");
        sb.Append("<textarea id='script-input' name='script' rows='7' placeholder='script'>").Append(E(currentScript)).AppendLine("</textarea>");
        sb.AppendLine("<button type='submit'>Set Script</button></form>");
        sb.AppendLine(
            "<div class='row script-actions'><form hx-post='api/execute' hx-swap='none'><button id='execute-btn' class='execute-btn' type='submit' title='Execute'>▶️</button></form><form hx-post='api/halt' hx-swap='none'><button type='submit' title='Halt'>⏹️</button></form><form hx-post='api/save-example' hx-swap='none'><button type='submit' title='Thumbs Up'>👍</button></form></div></section>");
        sb.AppendLine("<section class='space-panel script-block'><div class='space-panel-title'>Prompt</div><form id='prompt-form' hx-post='api/prompt' hx-swap='none' hx-on::after-request='window.handlePromptAfterRequest(event)' class='list'><textarea name='prompt' rows='4' placeholder='prompt for script generation'></textarea><button type='submit'>Generate Script</button></form>");
        sb.AppendLine("</section>");
        sb.AppendLine("<div id='right-panel' class='space-page' hx-get='partial/right' hx-trigger='load, every 1000ms' hx-swap='innerHTML'></div>");
        sb.AppendLine("</div>"); // end right-pane-script

        // Roleplay pane
        sb.AppendLine("<div id='right-pane-roleplay' class='tab-pane' role='tabpanel' aria-labelledby='right-tab-roleplay' hidden>");
        sb.AppendLine("<div hx-get='partial/roleplay' hx-trigger='load' hx-swap='innerHTML'></div>");
        sb.AppendLine("</div>"); // end right-pane-roleplay

        // Skills pane
        sb.AppendLine("<div id='right-pane-skills' class='tab-pane' role='tabpanel' aria-labelledby='right-tab-skills' hidden>");
        sb.AppendLine("<div id='skills-library' hx-get='partial/skills-library' hx-trigger='revealed' hx-swap='innerHTML'></div>");
        sb.AppendLine("</div>"); // end right-pane-skills

        sb.AppendLine("</div>"); // end script-column
    }
}
