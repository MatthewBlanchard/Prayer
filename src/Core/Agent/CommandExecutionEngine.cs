using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public sealed class CommandExecutionEngine
{
    private const string RootFramePath = "r";
    private readonly List<ICommand> _commands;
    private readonly Dictionary<string, ICommand> _commandMap;
    private readonly Queue<ActionMemory> _memory = new();
    private readonly LinkedList<CommandResult> _requeuedSteps = new();
    private readonly List<ExecutionFrame> _frames = new();

    private const int MaxMemory = 12;

    private string? _script;
    private DslAstProgram? _scriptAst;
    private int? _currentScriptLine;
    private bool _isHalted;
    private IMultiTurnCommand? _activeCommand;
    private CommandResult? _activeCommandResult;

    private readonly Action<string> _setStatus;
    private readonly IAgentLogger _logger;
    private readonly string _controlModeName;
    private readonly Action<CommandExecutionCheckpoint>? _saveCheckpoint;

    public CommandExecutionEngine(
        IEnumerable<ICommand> commands,
        Action<string> setStatus,
        IAgentLogger logger,
        string controlModeName,
        Action<CommandExecutionCheckpoint>? saveCheckpoint = null)
    {
        _commands = commands.ToList();
        _commandMap = _commands.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        _setStatus = setStatus;
        _logger = logger;
        _controlModeName = controlModeName;
        _saveCheckpoint = saveCheckpoint;
    }

    public bool IsHalted => _isHalted;
    public bool HasActiveCommand => _activeCommand != null;
    public int? CurrentScriptLine => _currentScriptLine;
    public string? CurrentScript => string.IsNullOrWhiteSpace(_script) ? null : _script;

    public string SetScript(string script, GameState? state = null)
    {
        var rawScript = script ?? string.Empty;
        _currentScriptLine = null;

        var tree = DslParser.ParseTree(rawScript);
        if (state != null)
            ValidateCommandNodes(tree.Statements, state);

        var normalizedSteps = DslInterpreter.Translate(tree);
        _script = DslInterpreter.RenderScript(normalizedSteps).TrimEnd();
        _scriptAst = tree;

        _logger.LogScriptNormalization("set_script", rawScript, _script);

        ResetFrames();
        _requeuedSteps.Clear();
        _isHalted = false;

        _setStatus(normalizedSteps.Count == 0
            ? "Script loaded (empty)"
            : $"Script loaded ({normalizedSteps.Count} steps)");
        PersistCheckpoint();

        return _script;
    }

    public void ActivateScriptControl()
    {
        _isHalted = false;
        _setStatus($"Mode: {_controlModeName}");
        PersistCheckpoint();
    }

    public bool InterruptActiveCommand(string reason = "Interrupted")
    {
        if (_activeCommand == null)
            return false;

        _activeCommand = null;
        _activeCommandResult = null;
        _setStatus(reason);
        PersistCheckpoint();
        return true;
    }

    public void Halt(string reason = "Halted")
    {
        _isHalted = true;
        _currentScriptLine = null;
        _setStatus(reason);
        PersistCheckpoint();
    }

    public void ResumeFromHalt(string reason = "Resumed")
    {
        _isHalted = false;
        _setStatus(reason);
        PersistCheckpoint();
    }

    public bool TryRestoreCheckpoint(CommandExecutionCheckpoint? checkpoint, GameState? state = null)
    {
        if (checkpoint == null || string.IsNullOrWhiteSpace(checkpoint.Script))
            return false;

        try
        {
            _ = SetScript(checkpoint.Script, state);
            RestoreFromCheckpoint(checkpoint);

            _setStatus(_isHalted
                ? "Resumed from checkpoint (halted)"
                : "Resumed from checkpoint");
            PersistCheckpoint();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<string> GetMemoryList()
    {
        return _memory.Select(m =>
        {
            var action = FormatAction(m);
            var msg = m.ResultMessage;

            return string.IsNullOrWhiteSpace(msg)
                ? action
                : $"{action} -> {msg}";
        }).ToList();
    }

    public IReadOnlyList<string> GetAvailableActions(GameState state)
    {
        var actions = _commands
            .Select(c => c.BuildHelp(state))
            .ToList();

        actions.Add("- halt -> pause and wait for user input");
        return actions;
    }

    public async Task ExecuteAsync(
        IRuntimeTransport client,
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

            PersistCheckpoint();

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
                PersistCheckpoint();

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
        PersistCheckpoint();

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

        _requeuedSteps.AddFirst(CloneStep(step));
        PersistCheckpoint();
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
                : $"- {action} -> {msg}";
        });

        return "Previous actions:\n" +
               string.Join("\n", lines) +
               "\n\n";
    }

    private Task<CommandResult?> DecideScriptStepAsync(GameState state)
    {
        while (true)
        {
            CommandResult next;

            if (_requeuedSteps.Count > 0)
            {
                next = _requeuedSteps.First!.Value;
                _requeuedSteps.RemoveFirst();
            }
            else if (!TryGetNextScriptCommand(state, out next))
            {
                Halt("Script complete: waiting for input");
                return Task.FromResult<CommandResult?>(null);
            }

            if (!IsExecutableAction(next.Action))
            {
                AddMemory(next, "invalid script command");
                PersistCheckpoint();
                continue;
            }

            _currentScriptLine = next.SourceLine;
            _setStatus($"Executing: script {FormatCommand(next)}");
            PersistCheckpoint();
            return Task.FromResult<CommandResult?>(next);
        }
    }

    private bool TryGetNextScriptCommand(GameState state, out CommandResult result)
    {
        result = new CommandResult();
        LogAstWalker("step_scan_begin", "Scanning for next executable script node.");

        while (_frames.Count > 0)
        {
            var frame = _frames[^1];

            if (frame.Index >= frame.Nodes.Count)
            {
                LogAstWalker(
                    "frame_complete",
                    $"Frame exhausted kind={frame.Kind} path={frame.Path}.");
                if (TryAdvanceCompletedLoop(frame, state))
                {
                    LogAstWalker(
                        "loop_rewind",
                        $"Loop rewound kind={frame.Kind} path={frame.Path}.");
                    continue;
                }

                _frames.RemoveAt(_frames.Count - 1);
                LogAstWalker(
                    "frame_pop",
                    $"Popped frame kind={frame.Kind} path={frame.Path}.");
                continue;
            }

            int nodeIndex = frame.Index;
            var node = frame.Nodes[frame.Index++];

            switch (node)
            {
                case DslCommandAstNode commandNode:
                    result = BuildCommandResult(commandNode);
                    LogAstWalker(
                        "emit_command",
                        $"Selected command line={result.SourceLine?.ToString() ?? "?"} cmd={FormatCommand(result)} path={frame.Path}/{nodeIndex}.");
                    return true;

                case DslRepeatAstNode repeatNode:
                {
                    IReadOnlyList<DslAstNode> body = repeatNode.Body ?? Array.Empty<DslAstNode>();
                    LogAstWalker(
                        "repeat_visit",
                        $"Visited repeat line={repeatNode.SourceLine} bodyCount={body.Count} path={frame.Path}/{nodeIndex}.");
                    if (body.Count > 0)
                    {
                        _frames.Add(new ExecutionFrame(
                            body,
                            ExecutionFrameKind.Repeat,
                            repeatNode.SourceLine,
                            untilCondition: null,
                            untilConditionKnown: false,
                            path: $"{frame.Path}/{nodeIndex}"));
                        LogAstWalker(
                            "frame_push",
                            $"Pushed repeat frame line={repeatNode.SourceLine} path={frame.Path}/{nodeIndex}.");
                    }
                    continue;
                }

                case DslIfAstNode ifNode:
                {
                    IReadOnlyList<DslAstNode> body = ifNode.Body ?? Array.Empty<DslAstNode>();
                    bool conditionKnown;
                    bool conditionValue;
                    bool shouldEnter = ShouldEnterIf(
                        ifNode.Condition,
                        state,
                        out conditionKnown,
                        out conditionValue);
                    LogAstWalker(
                        "if_visit",
                        $"Visited if line={ifNode.SourceLine} cond={NormalizeCondition(ifNode.Condition)} known={conditionKnown} value={conditionValue} enter={shouldEnter} bodyCount={body.Count} path={frame.Path}/{nodeIndex}.");
                    if (shouldEnter && body.Count > 0)
                    {
                        _frames.Add(new ExecutionFrame(
                            body,
                            ExecutionFrameKind.If,
                            ifNode.SourceLine,
                            untilCondition: null,
                            untilConditionKnown: false,
                            path: $"{frame.Path}/{nodeIndex}"));
                        LogAstWalker(
                            "frame_push",
                            $"Pushed if frame line={ifNode.SourceLine} path={frame.Path}/{nodeIndex}.");
                    }
                    continue;
                }

                case DslUntilAstNode untilNode:
                {
                    bool conditionKnown;
                    bool conditionValue;
                    bool shouldEnter = ShouldEnterUntil(
                        untilNode.Condition,
                        state,
                        out conditionKnown,
                        out conditionValue);
                    IReadOnlyList<DslAstNode> body = untilNode.Body ?? Array.Empty<DslAstNode>();
                    LogAstWalker(
                        "until_visit",
                        $"Visited until line={untilNode.SourceLine} cond={NormalizeCondition(untilNode.Condition)} known={conditionKnown} value={conditionValue} enter={shouldEnter} bodyCount={body.Count} path={frame.Path}/{nodeIndex}.");
                    if (shouldEnter && body.Count > 0)
                    {
                        _frames.Add(new ExecutionFrame(
                            body,
                            ExecutionFrameKind.Until,
                            untilNode.SourceLine,
                            NormalizeCondition(untilNode.Condition),
                            conditionKnown,
                            path: $"{frame.Path}/{nodeIndex}"));
                        LogAstWalker(
                            "frame_push",
                            $"Pushed until frame line={untilNode.SourceLine} path={frame.Path}/{nodeIndex}.");
                    }
                    continue;
                }
            }
        }

        LogAstWalker("step_scan_end", "No executable script node found.");
        return false;
    }

    private bool TryAdvanceCompletedLoop(ExecutionFrame frame, GameState state)
    {
        if (frame.Nodes.Count == 0)
            return false;

        if (frame.Kind == ExecutionFrameKind.Repeat)
        {
            frame.Index = 0;
            return true;
        }

        if (frame.Kind != ExecutionFrameKind.Until)
            return false;

        if (!frame.UntilConditionKnown || string.IsNullOrWhiteSpace(frame.UntilCondition))
            return false;

        if (!DslBooleanEvaluator.TryEvaluate(frame.UntilCondition, state, out var conditionValue))
            return false;

        if (conditionValue)
            return false;

        frame.Index = 0;
        return true;
    }

    private static bool ShouldEnterIf(
        string? condition,
        GameState state,
        out bool conditionKnown,
        out bool conditionValue)
    {
        if (!DslBooleanEvaluator.TryEvaluate(NormalizeCondition(condition), state, out var evaluated))
        {
            conditionKnown = false;
            conditionValue = false;
            return true;
        }

        conditionKnown = true;
        conditionValue = evaluated;
        return evaluated;
    }

    private static bool ShouldEnterUntil(
        string? condition,
        GameState state,
        out bool conditionKnown,
        out bool conditionValue)
    {
        var normalized = NormalizeCondition(condition);
        if (!DslBooleanEvaluator.TryEvaluate(normalized, state, out var evaluated))
        {
            conditionKnown = false;
            conditionValue = false;
            return true;
        }

        conditionKnown = true;
        conditionValue = evaluated;
        return !evaluated;
    }

    private static string NormalizeCondition(string? condition)
    {
        return (condition ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static CommandResult BuildCommandResult(DslCommandAstNode commandNode)
    {
        var command = new DslCommand(commandNode.Name, commandNode.Args);
        CommandResult result;
        try
        {
            result = command.ToValidCommand(state: null, command);
        }
        catch (FormatException ex) when (commandNode.SourceLine > 0)
        {
            throw new FormatException($"Line {commandNode.SourceLine}: {ex.Message}", ex);
        }

        result.SourceLine = commandNode.SourceLine > 0
            ? commandNode.SourceLine
            : null;
        return result;
    }

    private static void ValidateCommandNodes(IReadOnlyList<DslAstNode> nodes, GameState state)
    {
        foreach (var node in nodes ?? Array.Empty<DslAstNode>())
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                {
                    var command = new DslCommand(commandNode.Name, commandNode.Args);
                    try
                    {
                        _ = command.ToValidCommand(state, command);
                    }
                    catch (FormatException ex) when (commandNode.SourceLine > 0)
                    {
                        throw new FormatException($"Line {commandNode.SourceLine}: {ex.Message}", ex);
                    }
                    break;
                }

                case DslRepeatAstNode repeatNode:
                    ValidateCommandNodes(repeatNode.Body ?? Array.Empty<DslAstNode>(), state);
                    break;

                case DslIfAstNode ifNode:
                    ValidateCommandNodes(ifNode.Body ?? Array.Empty<DslAstNode>(), state);
                    break;

                case DslUntilAstNode untilNode:
                    ValidateCommandNodes(untilNode.Body ?? Array.Empty<DslAstNode>(), state);
                    break;
            }
        }
    }

    private void ResetFrames()
    {
        _frames.Clear();
        LogAstWalker("reset_frames", "Cleared execution frames.");
        if (_scriptAst?.Statements == null || _scriptAst.Statements.Count == 0)
            return;

        _frames.Add(new ExecutionFrame(
            _scriptAst.Statements,
            ExecutionFrameKind.Root,
            sourceLine: 1,
            untilCondition: null,
            untilConditionKnown: false,
            path: RootFramePath));
        LogAstWalker(
            "frame_push",
            $"Initialized root frame statements={_scriptAst.Statements.Count}.");
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

    private void RestoreFromCheckpoint(CommandExecutionCheckpoint checkpoint)
    {
        _memory.Clear();
        foreach (var memoryEntry in checkpoint.Memory.TakeLast(MaxMemory))
        {
            if (string.IsNullOrWhiteSpace(memoryEntry.Action))
                continue;

            _memory.Enqueue(new ActionMemory(
                memoryEntry.Action,
                memoryEntry.Arg1,
                memoryEntry.Quantity,
                memoryEntry.ResultMessage));
        }

        _requeuedSteps.Clear();
        foreach (var step in checkpoint.RequeuedSteps)
        {
            if (step == null || string.IsNullOrWhiteSpace(step.Action))
                continue;

            _requeuedSteps.AddLast(CloneStep(step));
        }

        _isHalted = checkpoint.IsHalted;
        _currentScriptLine = checkpoint.CurrentScriptLine;
        _activeCommand = null;
        _activeCommandResult = null;

        if (checkpoint.HadActiveCommand &&
            checkpoint.ActiveCommandResult != null &&
            !string.IsNullOrWhiteSpace(checkpoint.ActiveCommandResult.Action))
        {
            _requeuedSteps.AddFirst(CloneStep(checkpoint.ActiveCommandResult));
        }

        if (!TryRestoreFrames(checkpoint.Frames))
            ResetFrames();
    }

    private bool TryRestoreFrames(IReadOnlyList<ExecutionFrameCheckpoint> savedFrames)
    {
        if (_scriptAst?.Statements == null)
            return false;

        if (savedFrames == null || savedFrames.Count == 0)
            return false;

        var restored = new List<ExecutionFrame>(savedFrames.Count);
        foreach (var frameSnapshot in savedFrames)
        {
            if (!TryParseFrameKind(frameSnapshot.Kind, out var kind))
                return false;

            if (!TryResolveFrameNodes(frameSnapshot.Path, kind, out var nodes))
                return false;

            var frame = new ExecutionFrame(
                nodes,
                kind,
                frameSnapshot.SourceLine,
                frameSnapshot.UntilCondition,
                frameSnapshot.UntilConditionKnown,
                frameSnapshot.Path);

            frame.Index = Math.Clamp(frameSnapshot.Index, 0, frame.Nodes.Count);
            restored.Add(frame);
        }

        if (restored.Count == 0 || restored[0].Kind != ExecutionFrameKind.Root)
            return false;

        _frames.Clear();
        _frames.AddRange(restored);
        LogAstWalker(
            "restore_frames",
            $"Restored frame stack count={_frames.Count}.");
        return true;
    }

    private bool TryResolveFrameNodes(
        string path,
        ExecutionFrameKind kind,
        out IReadOnlyList<DslAstNode> nodes)
    {
        nodes = Array.Empty<DslAstNode>();
        if (_scriptAst?.Statements == null)
            return false;

        var normalizedPath = string.IsNullOrWhiteSpace(path)
            ? RootFramePath
            : path.Trim();

        if (!normalizedPath.StartsWith(RootFramePath, StringComparison.Ordinal))
            return false;

        if (string.Equals(normalizedPath, RootFramePath, StringComparison.Ordinal))
        {
            if (kind != ExecutionFrameKind.Root)
                return false;

            nodes = _scriptAst.Statements;
            return true;
        }

        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
            return false;

        IReadOnlyList<DslAstNode> currentNodes = _scriptAst.Statements;
        for (int i = 1; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], out var nodeIndex))
                return false;

            if (nodeIndex < 0 || nodeIndex >= currentNodes.Count)
                return false;

            var node = currentNodes[nodeIndex];
            currentNodes = node switch
            {
                DslRepeatAstNode repeatNode => repeatNode.Body,
                DslIfAstNode ifNode => ifNode.Body,
                DslUntilAstNode untilNode => untilNode.Body,
                _ => Array.Empty<DslAstNode>()
            };

            if (currentNodes.Count == 0 && i < segments.Length - 1)
                return false;
        }

        nodes = currentNodes;
        return true;
    }

    private static bool TryParseFrameKind(string? rawKind, out ExecutionFrameKind kind)
    {
        return Enum.TryParse(rawKind, ignoreCase: true, out kind);
    }

    private void LogAstWalker(string eventName, string detail)
    {
        _logger.LogAstWalker(eventName, $"{detail} stack={BuildFrameStackSummary()}");
    }

    private string BuildFrameStackSummary()
    {
        if (_frames.Count == 0)
            return "[]";

        return "[" + string.Join(
            " > ",
            _frames.Select(f => $"{f.Kind}:{f.Path}@{f.Index}/{f.Nodes.Count}")) + "]";
    }

    private void PersistCheckpoint()
    {
        if (_saveCheckpoint == null)
            return;

        try
        {
            _saveCheckpoint(BuildCheckpoint());
        }
        catch
        {
            // Checkpoint writes are best-effort.
        }
    }

    private CommandExecutionCheckpoint BuildCheckpoint()
    {
        return new CommandExecutionCheckpoint
        {
            Version = 1,
            Script = _script ?? string.Empty,
            IsHalted = _isHalted,
            CurrentScriptLine = _currentScriptLine,
            HadActiveCommand = _activeCommand != null,
            ActiveCommandResult = _activeCommandResult != null
                ? CloneStep(_activeCommandResult)
                : null,
            Memory = _memory.Select(m => new ActionMemoryCheckpoint
            {
                Action = m.Action,
                Arg1 = m.Arg1,
                Quantity = m.Quantity,
                ResultMessage = m.ResultMessage
            }).ToList(),
            RequeuedSteps = _requeuedSteps
                .Select(CloneStep)
                .ToList(),
            Frames = _frames.Select(f => new ExecutionFrameCheckpoint
            {
                Kind = f.Kind.ToString(),
                SourceLine = f.SourceLine,
                Index = f.Index,
                UntilCondition = f.UntilCondition,
                UntilConditionKnown = f.UntilConditionKnown,
                Path = f.Path
            }).ToList()
        };
    }

    private sealed class ExecutionFrame
    {
        public ExecutionFrame(
            IReadOnlyList<DslAstNode> nodes,
            ExecutionFrameKind kind,
            int sourceLine,
            string? untilCondition,
            bool untilConditionKnown,
            string path)
        {
            Nodes = nodes ?? Array.Empty<DslAstNode>();
            Kind = kind;
            SourceLine = sourceLine;
            UntilCondition = untilCondition;
            UntilConditionKnown = untilConditionKnown;
            Path = path;
        }

        public IReadOnlyList<DslAstNode> Nodes { get; }
        public ExecutionFrameKind Kind { get; }
        public int SourceLine { get; }
        public string? UntilCondition { get; }
        public bool UntilConditionKnown { get; }
        public string Path { get; }
        public int Index { get; set; }
    }

    private enum ExecutionFrameKind
    {
        Root,
        Repeat,
        If,
        Until
    }

    private record ActionMemory(
        string Action,
        string? Arg1,
        int? Quantity,
        string? ResultMessage);
}

