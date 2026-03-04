using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public sealed class CommandExecutionEngine
{
    private readonly List<ICommand> _commands;
    private readonly Dictionary<string, ICommand> _commandMap;
    private readonly Queue<ActionMemory> _memory = new();

    private const int MaxMemory = 12;

    private string? _script;
    private int? _currentScriptLine;
    private Queue<CommandResult> _scriptQueue = new();
    private readonly Dictionary<string, List<CommandResult>> _repeatBodies =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CommandResult>> _untilBodies =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _untilConditions =
        new(StringComparer.Ordinal);
    private bool _isHalted;
    private IMultiTurnCommand? _activeCommand;
    private CommandResult? _activeCommandResult;

    private readonly Action<string> _setStatus;
    private readonly IAgentLogger _logger;
    private readonly string _controlModeName;

    public CommandExecutionEngine(
        IEnumerable<ICommand> commands,
        Action<string> setStatus,
        IAgentLogger logger,
        string controlModeName)
    {
        _commands = commands.ToList();
        _commandMap = _commands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _setStatus = setStatus;
        _logger = logger;
        _controlModeName = controlModeName;
    }

    public bool IsHalted => _isHalted;
    public bool HasActiveCommand => _activeCommand != null;
    public int? CurrentScriptLine => _currentScriptLine;
    public string? CurrentScript => string.IsNullOrWhiteSpace(_script) ? null : _script;

    public string SetScript(string script, GameState? state = null)
    {
        var rawScript = script ?? string.Empty;
        _currentScriptLine = null;
        var steps = (state == null
            ? DslInterpreter.Translate(rawScript)
            : DslInterpreter.Translate(rawScript, state)).ToList();
        _script = DslInterpreter.RenderScript(steps).TrimEnd();

        _logger.LogScriptNormalization("set_script", rawScript, _script);

        for (int i = 0; i < steps.Count; i++)
            steps[i].SourceLine = i + 1;

        BuildRepeatBodyIndex(steps);
        BuildUntilBodyIndex(steps);
        _scriptQueue = new Queue<CommandResult>(steps);
        _isHalted = false;

        _setStatus(_scriptQueue.Count == 0
            ? "Script loaded (empty)"
            : $"Script loaded ({_scriptQueue.Count} steps)");

        return _script;
    }

    public void ActivateScriptControl()
    {
        _isHalted = false;
        _setStatus($"Mode: {_controlModeName}");
    }

    public bool InterruptActiveCommand(string reason = "Interrupted")
    {
        if (_activeCommand == null)
            return false;

        _activeCommand = null;
        _activeCommandResult = null;
        _setStatus(reason);
        return true;
    }

    public void Halt(string reason = "Halted")
    {
        _isHalted = true;
        _currentScriptLine = null;
        _setStatus(reason);
    }

    public void ResumeFromHalt(string reason = "Resumed")
    {
        _isHalted = false;
        _setStatus(reason);
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
        var actions = _commands
            .Select(c => c.BuildHelp(state))
            .ToList();

        actions.Add("- halt → pause and wait for user input");
        return actions;
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

            await _logger.LogCommandExecutionAsync(
                _controlModeName,
                activeCommandText,
                state,
                phase: "continue-start");

            _setStatus($"Executing: continuing {activeCommandText}");

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
                _setStatus("Waiting");
            }

            await _logger.LogCommandExecutionAsync(
                _controlModeName,
                activeCommandText,
                state,
                phase: "continue-end",
                details: message ?? (finished ? "completed" : "in-progress"));

            return;
        }

        await _logger.LogCommandExecutionAsync(
            _controlModeName,
            FormatCommand(result),
            state,
            phase: "start");

        if (string.Equals(result.Action, "halt", StringComparison.OrdinalIgnoreCase))
        {
            message = "Halting autonomous execution. Waiting for user input.";
            shouldAddMemory = true;
            Halt("Halted: waiting for user input");
        }
        else if (_commandMap.TryGetValue(result.Action, out var command))
        {
            if (command is IMultiTurnCommand multiTurnCommand)
            {
                _setStatus($"Executing: start {FormatCommand(result)}");
                _activeCommand = multiTurnCommand;
                _activeCommandResult = result;

                await multiTurnCommand.StartAsync(client, result, state);
            }
            else if (command is ISingleTurnCommand singleTurnCommand)
            {
                _setStatus($"Executing: run {FormatCommand(result)}");
                var response = await singleTurnCommand.ExecuteAsync(client, result, state);
                message = response?.ResultMessage;
                shouldAddMemory = true;
                _setStatus("Waiting");
            }
        }

        if (shouldAddMemory)
            AddMemory(result, message);

        await _logger.LogCommandExecutionAsync(
            _controlModeName,
            FormatCommand(result),
            state,
            phase: "end",
            details: message ?? "(no result message)");
    }

    public Task<CommandResult?> DecideAsync(GameState state)
    {
        if (_isHalted)
        {
            _setStatus("Halted: waiting for user input");
            return Task.FromResult<CommandResult?>(null);
        }

        if (HasActiveCommand)
            return Task.FromResult(_activeCommandResult);

        return DecideScriptStepAsync(state);
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

    private Task<CommandResult?> DecideScriptStepAsync(GameState state)
    {
        while (_scriptQueue.Count > 0)
        {
            var next = _scriptQueue.Dequeue();

            if (TryHandleRuntimeControl(next, state))
                continue;

            if (!IsExecutableAction(next.Action))
            {
                AddMemory(next, "invalid script command");
                continue;
            }

            _currentScriptLine = next.SourceLine;
            _setStatus($"Executing: script {FormatCommand(next)}");
            return Task.FromResult<CommandResult?>(next);
        }

        Halt("Script complete: waiting for input");
        return Task.FromResult<CommandResult?>(null);
    }

    private bool TryHandleRuntimeControl(CommandResult next, GameState state)
    {
        if (string.IsNullOrWhiteSpace(next.Action))
            return false;

        if (string.Equals(next.Action, DslInterpreter.IfStartAction, StringComparison.Ordinal))
        {
            if (!DslInterpreter.ParseIfStartArg(next.Arg1, out var ifId, out var condition))
                return true;

            if (!DslBooleanEvaluator.TryEvaluate(condition, state, out var conditionValue))
                return true;

            if (conditionValue)
                return true;

            SkipUntilIfEnd(ifId);
            return true;
        }

        if (string.Equals(next.Action, DslInterpreter.IfEndAction, StringComparison.Ordinal))
            return true;

        if (string.Equals(next.Action, DslInterpreter.UntilStartAction, StringComparison.Ordinal))
        {
            if (!DslInterpreter.ParseUntilStartArg(next.Arg1, out var untilId, out var condition))
                return true;

            if (!DslBooleanEvaluator.TryEvaluate(condition, state, out var conditionValue))
                return true;

            if (!conditionValue)
                return true;

            SkipUntilBlockEnd(untilId);
            return true;
        }

        if (string.Equals(next.Action, DslInterpreter.UntilEndAction, StringComparison.Ordinal))
        {
            var untilId = (next.Arg1 ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(untilId))
                return true;

            if (!_untilBodies.TryGetValue(untilId, out var untilBody) || untilBody.Count == 0)
                return true;

            if (!_untilConditions.TryGetValue(untilId, out var condition) ||
                !DslBooleanEvaluator.TryEvaluate(condition, state, out var conditionValue))
            {
                return true;
            }

            if (conditionValue)
                return true;

            var untilReplay = untilBody
                .Select(CloneStep)
                .Append(CloneStep(next))
                .Concat(_scriptQueue);

            _scriptQueue = new Queue<CommandResult>(untilReplay);
            return true;
        }

        if (string.Equals(next.Action, DslInterpreter.RepeatStartAction, StringComparison.Ordinal))
            return true;

        if (!string.Equals(next.Action, DslInterpreter.RepeatEndAction, StringComparison.Ordinal))
            return false;

        var repeatId = (next.Arg1 ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(repeatId))
            return true;

        if (!_repeatBodies.TryGetValue(repeatId, out var body) || body.Count == 0)
            return true;

        var replay = body
            .Select(CloneStep)
            .Append(CloneStep(next))
            .Concat(_scriptQueue);

        _scriptQueue = new Queue<CommandResult>(replay);
        return true;
    }

    private void SkipUntilIfEnd(string ifId)
    {
        while (_scriptQueue.Count > 0)
        {
            var step = _scriptQueue.Dequeue();
            if (!string.Equals(step.Action, DslInterpreter.IfEndAction, StringComparison.Ordinal))
                continue;

            if (string.Equals((step.Arg1 ?? string.Empty).Trim(), ifId, StringComparison.Ordinal))
                return;
        }
    }

    private void SkipUntilBlockEnd(string blockId)
    {
        while (_scriptQueue.Count > 0)
        {
            var step = _scriptQueue.Dequeue();
            if (!string.Equals(step.Action, DslInterpreter.UntilEndAction, StringComparison.Ordinal))
                continue;

            if (string.Equals((step.Arg1 ?? string.Empty).Trim(), blockId, StringComparison.Ordinal))
                return;
        }
    }

    private void BuildRepeatBodyIndex(IReadOnlyList<CommandResult> steps)
    {
        _repeatBodies.Clear();

        var stack = new Stack<(string RepeatId, int StartIndex)>();

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var action = step.Action ?? string.Empty;
            var repeatId = (step.Arg1 ?? string.Empty).Trim();

            if (string.Equals(action, DslInterpreter.RepeatStartAction, StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(repeatId))
                    stack.Push((repeatId, i));
                continue;
            }

            if (!string.Equals(action, DslInterpreter.RepeatEndAction, StringComparison.Ordinal))
                continue;

            if (string.IsNullOrWhiteSpace(repeatId) || stack.Count == 0)
                continue;

            var open = stack.Pop();
            if (!string.Equals(open.RepeatId, repeatId, StringComparison.Ordinal))
                continue;

            var body = steps
                .Skip(open.StartIndex + 1)
                .Take(i - open.StartIndex - 1)
                .Select(CloneStep)
                .ToList();

            _repeatBodies[repeatId] = body;
        }
    }

    private void BuildUntilBodyIndex(IReadOnlyList<CommandResult> steps)
    {
        _untilBodies.Clear();
        _untilConditions.Clear();

        var stack = new Stack<(string UntilId, string Condition, int StartIndex)>();

        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var action = step.Action ?? string.Empty;

            if (string.Equals(action, DslInterpreter.UntilStartAction, StringComparison.Ordinal))
            {
                if (DslInterpreter.ParseUntilStartArg(step.Arg1, out var untilId, out var condition))
                    stack.Push((untilId, condition, i));
                continue;
            }

            if (!string.Equals(action, DslInterpreter.UntilEndAction, StringComparison.Ordinal))
                continue;

            var endId = (step.Arg1 ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(endId) || stack.Count == 0)
                continue;

            var open = stack.Pop();
            if (!string.Equals(open.UntilId, endId, StringComparison.Ordinal))
                continue;

            var body = steps
                .Skip(open.StartIndex + 1)
                .Take(i - open.StartIndex - 1)
                .Select(CloneStep)
                .ToList();

            _untilBodies[endId] = body;
            _untilConditions[endId] = open.Condition;
        }
    }

    private static CommandResult CloneStep(CommandResult step)
    {
        return new CommandResult
        {
            Action = step.Action,
            Arg1 = step.Arg1,
            Quantity = step.Quantity,
            SourceLine = step.SourceLine
        };
    }

    private bool IsExecutableAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return false;

        if (string.Equals(action, "halt", StringComparison.OrdinalIgnoreCase))
            return true;

        return _commandMap.ContainsKey(action);
    }

    private void AddMemory(CommandResult result, string? message)
    {
        if (_memory.Count >= MaxMemory)
            _memory.Dequeue();

        _memory.Enqueue(new ActionMemory(
            result.Action,
            result.Arg1,
            result.Quantity,
            message));
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

    private record ActionMemory(
        string Action,
        string? Arg1,
        int? Quantity,
        string? ResultMessage);
}
