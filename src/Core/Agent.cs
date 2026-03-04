using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading.Channels;

#region Agent

public class SpaceMoltAgent
{
    private const double PromptSearchMatchCutoff = 0.62d;
    private const string BaseSystemPrompt =
        "You are an autonomous agent playing the online game SpaceMolt. " +
        "Your objective is to pursue the active objective. " +
        "Make rational, goal-directed decisions based on the current game state. ";
    private static bool PlanningEnabled => false;
    private const string DefaultScriptGenerationExamples =
        "Go, sell, then mine ->\n" +
        "go system_a;\n" +
        "sell cargo;\n" +
        "mine asteroid_belt;\n\n" +
        "Go to Sol ->\n" +
        "go sol;\n\n" +
        "Sell your cargo ->\n" +
        "sell cargo;\n\n" +
        "Go to system, mine, and sell at other system ->\n" +
        "go system_a;\n" +
        "mine asteroid_belt;\n" +
        "go system_b;\n" +
        "sell cargo;";

    private readonly ILLMClient _plannerLlm;
    private readonly PromptScriptRag? _scriptExampleRag;
    private readonly bool _hierarchicalPlanningEnabled;
    private ControlMode _controlMode = ScriptMode.Instance;

    private string? _plan;
    private string? _objective;
    private string? _script;
    private string? _lastScriptGenerationPrompt;
    private string? _lastGeneratedScript;
    private int? _currentScriptLine;
    private Queue<CommandResult> _scriptQueue = new();
    private bool _isHalted;
    private readonly List<ScriptGenerationExample> _scriptGenerationExamples = new();

    public string? CurrentObjective
        => string.IsNullOrWhiteSpace(_objective)
            ? null
            : _objective;
    public bool IsHalted => _isHalted;
    public ControlModeKind CurrentControlModeKind => _controlMode.Kind;

    public string CurrentControlModeName => _controlMode.Name;
    public string? LastScriptGenerationPrompt => _lastScriptGenerationPrompt;
    public int? CurrentScriptLine => _currentScriptLine;

    public string? CurrentControlInput
        => string.IsNullOrWhiteSpace(_script) ? null : _script;

    private ChannelWriter<string>? _statusWriter;

    public void SetStatusWriter(ChannelWriter<string> writer)
    {
        _statusWriter = writer;
    }

    private void SetStatus(string status)
    {
        _statusWriter?.TryWrite(status);
    }

    public void SetObjective(string objective, bool clearCurrentPlan = true)
    {
        _objective = ParseObjective(objective);
        _isHalted = false;

        if (clearCurrentPlan)
            _plan = string.Empty;

        SetStatus("Objective set");
    }

    public void SetScript(
        string script,
        GameState? state = null,
        bool preserveAssociatedPrompt = false)
    {
        var rawScript = script ?? string.Empty;
        _currentScriptLine = null;
        var steps = (state == null
            ? DslInterpreter.Translate(rawScript)
            : DslInterpreter.Translate(rawScript, state)).ToList();
        _script = DslInterpreter.RenderScript(steps).TrimEnd();
        LogScriptNormalization("set_script", rawScript, _script);

        for (int i = 0; i < steps.Count; i++)
            steps[i].SourceLine = i + 1;

        if (!preserveAssociatedPrompt &&
            !string.Equals(_script, _lastGeneratedScript, StringComparison.Ordinal))
        {
            _lastScriptGenerationPrompt = null;
            _lastGeneratedScript = null;
        }
        else if (preserveAssociatedPrompt && !string.IsNullOrWhiteSpace(_lastScriptGenerationPrompt))
        {
            // Keep prompt association in sync with the edited script.
            _lastGeneratedScript = _script;
        }

        _scriptQueue = new Queue<CommandResult>(steps);
        _isHalted = false;
        _plan = string.Empty;

        SetStatus(_scriptQueue.Count == 0
            ? "Script loaded (empty)"
            : $"Script loaded ({_scriptQueue.Count} steps)");
    }

    public async Task<string> GenerateScriptFromUserInputAsync(
        string userInput,
        GameState state,
        int maxAttempts = 3)
    {
        var attempts = Math.Max(1, maxAttempts);
        var generationInput = userInput.Trim();
        var stateContextBlock = BuildScriptGenerationStateContextBlock(state, generationInput);
        var examplesBlock = await BuildScriptGenerationExamplesBlockAsync(generationInput);
        string? previousScript = null;
        string? previousError = null;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var prompt = AgentPrompts.BuildScriptFromUserInputPrompt(
                baseSystemPrompt: BaseSystemPrompt,
                userInput: generationInput,
                stateContextBlock: stateContextBlock,
                examplesBlock: examplesBlock,
                attemptNumber: attempt,
                previousScript: previousScript,
                previousError: previousError);

            await LogScriptWriterContextTokensAsync(
                attempt,
                attempts,
                generationInput,
                stateContextBlock,
                examplesBlock,
                previousScript,
                previousError,
                prompt);
            await LogPlannerPromptAsync($"script_generation_attempt_{attempt}", prompt);

            var result = await _plannerLlm.CompleteAsync(
                prompt,
                maxTokens: 320,
                temperature: 0.2f,
                topP: 0.9f);

            var script = ExtractScript(result);

            try
            {
                var steps = DslInterpreter.Translate(script, state).ToList();
                var normalizedScript = DslInterpreter.RenderScript(steps).TrimEnd();
                LogScriptNormalization($"generation_attempt_{attempt}", script, normalizedScript);
                // Persist only the raw user request for prompt->script examples.
                _lastScriptGenerationPrompt = generationInput;
                _lastGeneratedScript = normalizedScript;
                return normalizedScript;
            }
            catch (FormatException ex)
            {
                previousScript = script;
                previousError = ex.Message;
                SetStatus($"Script generation retry {attempt}/{attempts}");
            }
        }