public sealed record CommandExecutionCheckpoint
{
    public int Version { get; init; } = 1;
    public DateTime SavedAtUtc { get; init; } = DateTime.UtcNow;
    public string Script { get; init; } = string.Empty;
    public bool IsHalted { get; init; }
    public int? CurrentScriptLine { get; init; }
    public bool HadActiveCommand { get; init; }
    public CommandResult? ActiveCommandResult { get; init; }
    public IReadOnlyList<ActionMemoryCheckpoint> Memory { get; init; } = Array.Empty<ActionMemoryCheckpoint>();
    public IReadOnlyList<CommandResult> RequeuedSteps { get; init; } = Array.Empty<CommandResult>();
    public IReadOnlyList<ExecutionFrameCheckpoint> Frames { get; init; } = Array.Empty<ExecutionFrameCheckpoint>();
}

public sealed class ActionMemoryCheckpoint
{
    public string Action { get; set; } = string.Empty;
    public string? Arg1 { get; set; }
    public int? Quantity { get; set; }
    public string? ResultMessage { get; set; }
}

public sealed class ExecutionFrameCheckpoint
{
    public string Kind { get; set; } = string.Empty;
    public int SourceLine { get; set; }
    public int Index { get; set; }
    public string? UntilCondition { get; set; }
    public bool UntilConditionKnown { get; set; }
    public string Path { get; set; } = string.Empty;
}
