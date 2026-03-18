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
    private readonly Dictionary<string, int> _minedByItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _stashedByItem = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxMemory = 12;

    private string? _script;
    private DslAstProgram? _scriptAst;
    private int? _currentScriptLine;
    private bool _isHalted;
    private ActiveCommandState _activeCommandState;
    private IMultiTurnCommand? _activeCommand;
    private CommandResult? _activeCommandResult;
    private StateSnapshot? _lastObservedState;
    private string? _pendingDeltaAction;

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
    public bool HasActiveCommand => _activeCommandState == ActiveCommandState.MultiTurn;
    public int? CurrentScriptLine => _currentScriptLine;
    public string? CurrentScript => string.IsNullOrWhiteSpace(_script) ? null : _script;

    public string SetScript(string script, GameState? state = null)
    {
        EnsureActiveCommandInvariant();
        var rawScript = script ?? string.Empty;
        _currentScriptLine = null;

        var tree = DslParser.ParseTree(rawScript);
        if (state != null)
            ValidateCommandNodes(tree.Statements, state);

        var normalizedSteps = DslScriptTransformer.Translate(tree);
        _script = DslScriptTransformer.RenderScript(tree).TrimEnd();
        _scriptAst = tree;

        _logger.LogScriptNormalization("set_script", rawScript, _script);

        ResetFrames();
        ResetScriptConditionCounters();
        ClearActiveCommand();
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
        EnsureActiveCommandInvariant();
        _isHalted = false;
        _setStatus($"Mode: {_controlModeName}");
        PersistCheckpoint();
    }

    public bool InterruptActiveCommand(string reason = "Interrupted")
    {
        EnsureActiveCommandInvariant();
        if (!HasActiveCommand)
        {
            _logger.LogAstWalker("interrupt_ignored", $"reason={reason}");
            return false;
        }

        _logger.LogAstWalker("interrupt_active_command", $"reason={reason}");
        ClearActiveCommand();
        _setStatus(reason);
        PersistCheckpoint();
        return true;
    }

    public void Halt(string reason = "Halted")
    {
        EnsureActiveCommandInvariant();
        _logger.LogAstWalker("halt", $"reason={reason}");
        ClearActiveCommand();
        _isHalted = true;
        _currentScriptLine = null;
        _setStatus(reason);
        PersistCheckpoint();
    }

    public void ResumeFromHalt(string reason = "Resumed")
    {
        EnsureActiveCommandInvariant();
        _logger.LogAstWalker("resume_from_halt", $"reason={reason}");
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

    public async Task<string?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult result,
        GameState state)
    {
        EnsureActiveCommandInvariant();
        ObserveStateAndApplyCounters(state);
        string? message = null;
        bool shouldAddMemory = false;
        bool haltScript = false;

        if (HasActiveCommand)
        {
            string activeCommandText = FormatCommand(_activeCommandResult!);

            await _logger.LogCommandExecutionAsync(
                _controlModeName,
                activeCommandText,
                state,
                phase: "continue-start");

            _setStatus($"Executing: continuing {activeCommandText}");

            (bool finished, CommandExecutionResult? result) continuation;
            try
            {
                continuation = await _activeCommand!.ContinueAsync(client, state);
            }
            catch
            {
                ClearActiveCommand();
                PersistCheckpoint();
                throw;
            }

            bool finished = continuation.Item1;
            var response = continuation.Item2;
            SetPendingDeltaAction(_activeCommandResult!.Action);

            if (finished)
            {
                message = response?.ResultMessage;
                shouldAddMemory = true;
                haltScript = response?.HaltScript == true;
                AddMemory(_activeCommandResult!, message);
                ClearActiveCommand();
                _setStatus("Waiting");
            }

            PersistCheckpoint();

            if (haltScript)
            {
                _logger.LogScriptCommandFailure(activeCommandText, message ?? "Unknown failure");
                Halt(message ?? "Script halted by command failure");
            }

            EnsureActiveCommandInvariant();

            await _logger.LogCommandExecutionAsync(
                _controlModeName,
                activeCommandText,
                state,
                phase: "continue-end",
                details: message ?? (finished ? "completed" : "in-progress"));

            return message;
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
                SetActiveCommand(multiTurnCommand, result);
                PersistCheckpoint();

                (bool finished, CommandExecutionResult? response) startResult;
                try
                {
                    startResult = await multiTurnCommand.StartAsync(client, result, state);
                }
                catch
                {
                    ClearActiveCommand();
                    PersistCheckpoint();
                    throw;
                }

                if (startResult.finished)
                {
                    message = startResult.response?.ResultMessage;
                    shouldAddMemory = true;
                    haltScript = startResult.response?.HaltScript == true;
                    ClearActiveCommand();
                    _setStatus("Waiting");
                }

                SetPendingDeltaAction(result.Action);
            }
            else if (command is ISingleTurnCommand singleTurnCommand)
            {
                _setStatus($"Executing: run {FormatCommand(result)}");
                var response = await singleTurnCommand.ExecuteAsync(client, result, state);
                message = response?.ResultMessage;
                shouldAddMemory = true;
                haltScript = response?.HaltScript == true;
                _setStatus("Waiting");
                SetPendingDeltaAction(result.Action);
            }
        }

        if (shouldAddMemory)
            AddMemory(result, message);
        PersistCheckpoint();

        if (haltScript)
        {
            _logger.LogScriptCommandFailure(FormatCommand(result), message ?? "Unknown failure");
            Halt(message ?? "Script halted by command failure");
        }

        EnsureActiveCommandInvariant();

        await _logger.LogCommandExecutionAsync(
            _controlModeName,
            FormatCommand(result),
            state,
            phase: "end",
            details: message ?? "(no result message)");

        return message;
    }

    public Task<CommandResult?> DecideAsync(GameState state)
    {
        EnsureActiveCommandInvariant();
        ObserveStateAndApplyCounters(state);
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
        EnsureActiveCommandInvariant();
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
            else
            {
                try
                {
                    if (!TryGetNextScriptCommand(state, out next))
                    {
                        Halt("Script complete: waiting for input");
                        return Task.FromResult<CommandResult?>(null);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    Halt($"Script condition error: {ex.Message}");
                    return Task.FromResult<CommandResult?>(null);
                }
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
                    result = BuildCommandResult(commandNode, state);
                    LogAstWalker(
                        "emit_command",
                        $"Selected command line={result.SourceLine?.ToString() ?? "?"} cmd={FormatCommand(result)} path={frame.Path}/{nodeIndex}.");
                    return true;

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
                        $"Visited if line={ifNode.SourceLine} cond={DslBooleanEvaluator.RenderCondition(ifNode.Condition)} known={conditionKnown} value={conditionValue} enter={shouldEnter} bodyCount={body.Count} path={frame.Path}/{nodeIndex}.");
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
                        $"Visited until line={untilNode.SourceLine} cond={DslBooleanEvaluator.RenderCondition(untilNode.Condition)} known={conditionKnown} value={conditionValue} enter={shouldEnter} bodyCount={body.Count} path={frame.Path}/{nodeIndex}.");
                    if (shouldEnter && body.Count > 0)
                    {
                        _frames.Add(new ExecutionFrame(
                            body,
                            ExecutionFrameKind.Until,
                            untilNode.SourceLine,
                            untilNode.Condition,
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

        if (frame.Kind != ExecutionFrameKind.Until)
            return false;

        if (!frame.UntilConditionKnown || frame.UntilCondition == null)
            return false;

        if (!DslBooleanEvaluator.TryEvaluate(frame.UntilCondition, state, out var conditionValue))
            return false;

        if (conditionValue)
            return false;

        frame.Index = 0;
        return true;
    }

    private static bool ShouldEnterIf(
        DslConditionAstNode condition,
        GameState state,
        out bool conditionKnown,
        out bool conditionValue)
    {
        if (!DslBooleanEvaluator.TryEvaluate(condition, state, out var evaluated))
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
        DslConditionAstNode condition,
        GameState state,
        out bool conditionKnown,
        out bool conditionValue)
    {
        if (!DslBooleanEvaluator.TryEvaluate(condition, state, out var evaluated))
        {
            conditionKnown = false;
            conditionValue = false;
            return true;
        }

        conditionKnown = true;
        conditionValue = evaluated;
        return !evaluated;
    }

    private static CommandResult BuildCommandResult(DslCommandAstNode commandNode, GameState state)
    {
        var command = new DslCommand(commandNode.Name, commandNode.Args);
        CommandResult result;
        try
        {
            result = command.ToValidCommand(state, command);
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
            result.Args.ToList(),
            message));
    }

    private static string FormatAction(ActionMemory m)
    {
        if (m.Args.Count == 0)
            return m.Action;
        return $"{m.Action} {string.Join(" ", m.Args.Where(a => !string.IsNullOrWhiteSpace(a)))}";
    }

    private static string FormatCommand(CommandResult cmd)
    {
        if (cmd.Args.Count == 0)
            return cmd.Action;
        return $"{cmd.Action} {string.Join(" ", cmd.Args.Where(a => !string.IsNullOrWhiteSpace(a)))}";
    }

    private static CommandResult CloneStep(CommandResult step)
    {
        return new CommandResult
        {
            Action = step.Action,
            Args = step.Args.ToList(),
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
                memoryEntry.Args ?? new List<string>(),
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
        ClearActiveCommand();

        if (checkpoint.HadActiveCommand &&
            checkpoint.ActiveCommandResult != null &&
            !string.IsNullOrWhiteSpace(checkpoint.ActiveCommandResult.Action))
        {
            _requeuedSteps.AddFirst(CloneStep(checkpoint.ActiveCommandResult));
        }

        RestoreScriptConditionCounters(checkpoint);

        if (!TryRestoreFrames(checkpoint.Frames))
            ResetFrames();

        EnsureActiveCommandInvariant();
    }

    private void SetActiveCommand(IMultiTurnCommand command, CommandResult result)
    {
        _activeCommand = command ?? throw new ArgumentNullException(nameof(command));
        _activeCommandResult = result ?? throw new ArgumentNullException(nameof(result));
        _activeCommandState = ActiveCommandState.MultiTurn;
        EnsureActiveCommandInvariant();
    }

    private void ClearActiveCommand()
    {
        _activeCommand = null;
        _activeCommandResult = null;
        _activeCommandState = ActiveCommandState.Idle;
    }

    private void EnsureActiveCommandInvariant()
    {
        bool hasCommand = _activeCommand != null;
        bool hasCommandResult = _activeCommandResult != null;
        bool stateSaysActive = _activeCommandState == ActiveCommandState.MultiTurn;
        bool refsMatch = hasCommand == hasCommandResult;

        if (!refsMatch || stateSaysActive != hasCommand)
        {
            throw new InvalidOperationException(
                $"Invalid command execution state: activeState={_activeCommandState}, hasCommand={hasCommand}, hasCommandResult={hasCommandResult}.");
        }
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

            if (!TryParseCheckpointCondition(frameSnapshot.UntilCondition, out var untilCondition))
                return false;

            var frame = new ExecutionFrame(
                nodes,
                kind,
                frameSnapshot.SourceLine,
                untilCondition,
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
                Args = m.Args.ToList(),
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
                UntilCondition = DslBooleanEvaluator.RenderCondition(f.UntilCondition),
                UntilConditionKnown = f.UntilConditionKnown,
                Path = f.Path
            }).ToList(),
            MinedByItem = new Dictionary<string, int>(_minedByItem, StringComparer.OrdinalIgnoreCase),
            StashedByItem = new Dictionary<string, int>(_stashedByItem, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool TryParseCheckpointCondition(string? condition, out DslConditionAstNode? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(condition))
            return true;

        return DslParser.TryParseCondition(condition, out parsed, out _);
    }

    private void ResetScriptConditionCounters()
    {
        _minedByItem.Clear();
        _stashedByItem.Clear();
        _lastObservedState = null;
        _pendingDeltaAction = null;
    }

    private void RestoreScriptConditionCounters(CommandExecutionCheckpoint checkpoint)
    {
        _minedByItem.Clear();
        foreach (var entry in checkpoint.MinedByItem)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;
            _minedByItem[entry.Key] = Math.Max(0, entry.Value);
        }

        _stashedByItem.Clear();
        foreach (var entry in checkpoint.StashedByItem)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;
            _stashedByItem[entry.Key] = Math.Max(0, entry.Value);
        }

        _lastObservedState = null;
        _pendingDeltaAction = null;
    }

    private void ObserveStateAndApplyCounters(GameState state)
    {
        if (state == null)
            return;

        var current = CaptureStateSnapshot(state);

        if (_lastObservedState != null &&
            !string.IsNullOrWhiteSpace(_pendingDeltaAction))
        {
            if (string.Equals(_pendingDeltaAction, "mine", StringComparison.OrdinalIgnoreCase))
                AccumulatePositiveDelta(_minedByItem, _lastObservedState.CargoByItem, current.CargoByItem);
            else if (string.Equals(_pendingDeltaAction, "stash", StringComparison.OrdinalIgnoreCase))
                AccumulatePositiveDelta(_stashedByItem, _lastObservedState.StorageByItem, current.StorageByItem);
        }

        _lastObservedState = current;
        _pendingDeltaAction = null;
        ApplyCountersToState(state);
    }

    private void SetPendingDeltaAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return;

        _pendingDeltaAction = action.Trim();
    }

    private void ApplyCountersToState(GameState state)
    {
        state.ScriptMinedByItem = new Dictionary<string, int>(_minedByItem, StringComparer.OrdinalIgnoreCase);
        state.ScriptStashedByItem = new Dictionary<string, int>(_stashedByItem, StringComparer.OrdinalIgnoreCase);
    }

    private static StateSnapshot CaptureStateSnapshot(GameState state)
    {
        return new StateSnapshot(
            CopyItemStacks(state.Ship?.Cargo),
            CopyItemStacks(state.StorageItems));
    }

    private static Dictionary<string, int> CopyItemStacks(Dictionary<string, ItemStack>? source)
    {
        var snapshot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (source == null)
            return snapshot;

        foreach (var (itemId, stack) in source)
        {
            if (string.IsNullOrWhiteSpace(itemId))
                continue;

            int quantity = stack?.Quantity ?? 0;
            if (quantity > 0)
                snapshot[itemId] = quantity;
        }

        return snapshot;
    }

    private static void AccumulatePositiveDelta(
        Dictionary<string, int> totals,
        Dictionary<string, int> before,
        Dictionary<string, int> after)
    {
        foreach (var (itemId, afterQuantity) in after)
        {
            before.TryGetValue(itemId, out var beforeQuantity);
            int delta = afterQuantity - beforeQuantity;
            if (delta <= 0)
                continue;

            totals.TryGetValue(itemId, out var existing);
            totals[itemId] = existing + delta;
        }
    }

    private sealed record StateSnapshot(
        Dictionary<string, int> CargoByItem,
        Dictionary<string, int> StorageByItem);

    private sealed class ExecutionFrame
    {
        public ExecutionFrame(
            IReadOnlyList<DslAstNode> nodes,
            ExecutionFrameKind kind,
            int sourceLine,
            DslConditionAstNode? untilCondition,
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
        public DslConditionAstNode? UntilCondition { get; }
        public bool UntilConditionKnown { get; }
        public string Path { get; }
        public int Index { get; set; }
    }

    private enum ExecutionFrameKind
    {
        Root,
        If,
        Until
    }

    private enum ActiveCommandState
    {
        Idle,
        MultiTurn
    }

    private record ActionMemory(
        string Action,
        IReadOnlyList<string> Args,
        string? ResultMessage);
}