        throw new FormatException(
            "Failed to generate a valid script after retries. Last error: " +
            (previousError ?? "Unknown script error."));
    }

    public void SetControlMode(ControlMode mode, bool clearCurrentPlan = true)
    {
        _controlMode = mode ?? ScriptMode.Instance;
        _isHalted = false;

        if (clearCurrentPlan)
            _plan = string.Empty;

        SetStatus($"Mode: {_controlMode.Name}");
    }

    public bool InterruptActiveCommand(string reason = "Interrupted")
    {
        if (_activeCommand == null)
            return false;

        _activeCommand = null;
        _activeCommandResult = null;
        SetStatus(reason);
        return true;
    }

    public void Halt(string reason = "Halted")
    {
        _isHalted = true;
        _currentScriptLine = null;
        SetStatus(reason);
    }

    public void ResumeFromHalt(bool clearCurrentPlan = true, string reason = "Resumed")
    {
        _isHalted = false;
        if (clearCurrentPlan)
            _plan = string.Empty;
        SetStatus(reason);
    }

    private readonly List<ICommand> _commands;
    private readonly Dictionary<string, ICommand> _commandMap;

    private Queue<ActionMemory> _memory = new();
    private const int MaxMemory = 12;

    private IMultiTurnCommand? _activeCommand;
    private CommandResult? _activeCommandResult;

    public bool HasActiveCommand => _activeCommand != null;

    private record ActionMemory(
        string Action,
        string? Arg1,
        int? Quantity,
        string? ResultMessage
    );

    public SpaceMoltAgent(
        ILLMClient llm,
        ILLMClient? plannerLlm = null,
        bool hierarchicalPlanningEnabled = true,
        PromptScriptRag? scriptExampleRag = null)
    {
        _plannerLlm = plannerLlm ?? llm;
        _scriptExampleRag = scriptExampleRag;
        _hierarchicalPlanningEnabled = hierarchicalPlanningEnabled;

        _commands = SpaceContextMode.Instance.GetCommands()
            .Concat(TradeContextMode.Instance.GetCommands())
            .Concat(HangarContextMode.Instance.GetCommands())
            .Concat(ShipyardContextMode.Instance.GetCommands())
            .Concat(ShipCatalogContextMode.Instance.GetCommands())
            .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        _commandMap = _commands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        LoadScriptGenerationExamples();
        SyncRagExamples();
    }

    public async Task<(bool Added, string Message)> AddCurrentScriptAsExampleAsync()
    {
        if (string.IsNullOrWhiteSpace(_script))
            return (false, "No script loaded to save as an example.");

        if (string.IsNullOrWhiteSpace(_lastScriptGenerationPrompt))
            return (false, "No tracked generation prompt for this script.");

        var candidateScript = _script.Trim();
        var candidatePrompt = _lastScriptGenerationPrompt.Trim();
        bool alreadyExists = _scriptGenerationExamples.Any(e =>
            string.Equals(e.Script, candidateScript, StringComparison.Ordinal) &&
            string.Equals(e.Prompt, candidatePrompt, StringComparison.Ordinal));

        if (alreadyExists)
            return (false, "That script example is already saved.");

        _scriptGenerationExamples.Add(new ScriptGenerationExample
        {
            Prompt = candidatePrompt,
            Script = candidateScript,
            CreatedUtc = DateTime.UtcNow
        });
        _scriptExampleRag?.AddExample(new PromptScriptExample(candidatePrompt, candidateScript));

        await SaveScriptGenerationExamplesAsync();
        return (true, $"Saved script example #{_scriptGenerationExamples.Count}.");
    }

    public IReadOnlyList<string> GetMemoryList()
    {
        return _memory.Select(m =>
        {
            var action = FormatAction(m);
            var msg = m.ResultMessage;

            return string.IsNullOrWhiteSpace(msg)
                ? action
                : $"{action} → {msg}";
        }).ToList();
    }

    public IReadOnlyList<string> GetAvailableActions(GameState state)
    {
        return GetCandidateCommands(state, onlyAvailable: true)
            .Where(c => c.IsAvailable(state))
            .Select(c => c.BuildHelp(state))
            .ToList();
    }

    public (string SpaceStateMarkdown, string? TradeStateMarkdown, string? ShipyardStateMarkdown, string? CantinaStateMarkdown) BuildUiState(GameState state)
    {
        var spaceState = SpaceContextMode.Instance.ToDisplayText(state);
        var tradeState = state.Docked
            ? TradeContextMode.Instance.ToDisplayText(state)
            : null;
        var shipyardState = state.Docked && state.CurrentPOI.IsStation
            ? BuildShipyardUiState(state)
            : null;
        var cantinaState = state.Docked && state.CurrentPOI.IsStation
            ? BuildCantinaUiState(state)
            : null;

        return (spaceState, tradeState, shipyardState, cantinaState);
    }

    private static string BuildShipyardUiState(GameState state)
    {
        var shipyardState = ShipyardContextMode.Instance.ToDisplayText(state);
        int currentPage = state.ShipCatalogue.Page ?? 1;
        int totalPages = state.ShipCatalogue.TotalPages ?? 1;
        int totalItems = state.ShipCatalogue.Total ?? state.ShipCatalogue.TotalItems ?? 0;
        int entriesOnPage = state.ShipCatalogue.NormalizedEntries.Length;
        string catalogEntries = GameState.StripMarkdown(
            GameState.FormatCatalogueEntries(state.ShipCatalogue.NormalizedEntries));

        return
$@"{shipyardState}

CATALOG CACHE
PAGE: {currentPage}/{totalPages}
ENTRIES ON PAGE: {entriesOnPage}
TOTAL SHIPS: {totalItems}

SHIPS
{catalogEntries}";
    }

    private static string BuildCantinaUiState(GameState state)
    {
        string activeMissions = GameState.StripMarkdown(GameState.FormatMissions(state.ActiveMissions));
        string availableMissions = GameState.StripMarkdown(GameState.FormatMissions(state.AvailableMissions));

        if (string.IsNullOrWhiteSpace(activeMissions))
            activeMissions = "- _(none)_";
        if (string.IsNullOrWhiteSpace(availableMissions))
            availableMissions = "- _(none)_";

        return
$@"CONTEXT: CANTINA
STATION: {state.CurrentPOI.Id}
CREDITS: {state.Credits}

ACTIVE MISSIONS
{activeMissions}

AVAILABLE MISSIONS
{availableMissions}{state.BuildNotificationsDisplaySection()}";
    }

    public async Task ExecuteAsync(
        SpaceMoltHttpClient client,
        CommandResult result,
        GameState state)
    {
        string? message = null;
        bool shouldAddMemory = false;

        if (_activeCommand != null)
        {
            string activeCommandText = _activeCommandResult == null
                ? "multi-step command"
                : FormatCommand(_activeCommandResult);

            await LogCommandExecutionAsync(
                commandText: activeCommandText,
                state: state,
                phase: "continue-start");

            SetStatus($"Executing: continuing {activeCommandText}");

            var continuation = await _activeCommand.ContinueAsync(client, state);

            bool finished = continuation.Item1;
            var response = continuation.Item2;

            if (finished)
            {
                message = response?.ResultMessage;
                shouldAddMemory = _activeCommandResult != null;

                if (shouldAddMemory && _activeCommandResult != null)
                    AddMemory(_activeCommandResult, message);

                _activeCommand = null;
                _activeCommandResult = null;
                SetStatus("Waiting");
                if (PlanningEnabled)
                    await RefreshPlanAfterExecutionAsync(client, state);
            }

            await LogCommandExecutionAsync(
                commandText: activeCommandText,
                state: state,
                phase: "continue-end",
                details: message ?? (finished ? "completed" : "in-progress"));

            return;
        }

        await LogCommandExecutionAsync(
            commandText: FormatCommand(result),
            state: state,
            phase: "start");

        if (_commandMap.TryGetValue(result.Action, out var command))
        {
            if (command is IMultiTurnCommand multiTurnCommand)
            {
                SetStatus($"Executing: start {FormatCommand(result)}");
                _activeCommand = multiTurnCommand;
                _activeCommandResult = result;

                await multiTurnCommand.StartAsync(client, result, state);
            }
            else if (command is ISingleTurnCommand singleTurnCommand)
            {
                SetStatus($"Executing: run {FormatCommand(result)}");
                var response = await singleTurnCommand.ExecuteAsync(client, result, state);
                message = response?.ResultMessage;
                shouldAddMemory = true;

                if (string.Equals(result.Action, "halt", StringComparison.Ordinal))
                {
                    Halt("Halted: waiting for user input");
                }
                else
                {
                    SetStatus("Waiting");
                    if (PlanningEnabled)
                        await RefreshPlanAfterExecutionAsync(client, state);
                }
            }
        }

        if (shouldAddMemory)
            AddMemory(result, message);

        await LogCommandExecutionAsync(
            commandText: FormatCommand(result),
            state: state,
            phase: "end",
            details: message ?? "(no result message)");
    }

    private async Task LogCommandExecutionAsync(
        string commandText,
        GameState state,
        string phase,
        string? details = null)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:O}] === COMMAND_EXECUTION:{phase} ===");
            sb.AppendLine($"ControlMode: {_controlMode.Name}");
            sb.AppendLine($"Command: {commandText}");

            if (!string.IsNullOrWhiteSpace(details))
                sb.AppendLine($"Details: {details}");

            sb.AppendLine("GameState:");
            sb.AppendLine(state.ToLLMMarkdown());
            sb.AppendLine();

            await File.AppendAllTextAsync(AppPaths.CommandExecutionLogFile, sb.ToString());
        }
        catch
        {
            // Never fail command execution due to logging errors.
        }
    }

    private void AddMemory(CommandResult result, string? message)
    {
        if (_memory.Count >= MaxMemory)
            _memory.Dequeue();

        _memory.Enqueue(new ActionMemory(
            result.Action,
            result.Arg1,
            result.Quantity,
            message
        ));
    }

    public async Task<CommandResult?> DecideAsync(GameState state)
    {
        if (_isHalted)
        {
            SetStatus("Halted: waiting for user input");
            return null;
        }

        if (HasActiveCommand)
            return _activeCommandResult;

        return await DecideScriptStepAsync(state);
    }

    private async Task<CommandResult?> DecidePromptStepAsync(GameState state)
    {

        if (_hierarchicalPlanningEnabled && string.IsNullOrWhiteSpace(_objective))
        {
            _objective = await GenerateObjectiveAsync(state);
        }

        if (!PlanningEnabled)
            return await ExecutePlanStepAsync(state);

        if (IsPlanEmpty())
        {
            SetStatus("Planning");
            _plan = await GeneratePlanAsync(state);

            if (IsPlanEmpty())
            {
                return null;
            }
        }

        return await ExecutePlanStepAsync(state);
    }

    private Task<CommandResult?> DecideScriptStepAsync(GameState state)
    {
        while (_scriptQueue.Count > 0)
        {
            var next = _scriptQueue.Dequeue();

            if (string.IsNullOrWhiteSpace(next.Action) || !_commandMap.ContainsKey(next.Action))
            {
                AddMemory(next, "invalid script command");
                continue;
            }

            _currentScriptLine = next.SourceLine;
            SetStatus($"Executing: script {FormatCommand(next)}");
            return Task.FromResult<CommandResult?>(next);
        }

        Halt("Script complete: waiting for input");
        return Task.FromResult<CommandResult?>(null);
    }

    public void RequeueScriptStep(CommandResult step)
    {
        if (step == null || string.IsNullOrWhiteSpace(step.Action))
            return;

        var copy = new CommandResult
        {
            Action = step.Action,
            Arg1 = step.Arg1,
            Quantity = step.Quantity,
            SourceLine = step.SourceLine
        };

        _scriptQueue = new Queue<CommandResult>(
            new[] { copy }.Concat(_scriptQueue));
    }

    private async Task RefreshPlanAfterExecutionAsync(
        SpaceMoltHttpClient client,
        GameState fallbackState)
    {
        SetStatus("Planning");

        GameState latestState = fallbackState;

        try
        {
            latestState = client.GetGameState();
        }
        catch
        {
            // Keep using fallback state if refresh fails.
        }

        _plan = await UpdatePlanAsync(latestState);

        if (IsPlanEmpty())
        {
            _plan = await GeneratePlanAsync(latestState);
        }
    }

    private async Task<string> GeneratePlanAsync(GameState state)
    {
        SetStatus("Planning");

        return await CompletePlanAsync(
            state,
            objective:
                "Create or refresh the rationale.\n" +
                "Rationale is freeform and can include any useful ideas.\n" +
                "Keep it concise and practical.",
            includeCurrentPlan: true,
            memoryRecent: 8
        );
    }

    private async Task<CommandResult?> ExecutePlanStepAsync(GameState state)
    {
        SetStatus("Executing: selecting next move");

        if (!GetCandidateCommands(state, onlyAvailable: true).Any(c => c.IsAvailable(state)))
            return null;

        SetStatus("Executing: planner suggestion");
        string selectedCommand = await SuggestNextMoveAsync(state);
        SetStatus($"Executing: selected {selectedCommand}");

        var parsed = Parse(selectedCommand);
        if (!IsValidCommandForCurrentState(parsed, state))
        {
            AddInvalidCommandMemory(parsed);
            SetStatus("Waiting");
            return null;
        }

        return parsed;
    }

    private async Task<string> SuggestNextMoveAsync(GameState state)
    {
        string prompt = AgentPrompts.BuildSuggestNextMovePrompt(
            baseSystemPrompt: BaseSystemPrompt,
            stateMarkdown: state.ToLLMMarkdown(),
            availableActionsBlock: BuildAvailableActionsBlock(state, onlyAvailable: true),
            allActionsBlock: BuildAvailableActionsBlock(state, onlyAvailable: false),
            currentObjectiveBlock: BuildCurrentObjectiveContextBlock(),
            previousActionsBlock: BuildMemoryBlock(8)
        );

        await LogPlannerPromptAsync("suggest_next_move", prompt);

        string result = await _plannerLlm.CompleteAsync(
            prompt,
            maxTokens: 24,
            temperature: 0.2f,
            topP: 0.9f
        );

        return string.IsNullOrWhiteSpace(result)
            ? "No suggestion."
            : result.Trim();
    }

    private async Task<string> UpdatePlanAsync(GameState state)
    {
        string updated = await CompletePlanAsync(
            state,
            objective:
                "Regenerate the rationale after the most recent action.\n" +
                "Rationale is freeform and can include any useful ideas, priorities, or hypotheses.\n" +
                "Keep it concise and practical.\n" +
                "Output rationale lines only.",
            includeCurrentPlan: true,
            memoryRecent: 8,
            temperature: 0.2f,
            topP: 0.8f
        );

        return string.IsNullOrWhiteSpace(updated)
            ? (_plan ?? string.Empty)
            : updated;
    }

    private async Task<string> CompletePlanAsync(
        GameState state,
        string objective,
        string? userRequest = null,
        bool includeCurrentPlan = false,
        int memoryRecent = 8,
        float temperature = 0.3f,
        float topP = 0.85f)
    {
        string prompt = BuildPlanPrompt(state, objective, userRequest, includeCurrentPlan, memoryRecent);
        await LogPlannerPromptAsync("plan_rationale", prompt);

        string result = await _plannerLlm.CompleteAsync(
            prompt,
            maxTokens: 160,
            temperature: temperature,
            topP: topP
        );

        return (result ?? string.Empty).Trim();
    }

    private string BuildPlanPrompt(
        GameState state,
        string objective,
        string? userRequest,
        bool includeCurrentPlan,
        int memoryRecent)
    {
        string requestBlock = string.IsNullOrWhiteSpace(userRequest)
            ? ""
            : "User request:\n" + userRequest.Trim() + "\n\n";

        string currentRationaleBlock = includeCurrentPlan
            ? BuildCurrentRationaleBlock()
            : "";

        return AgentPrompts.BuildPlanPrompt(
            baseSystemPrompt: BaseSystemPrompt,
            requestBlock: requestBlock,
            stateMarkdown: state.ToLLMMarkdown(),
            currentObjectiveBlock: BuildCurrentObjectiveBlock(),
            previousActionsBlock: BuildMemoryBlock(memoryRecent),
            currentRationaleBlock: currentRationaleBlock,
            objective: objective
        );
    }

    public string BuildMemoryBlock(int? maxRecent = null)
    {
        if (_memory.Count == 0)
            return "";

        var items = maxRecent.HasValue
            ? _memory.TakeLast(maxRecent.Value)
            : _memory;

        var lines = items.Select(m =>
        {
            var action = FormatAction(m);

            var msg = m.ResultMessage;

            return string.IsNullOrWhiteSpace(msg)
                ? $"- {action}"
                : $"- {action} → {msg}";
        });

        return "Previous actions:\n" +
               string.Join("\n", lines) +
               "\n\n";
    }

    private string BuildAvailableActionsBlock(GameState state, bool onlyAvailable)
    {
        var help = _commands
            .Where(c => !onlyAvailable || c.IsAvailable(state))
            .Select(c => c.BuildHelp(state))
            .ToList();

        if (help.Count == 0)
            return onlyAvailable
                ? "Available actions:\n- none\n\n"
                : "All actions:\n- none\n\n";

        var heading = onlyAvailable ? "Available actions:" : "All actions:";
        return heading + "\n" + string.Join("\n", help) + "\n\n";
    }

    private IEnumerable<ICommand> GetCandidateCommands(GameState state, bool onlyAvailable)
    {
        return onlyAvailable
            ? _commands.Where(c => c.IsAvailable(state))
            : _commands;
    }

    private string BuildCurrentRationaleBlock()
    {
        if (string.IsNullOrWhiteSpace(_plan))
            return "Current rationale:\n- none\n\n";

        return "Current rationale:\n" +
               _plan.Trim() +
               "\n\n";
    }

    private string BuildCurrentObjectiveContextBlock()
    {
        if (string.IsNullOrWhiteSpace(_objective))
            return "";

        return "Current objective:\n- " + _objective.Trim() + "\n\n";
    }

    private string BuildCurrentObjectiveBlock()
    {
        if (!_hierarchicalPlanningEnabled)
            return "";

        if (string.IsNullOrWhiteSpace(_objective))
            return "Current objective:\n- none\n\n";

        return "Current objective:\n- " + _objective.Trim() + "\n\n";
    }

    private async Task<string> GenerateObjectiveAsync(GameState state)
    {
        string prompt = AgentPrompts.BuildObjectivePrompt(
            baseSystemPrompt: BaseSystemPrompt,
            stateMarkdown: state.ToLLMMarkdown(),
            allActionsBlock: BuildAvailableActionsBlock(state, onlyAvailable: false),
            previousActionsBlock: BuildMemoryBlock(8)
        );

        await LogPlannerPromptAsync("generate_objective", prompt);

        string result = await _plannerLlm.CompleteAsync(
            prompt,
            maxTokens: 48,
            temperature: 0.2f,
            topP: 0.9f
        );

        return ParseObjective(result);
    }

    private async Task<string> GenerateObjectiveFromUserInputAsync(string input, GameState state)
    {
        string prompt = AgentPrompts.BuildObjectiveFromUserInputPrompt(
            baseSystemPrompt: BaseSystemPrompt,
            userInput: input.Trim(),
            stateMarkdown: state.ToLLMMarkdown()
        );

        await LogPlannerPromptAsync("generate_objective_from_user_input", prompt);

        string result = await _plannerLlm.CompleteAsync(
            prompt,
            maxTokens: 48,
            temperature: 0.2f,
            topP: 0.9f
        );

        return ParseObjective(result);
    }

    private static string ParseObjective(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "";

        var lines = raw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length == 0)
            return "";

        return string.Join('\n', lines);
    }

    private static string ExtractScript(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var text = raw.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
            return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text.Trim('`').Trim();

        var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence <= firstNewline)
            return text[(firstNewline + 1)..].Trim();

        return text.Substring(firstNewline + 1, lastFence - firstNewline - 1).Trim();
    }

    private static async Task LogScriptWriterContextTokensAsync(
        int attempt,
        int maxAttempts,
        string userInput,
        string stateContextBlock,
        string examplesBlock,
        string? previousScript,
        string? previousError,
        string fullPrompt)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:O}] === script_writer_context ===");
            sb.AppendLine($"attempt={attempt}/{maxAttempts}");
            sb.AppendLine($"est_tokens.full_prompt={EstimateTokenCount(fullPrompt)}");
            sb.AppendLine($"est_tokens.user_input={EstimateTokenCount(userInput)}");
            sb.AppendLine($"est_tokens.state_context={EstimateTokenCount(stateContextBlock)}");
            sb.AppendLine($"est_tokens.examples={EstimateTokenCount(examplesBlock)}");
            sb.AppendLine($"est_tokens.previous_script={EstimateTokenCount(previousScript)}");
            sb.AppendLine($"est_tokens.previous_error={EstimateTokenCount(previousError)}");
            sb.AppendLine($"chars.full_prompt={(fullPrompt ?? string.Empty).Length}");
            sb.AppendLine($"chars.user_input={(userInput ?? string.Empty).Length}");
            sb.AppendLine($"chars.state_context={(stateContextBlock ?? string.Empty).Length}");
            sb.AppendLine($"chars.examples={(examplesBlock ?? string.Empty).Length}");
            sb.AppendLine($"chars.previous_script={(previousScript ?? string.Empty).Length}");
            sb.AppendLine($"chars.previous_error={(previousError ?? string.Empty).Length}");
            sb.AppendLine();

            await File.AppendAllTextAsync(AppPaths.ScriptWriterContextLogFile, sb.ToString());
        }
        catch
        {
            // Context metrics logging should never block script generation.
        }
    }

    private static int EstimateTokenCount(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        int nonWhitespaceChars = 0;
        foreach (char ch in text)
        {
            if (!char.IsWhiteSpace(ch))
                nonWhitespaceChars++;
        }

        // Rough cross-model heuristic: ~1 token per 4 non-whitespace chars.
        return (int)Math.Ceiling(nonWhitespaceChars / 4d);
    }

    private static async Task LogPlannerPromptAsync(string stage, string prompt)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:O}] === {stage} ===");
            sb.AppendLine(prompt ?? string.Empty);
            sb.AppendLine();

            await File.AppendAllTextAsync(AppPaths.PlannerPromptLogFile, sb.ToString());
        }
        catch
        {
            // Prompt logging should never block gameplay.
        }
    }

    private static void LogScriptNormalization(string source, string inputScript, string outputScript)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.UtcNow:O}] === script_normalization ({source}) ===");
            sb.AppendLine("INPUT:");
            sb.AppendLine(inputScript ?? string.Empty);
            sb.AppendLine("OUTPUT:");
            sb.AppendLine(outputScript ?? string.Empty);
            sb.AppendLine();
            File.AppendAllText(AppPaths.ScriptNormalizationLogFile, sb.ToString());
        }
        catch
        {
            // Script normalization logging should never block gameplay.
        }
    }

    private bool IsValidCommandForCurrentState(CommandResult result, GameState state)
    {
        if (string.IsNullOrWhiteSpace(result.Action))
            return false;

        if (!_commandMap.TryGetValue(result.Action, out var command))
            return false;

        var activeCommands = GetCandidateCommands(state, onlyAvailable: false)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!activeCommands.Contains(result.Action))
            return false;

        return command.IsAvailable(state);
    }

    private void AddInvalidCommandMemory(CommandResult result)
    {
        var memoryResult = new CommandResult
        {
            Action = string.IsNullOrWhiteSpace(result.Action) ? "<empty>" : result.Action.Trim(),
            Arg1 = result.Arg1,
            Quantity = result.Quantity
        };

        AddMemory(memoryResult, "invalid command");
    }

    private bool IsPlanEmpty()
    {
        return string.IsNullOrWhiteSpace(_plan);
    }

    private CommandResult Parse(string output)
    {
        var parts = output
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return new CommandResult
        {
            Action = parts.ElementAtOrDefault(0) ?? "",
            Arg1 = parts.ElementAtOrDefault(1),
            Quantity = int.TryParse(parts.ElementAtOrDefault(2), out int n)
                ? n
                : null
        };
    }

    private static string FormatAction(ActionMemory m)
    {
        if (!string.IsNullOrWhiteSpace(m.Arg1) && m.Quantity.HasValue)
            return $"{m.Action} {m.Arg1} {m.Quantity}";

        if (!string.IsNullOrWhiteSpace(m.Arg1))
            return $"{m.Action} {m.Arg1}";

        return m.Action;
    }

    private static string FormatCommand(CommandResult cmd)
    {
        if (!string.IsNullOrWhiteSpace(cmd.Arg1) && cmd.Quantity.HasValue)
            return $"{cmd.Action} {cmd.Arg1} {cmd.Quantity}";

        if (!string.IsNullOrWhiteSpace(cmd.Arg1))
            return $"{cmd.Action} {cmd.Arg1}";

        return cmd.Action;
    }

    private static string BuildScriptGenerationStateContextBlock(GameState state, string userInput)
    {
        var searchTerms = BuildPromptSearchTerms(userInput);

        var topPoiMatches = FindTopMatches(
            searchTerms,
            BuildPoiAliasMap(state),
            maxMatches: 3);
        var topSystemMatches = FindTopMatches(
            searchTerms,
            BuildSystemAliasMap(state),
            maxMatches: 3);
        var topItemMatches = FindTopMatches(
            searchTerms,
            BuildItemAliasMap(state),
            maxMatches: 3);

        var poiPrimary = state.POIs
            .Select(p => (Key: p.Id, Label: $"{p.Id} ({p.Type})"))
            .ToList();
        var systemPrimary = state.Systems
            .Select(s => (Key: s, Label: s))
            .ToList();
        var cargoPrimary = state.Cargo.Values
            .OrderByDescending(c => c.Quantity)
            .Select(c => (Key: c.ItemId, Label: c.ItemId))
            .ToList();

        var poiLines = InterleaveWithTopMatches(
            poiPrimary,
            topPoiMatches,
            match => match);
        var systemLines = InterleaveWithTopMatches(
            systemPrimary,
            topSystemMatches,
            match => match);
        var cargoLines = InterleaveWithTopMatches(
            cargoPrimary,
            topItemMatches,
            match => match);

        string currentPoiId = state.CurrentPOI?.Id ?? "-";
        string currentPoiType = state.CurrentPOI?.Type ?? "-";

        return
            "Current location:\n" +
            $"- system: {state.System}\n" +
            $"- poi: {currentPoiId} ({currentPoiType})\n\n" +
            "POIs:\n" + FormatPromptSectionLines(poiLines) + "\n\n" +
            "Systems:\n" + FormatPromptSectionLines(systemLines) + "\n\n" +
            "Items:\n" + FormatPromptSectionLines(cargoLines);
    }

    private static IReadOnlyList<string> BuildPromptSearchTerms(string userInput)
    {
        if (string.IsNullOrWhiteSpace(userInput))
            return Array.Empty<string>();

        var tokens = new List<string>();
        var sb = new StringBuilder();

        foreach (char ch in userInput)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                continue;
            }

            if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
            tokens.Add(sb.ToString());

        if (tokens.Count == 0)
            return Array.Empty<string>();

        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddTerm(string value)
        {
            string normalized = DslFuzzyMatcher.Normalize(value);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            if (seen.Add(normalized))
                terms.Add(normalized);
        }

        int maxN = Math.Min(3, tokens.Count);
        for (int n = maxN; n >= 1; n--)
        {
            for (int i = 0; i + n <= tokens.Count; i++)
                AddTerm(string.Join('_', tokens.Skip(i).Take(n)));
        }

        return terms;
    }

    private static Dictionary<string, HashSet<string>> BuildSystemAliasMap(GameState state)
    {
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        AddPromptAlias(aliases, state.System, state.System);
        foreach (var systemId in state.Systems)
            AddPromptAlias(aliases, systemId, systemId);

        var map = state.Galaxy?.Map?.Systems?.Count > 0
            ? state.Galaxy.Map
            : LoadMapCache();
        foreach (var system in map.Systems)
            AddPromptAlias(aliases, system.Id, system.Id);

        return aliases;
    }

    private static Dictionary<string, HashSet<string>> BuildPoiAliasMap(GameState state)
    {
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        AddPromptAlias(aliases, state.CurrentPOI?.Id, state.CurrentPOI?.Id);
        foreach (var poi in state.POIs)
        {
            AddPromptAlias(aliases, poi.Id, poi.Id);
            AddPromptAlias(aliases, poi.Id, poi.Name);
        }

        var map = state.Galaxy?.Map?.Systems?.Count > 0
            ? state.Galaxy.Map
            : LoadMapCache();
        foreach (var system in map.Systems)
        {
            foreach (var poi in system.Pois)
                AddPromptAlias(aliases, poi.Id, poi.Id);
        }

        return aliases;
    }

    private static Dictionary<string, HashSet<string>> BuildItemAliasMap(GameState state)
    {
        var aliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var itemId in state.Cargo.Keys)
            AddPromptAlias(aliases, itemId, itemId);

        if (state.Shared.StorageItems != null)
        {
            foreach (var itemId in state.Shared.StorageItems.Keys)
                AddPromptAlias(aliases, itemId, itemId);
        }

        if (state.Galaxy?.Catalog?.ItemsById != null)
        {
            foreach (var (itemId, entry) in state.Galaxy.Catalog.ItemsById)
            {
                AddPromptAlias(aliases, itemId, itemId);
                AddPromptAlias(aliases, itemId, entry?.Name);
            }
        }

        return aliases;
    }

    private static IReadOnlyList<string> FindTopMatches(
        IReadOnlyList<string> searchTerms,
        IReadOnlyDictionary<string, HashSet<string>> aliasesByCanonical,
        int maxMatches)
    {
        if (searchTerms.Count == 0 ||
            aliasesByCanonical.Count == 0 ||
            maxMatches <= 0)
        {
            return Array.Empty<string>();
        }

        var scored = new List<(string Canonical, double Score, int Distance)>();

        foreach (var (canonical, aliases) in aliasesByCanonical)
        {
            double bestScore = -1d;
            int bestDistance = int.MaxValue;

            foreach (var alias in aliases)
            {
                foreach (var term in searchTerms)
                {
                    double score = ComputePromptMatchScore(term, alias);
                    int distance = LevenshteinDistance(term, alias);

                    if (score > bestScore ||
                        (Math.Abs(score - bestScore) < 0.0001d && distance < bestDistance))
                    {
                        bestScore = score;
                        bestDistance = distance;
                    }
                }
            }

            if (bestScore >= PromptSearchMatchCutoff)
                scored.Add((canonical, bestScore, bestDistance));
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Distance)
            .ThenBy(s => s.Canonical, StringComparer.Ordinal)
            .Take(maxMatches)
            .Select(s => s.Canonical)
            .ToList();
    }

    private static IReadOnlyList<string> InterleaveWithTopMatches(
        IReadOnlyList<(string Key, string Label)> primaryEntries,
        IReadOnlyList<string> topMatches,
        Func<string, string> matchLabelFactory)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        int primaryIndex = 0;
        int matchIndex = 0;

        while (primaryIndex < primaryEntries.Count || matchIndex < topMatches.Count)
        {
            if (primaryIndex < primaryEntries.Count)
            {
                var primary = primaryEntries[primaryIndex++];
                string key = DslFuzzyMatcher.Normalize(primary.Key);
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    result.Add(primary.Label);
            }

            if (matchIndex < topMatches.Count)
            {
                string match = topMatches[matchIndex++];
                string key = DslFuzzyMatcher.Normalize(match);
                if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    result.Add(matchLabelFactory(match));
            }
        }

        return result;
    }

    private static string FormatPromptSectionLines(IReadOnlyList<string> lines)
    {
        if (lines == null || lines.Count == 0)
            return "- none";

        return string.Join("\n", lines.Select(l => $"- {l}"));
    }

    private static void AddPromptAlias(
        Dictionary<string, HashSet<string>> aliasesByCanonical,
        string? canonicalRaw,
        string? aliasRaw)
    {
        if (string.IsNullOrWhiteSpace(canonicalRaw))
            return;

        string canonical = canonicalRaw.Trim();
        string alias = DslFuzzyMatcher.Normalize(aliasRaw ?? canonical);
        if (string.IsNullOrWhiteSpace(alias))
            alias = DslFuzzyMatcher.Normalize(canonical);

        if (string.IsNullOrWhiteSpace(alias))
            return;

        if (!aliasesByCanonical.TryGetValue(canonical, out var aliases))
        {
            aliases = new HashSet<string>(StringComparer.Ordinal);
            aliasesByCanonical[canonical] = aliases;
        }

        aliases.Add(alias);
        aliases.Add(DslFuzzyMatcher.Normalize(canonical));
    }

    private static double ComputePromptMatchScore(string query, string candidateAlias)
    {
        if (query.Length == 0 || candidateAlias.Length == 0)
            return -1d;

        if (string.Equals(query, candidateAlias, StringComparison.Ordinal))
            return 1d;

        if (candidateAlias.StartsWith(query, StringComparison.Ordinal))
            return 0.94d;

        if (query.StartsWith(candidateAlias, StringComparison.Ordinal))
            return 0.88d;

        if (candidateAlias.Contains(query, StringComparison.Ordinal))
            return 0.82d;

        var tokenScore = TokenOverlapScore(query, candidateAlias);
        var editScore = LevenshteinSimilarity(query, candidateAlias);

        return (editScore * 0.65d) + (tokenScore * 0.35d);
    }

    private static double TokenOverlapScore(string a, string b)
    {
        var aTokens = a.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var bTokens = b.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (aTokens.Length == 0 || bTokens.Length == 0)
            return 0d;

        var aSet = aTokens.ToHashSet(StringComparer.Ordinal);
        var bSet = bTokens.ToHashSet(StringComparer.Ordinal);

        int overlap = aSet.Count(t => bSet.Contains(t));
        int union = aSet.Count + bSet.Count - overlap;
        if (union <= 0)
            return 0d;

        return overlap / (double)union;
    }

    private static double LevenshteinSimilarity(string a, string b)
    {
        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0)
            return 1d;

        int distance = LevenshteinDistance(a, b);
        return Math.Max(0d, 1d - (distance / (double)maxLen));
    }

    private static int LevenshteinDistance(string a, string b)
    {
        int n = a.Length;
        int m = b.Length;
        if (n == 0)
            return m;
        if (m == 0)
            return n;

        var prev = new int[m + 1];
        var cur = new int[m + 1];

        for (int j = 0; j <= m; j++)
            prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(
                    Math.Min(cur[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[m];
    }

    private static GalaxyMapSnapshot LoadMapCache()
    {
        return GalaxyMapSnapshotFile.Load(AppPaths.GalaxyMapFile);
    }

    private async Task<string> BuildScriptGenerationExamplesBlockAsync(string generationInput)
    {
        var sb = new StringBuilder();
        sb.Append(DefaultScriptGenerationExamples.TrimEnd());
        sb.Append("\n\n");

        if (_scriptGenerationExamples.Count == 0)
            return sb.ToString().TrimEnd();

        IReadOnlyList<PromptScriptMatch> matches = Array.Empty<PromptScriptMatch>();

        if (_scriptExampleRag != null && !string.IsNullOrWhiteSpace(generationInput))
        {
            try
            {
                matches = await _scriptExampleRag.FindTopMatchesAsync(generationInput, maxMatches: 5);
            }
            catch
            {
                // Retrieval failure should never block script generation.
            }
        }

        if (matches.Count == 0)
        {
            matches = _scriptGenerationExamples
                .TakeLast(5)
                .Select(e => new PromptScriptMatch(e.Prompt, e.Script, 0d))
                .ToList();
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var example = matches[i];
            sb.Append(example.Prompt.Trim());
            sb.Append(" ->\n");
            sb.Append(NormalizeScriptExampleForPrompt(example.Script));
            sb.Append("\n\n");
        }

        return sb.ToString().TrimEnd();
    }

    private void LoadScriptGenerationExamples()
    {
        try
        {
            if (!File.Exists(AppPaths.ScriptGenerationExamplesFile))
                return;

            var raw = File.ReadAllText(AppPaths.ScriptGenerationExamplesFile);
            var loaded = JsonSerializer.Deserialize<List<ScriptGenerationExample>>(raw);
            if (loaded == null || loaded.Count == 0)
                return;

            _scriptGenerationExamples.Clear();
            _scriptGenerationExamples.AddRange(
                loaded.Where(e =>
                    !string.IsNullOrWhiteSpace(e.Prompt) &&
                    !string.IsNullOrWhiteSpace(e.Script)));
        }
        catch
        {
            // Loading examples should never block startup.
        }
    }

    private async Task SaveScriptGenerationExamplesAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_scriptGenerationExamples, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(AppPaths.ScriptGenerationExamplesFile, json);
        }
        catch
        {
            // Persisting examples should never block gameplay.
        }
    }

    private void SyncRagExamples()
    {
        _scriptExampleRag?.ReplaceExamples(
            _scriptGenerationExamples.Select(e => new PromptScriptExample(e.Prompt, e.Script)));
    }

    private static string NormalizeScriptExampleForPrompt(string script)
    {
        try
        {
            return DslInterpreter.NormalizeScript(script);
        }
        catch
        {
            return script?.Trim() ?? string.Empty;
        }
    }

    private static string RenderDslNodes(IReadOnlyList<DslAstNode> nodes)
    {
        var sb = new StringBuilder();

        foreach (var node in nodes)
        {
            switch (node)
            {
                case DslCommandAstNode command:
                    sb.Append(command.Name);
                    if (command.Args.Count > 0)
                    {
                        sb.Append(' ');
                        sb.Append(string.Join(" ", command.Args));
                    }

                    sb.AppendLine(";");
                    break;
            }
        }

        return sb.ToString();
    }

    private sealed class ScriptGenerationExample
    {
        public string Prompt { get; set; } = string.Empty;
        public string Script { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; }
    }
}

#endregion

public class CommandResult
{
    public string Action { get; set; } = "";
    public string? Arg1 { get; set; }
    public int? Quantity { get; set; }
    public int? SourceLine { get; set; }
}
