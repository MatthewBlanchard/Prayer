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

    // Skill library — provides named skills and always-on overrides.
    private SkillLibrary? _skillLibrary;

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

    public string? ActiveOverrideName
    {
        get
        {
            for (int i = _frames.Count - 1; i >= 0; i--)
            {
                if (_frames[i].Kind == ExecutionFrameKind.Override)
                    return _frames[i].OverrideName;
            }
            return null;
        }
    }

    /// <summary>
    /// Sets or replaces the skill library. Skills become valid commands in subsequently
    /// loaded scripts, and overrides are evaluated before each script step.
    /// </summary>
    public void SetSkillLibrary(SkillLibrary? library)
    {
        _skillLibrary = library;
    }

    public string SetScript(string script, GameState? state = null)
    {
        EnsureActiveCommandInvariant();
        var rawScript = script ?? string.Empty;
        _currentScriptLine = null;

        var skillDefs = _skillLibrary?.Skills.ToDictionary(
            s => s.Name,
            s => s.Params,
            StringComparer.OrdinalIgnoreCase);
        var tree = DslParser.ParseTree(rawScript, skillDefs);
        if (state != null)
            ValidateCommandNodes(tree.Statements, state);

        var normalizedSteps = DslScriptTransformer.Translate(tree);
        _script = DslScriptTransformer.RenderScript(tree).TrimEnd();
        _scriptAst = tree;

        _logger.LogScriptNormalization("set_script", rawScript, _script);

        ResetFrames(state);
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

        // Overrides are hard safety mechanisms — check them unconditionally,
        // even while halted or between scripts.
        if (!HasActiveCommand)
            TryTriggerOverride(state);

        if (_isHalted && _frames.Count == 0)
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

            _currentScriptLine = next.SourceLine ?? GetCallSiteSourceLine();
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
                if (frame.Kind == ExecutionFrameKind.Override && frame.OverrideName != null)
                    _logger.LogOverride("completed", frame.OverrideName, $"path={frame.Path}");
                continue;
            }

            int nodeIndex = frame.Index;
            var node = frame.Nodes[frame.Index++];

            switch (node)
            {
                case DslCommandAstNode commandNode:
                {
                    // Skill call: push a new Skill frame instead of emitting a command.
                    if (_skillLibrary != null &&
                        _skillLibrary.TryGetSkill(commandNode.Name, out var skillDef))
                    {
                        var bindings = BuildSkillBindings(skillDef!, commandNode.Args, state);
                        if (!string.IsNullOrWhiteSpace(state.System))
                            bindings["here"] = state.System;
                        PushSkillFrame(skillDef!, bindings, $"{frame.Path}/{nodeIndex}", commandNode.SourceLine);
                        LogAstWalker(
                            "skill_call",
                            $"Pushed skill frame skill={skillDef!.Name} path={frame.Path}/{nodeIndex}.");
                        continue;
                    }

                    var substituted = SubstituteBindings(commandNode);
                    result = BuildCommandResult(substituted, state);
                    LogAstWalker(
                        "emit_command",
                        $"Selected command line={result.SourceLine?.ToString() ?? "?"} cmd={FormatCommand(result)} path={frame.Path}/{nodeIndex}.");
                    return true;
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

        if (!DslBooleanEvaluator.TryEvaluate(frame.UntilCondition, state, out var conditionValue, GetActiveFrameBindings()))
            return false;

        if (conditionValue)
            return false;

        frame.Index = 0;
        return true;
    }

    private bool ShouldEnterIf(
        DslConditionAstNode condition,
        GameState state,
        out bool conditionKnown,
        out bool conditionValue)
    {
        if (!DslBooleanEvaluator.TryEvaluate(condition, state, out var evaluated, GetActiveFrameBindings()))
        {
            conditionKnown = false;
            conditionValue = false;
            return true;
        }

        conditionKnown = true;
        conditionValue = evaluated;
        return evaluated;
    }

    private bool ShouldEnterUntil(
        DslConditionAstNode condition,
        GameState state,
        out bool conditionKnown,
        out bool conditionValue)
    {
        if (!DslBooleanEvaluator.TryEvaluate(condition, state, out var evaluated, GetActiveFrameBindings()))
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

    private void ValidateCommandNodes(IReadOnlyList<DslAstNode> nodes, GameState state)
    {
        foreach (var node in nodes ?? Array.Empty<DslAstNode>())
        {
            switch (node)
            {
                case DslCommandAstNode commandNode:
                {
                    // Skill calls are validated at runtime when the frame is pushed; skip here.
                    if (_skillLibrary != null &&
                        _skillLibrary.TryGetSkill(commandNode.Name, out _))
                        break;

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

    private void ResetFrames(GameState? state = null)
    {
        _frames.Clear();
        LogAstWalker("reset_frames", "Cleared execution frames.");
        if (_scriptAst?.Statements == null || _scriptAst.Statements.Count == 0)
            return;

        Dictionary<string, string>? rootBindings = null;
        if (!string.IsNullOrWhiteSpace(state?.System))
        {
            rootBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["here"] = state.System
            };
        }

        _frames.Add(new ExecutionFrame(
            _scriptAst.Statements,
            ExecutionFrameKind.Root,
            sourceLine: 1,
            untilCondition: null,
            untilConditionKnown: false,
            path: RootFramePath,
            bindings: rootBindings));
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
            // Accumulate into the innermost scoped frame's counters when inside a skill/override,
            // otherwise accumulate into the root script counters.
            var scopedFrame = GetInnermostScopedFrame();

            if (string.Equals(_pendingDeltaAction, "mine", StringComparison.OrdinalIgnoreCase))
            {
                var target = scopedFrame?.FrameMinedByItem ?? _minedByItem;
                AccumulatePositiveDelta(target, _lastObservedState.CargoByItem, current.CargoByItem);
            }
            else if (string.Equals(_pendingDeltaAction, "stash", StringComparison.OrdinalIgnoreCase))
            {
                var target = scopedFrame?.FrameStashedByItem ?? _stashedByItem;
                AccumulatePositiveDelta(target, _lastObservedState.StorageByItem, current.StorageByItem);
            }
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
        // Inside a Skill/Override frame, expose that frame's counters so MINED()/STASHED()
        // conditions reflect only what the current subscript has done.
        var scopedFrame = GetInnermostScopedFrame();
        if (scopedFrame != null)
        {
            state.ScriptMinedByItem = new Dictionary<string, int>(
                scopedFrame.FrameMinedByItem!, StringComparer.OrdinalIgnoreCase);
            state.ScriptStashedByItem = new Dictionary<string, int>(
                scopedFrame.FrameStashedByItem!, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            state.ScriptMinedByItem = new Dictionary<string, int>(_minedByItem, StringComparer.OrdinalIgnoreCase);
            state.ScriptStashedByItem = new Dictionary<string, int>(_stashedByItem, StringComparer.OrdinalIgnoreCase);
        }
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

    // ─── Skill / Override helpers ─────────────────────────────────────────────

    /// <summary>
    /// Returns the innermost frame that carries scoped counters (Skill or Override kind).
    /// Null when the current stack has no Skill/Override frames active.
    /// </summary>
    private ExecutionFrame? GetInnermostScopedFrame()
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var f = _frames[i];
            if (f.Kind == ExecutionFrameKind.Skill || f.Kind == ExecutionFrameKind.Override)
                return f;
        }
        return null;
    }

    /// <summary>
    /// Returns the call-site source line from the innermost Skill frame, or null if none.
    /// Used to display the parent script line number while a skill body is executing.
    /// </summary>
    private int? GetCallSiteSourceLine()
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            if (_frames[i].Kind == ExecutionFrameKind.Skill && _frames[i].SourceLine > 0)
                return _frames[i].SourceLine;
        }
        return null;
    }

    /// <summary>
    /// Returns the bindings from the innermost Skill frame on the stack, or null if none.
    /// </summary>
    private IReadOnlyDictionary<string, string>? GetActiveFrameBindings()
    {
        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var f = _frames[i];
            if (f.Bindings != null &&
                (f.Kind == ExecutionFrameKind.Skill ||
                 f.Kind == ExecutionFrameKind.Root ||
                 f.Kind == ExecutionFrameKind.Override))
                return f.Bindings;
        }
        return null;
    }

    /// <summary>
    /// Returns a copy of <paramref name="node"/> with any $param tokens in its args
    /// replaced by values from the active Skill frame bindings.
    /// Global macros ($home, $here, etc.) are left as-is for runtime resolution.
    /// </summary>
    private DslCommandAstNode SubstituteBindings(DslCommandAstNode node)
    {
        var bindings = GetActiveFrameBindings();
        if (bindings == null || bindings.Count == 0)
            return node;

        bool changed = false;
        var newArgs = new List<string>(node.Args.Count);
        foreach (var arg in node.Args)
        {
            if (arg.StartsWith("$", StringComparison.Ordinal) &&
                bindings.TryGetValue(arg[1..], out var bound))
            {
                newArgs.Add(bound);
                changed = true;
            }
            else
            {
                newArgs.Add(arg);
            }
        }

        return changed ? node with { Args = newArgs } : node;
    }

    /// <summary>
    /// Resolves a call-site arg token, checking the active frame bindings first,
    /// then falling through to global macro expansion.
    /// </summary>
    private string ResolveCallSiteArg(string arg, GameState state)
    {
        if (!arg.StartsWith("$", StringComparison.Ordinal))
            return arg;

        var name = arg[1..];
        var bindings = GetActiveFrameBindings();
        if (bindings != null && bindings.TryGetValue(name, out var bound))
            return bound;

        // Fall through to global macro resolution.
        return DslParser.ExpandMacroArg(arg, state);
    }

    /// <summary>
    /// Builds a bindings dictionary by mapping skill parameter names to resolved call-site arg values.
    /// </summary>
    private Dictionary<string, string> BuildSkillBindings(
        DslSkillAstNode skill,
        IReadOnlyList<string> callSiteArgs,
        GameState state)
    {
        var @params = skill.Params;

        int requiredCount = @params.Count; // all params are required (no defaults in skills)
        if (callSiteArgs.Count < requiredCount)
            throw new FormatException(
                $"Skill '{skill.Name}' requires {requiredCount} argument(s), got {callSiteArgs.Count}.");

        if (callSiteArgs.Count > @params.Count)
            throw new FormatException(
                $"Skill '{skill.Name}' takes {requiredCount} argument(s), got {callSiteArgs.Count}.");

        var bindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < @params.Count; i++)
        {
            var resolved = ResolveCallSiteArg(callSiteArgs[i], state);
            bindings[@params[i].Name] = resolved;
        }

        return bindings;
    }

    /// <summary>Pushes a new Skill execution frame onto the stack.</summary>
    private void PushSkillFrame(
        DslSkillAstNode skill,
        IReadOnlyDictionary<string, string> bindings,
        string parentPath,
        int callSiteSourceLine = 0)
    {
        _frames.Add(new ExecutionFrame(
            skill.Body,
            ExecutionFrameKind.Skill,
            sourceLine: callSiteSourceLine,
            untilCondition: null,
            untilConditionKnown: false,
            path: $"skill/{skill.Name}",
            bindings: bindings));

        LogAstWalker(
            "frame_push",
            $"Pushed skill frame skill={skill.Name} parent={parentPath}.");
    }

    /// <summary>
    /// Checks registered overrides and pushes an Override frame for the first one whose
    /// condition is true and whose frame is not already on the stack. At most one fires per tick.
    /// </summary>
    private void TryTriggerOverride(GameState state)
    {
        if (_skillLibrary == null)
            return;

        foreach (var ov in _skillLibrary.Overrides)
        {
            if (!ov.Enabled) continue;

            // Don't re-trigger while the override's own frame is already executing.
            bool alreadyActive = false;
            for (int i = 0; i < _frames.Count; i++)
            {
                if (_frames[i].Kind == ExecutionFrameKind.Override &&
                    string.Equals(_frames[i].OverrideName, ov.Name, StringComparison.OrdinalIgnoreCase))
                {
                    alreadyActive = true;
                    break;
                }
            }
            if (alreadyActive) continue;

            if (!DslBooleanEvaluator.TryEvaluate(ov.Condition, state, out var fired) || !fired)
                continue;

            Dictionary<string, string>? overrideBindings = null;
            if (!string.IsNullOrWhiteSpace(state?.System))
            {
                overrideBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["here"] = state.System
                };
            }

            _frames.Add(new ExecutionFrame(
                ov.Body,
                ExecutionFrameKind.Override,
                sourceLine: 0,
                untilCondition: null,
                untilConditionKnown: false,
                path: $"override/{ov.Name}",
                overrideName: ov.Name,
                bindings: overrideBindings));

            LogAstWalker(
                "override_trigger",
                $"Override '{ov.Name}' fired cond={DslBooleanEvaluator.RenderCondition(ov.Condition)}.");
            _logger.LogOverride(
                "triggered",
                ov.Name,
                $"cond={DslBooleanEvaluator.RenderCondition(ov.Condition)} halted={_isHalted}");

            break; // Only one override per tick.
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

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
            string path,
            IReadOnlyDictionary<string, string>? bindings = null,
            string? overrideName = null)
        {
            Nodes = nodes ?? Array.Empty<DslAstNode>();
            Kind = kind;
            SourceLine = sourceLine;
            UntilCondition = untilCondition;
            UntilConditionKnown = untilConditionKnown;
            Path = path;
            Bindings = bindings;
            OverrideName = overrideName;

            // Skill and Override frames each carry their own isolated counters.
            if (kind == ExecutionFrameKind.Skill || kind == ExecutionFrameKind.Override)
            {
                FrameMinedByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                FrameStashedByItem = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public IReadOnlyList<DslAstNode> Nodes { get; }
        public ExecutionFrameKind Kind { get; }
        public int SourceLine { get; }
        public DslConditionAstNode? UntilCondition { get; }
        public bool UntilConditionKnown { get; }
        public string Path { get; }
        public int Index { get; set; }

        /// <summary>Skill parameter bindings ($param -> resolved value). Null for non-Skill frames.</summary>
        public IReadOnlyDictionary<string, string>? Bindings { get; }

        /// <summary>Name of the override this frame belongs to. Null for non-Override frames.</summary>
        public string? OverrideName { get; }

        /// <summary>Scoped MINED counter for Skill/Override frames. Null for other frame kinds.</summary>
        public Dictionary<string, int>? FrameMinedByItem { get; }

        /// <summary>Scoped STASHED counter for Skill/Override frames. Null for other frame kinds.</summary>
        public Dictionary<string, int>? FrameStashedByItem { get; }
    }

    private enum ExecutionFrameKind
    {
        Root,
        If,
        Until,
        Skill,
        Override
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
