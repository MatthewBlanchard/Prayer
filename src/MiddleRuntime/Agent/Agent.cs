using System;
using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;

#region Agent

public class SpaceMoltAgent
{
    private const string ControlModeName = "ScriptMode";

    private readonly CommandExecutionEngine _execution;
    private readonly ScriptGenerationService _scriptGeneration;
    private readonly ScriptGenerationExampleStore _exampleStore;
    private readonly IAgentUiStateBuilder _uiStateBuilder;

    private string? _lastScriptGenerationPrompt;
    private string? _lastGeneratedScript;

    public bool IsHalted => _execution.IsHalted;
    public bool HasActiveCommand => _execution.HasActiveCommand;
    public string CurrentControlModeName => ControlModeName;
    public string? LastScriptGenerationPrompt => _lastScriptGenerationPrompt;
    public int? CurrentScriptLine => _execution.CurrentScriptLine;
    public string? CurrentControlInput => _execution.CurrentScript;

    private ChannelWriter<string>? _statusWriter;

    public SpaceMoltAgent(
        ILLMClient llm,
        ILLMClient? plannerLlm = null,
        PromptScriptRag? scriptExampleRag = null,
        Action<CommandExecutionCheckpoint>? saveCheckpoint = null)
    {
        var logger = new FileAgentLogger();
        _uiStateBuilder = new AgentUiStateBuilder();
        _exampleStore = new ScriptGenerationExampleStore(scriptExampleRag);
        _scriptGeneration = new ScriptGenerationService(
            plannerLlm ?? llm,
            _exampleStore,
            logger,
            AgentPrompt.BaseSystemPrompt,
            SetStatus);
        _execution = new CommandExecutionEngine(
            CommandCatalog.All,
            SetStatus,
            logger,
            ControlModeName,
            saveCheckpoint);
    }

    public void SetStatusWriter(ChannelWriter<string> writer)
    {
        _statusWriter = writer;
    }

    private void SetStatus(string status)
    {
        _statusWriter?.TryWrite(status);
    }

    public void SetScript(
        string script,
        GameState? state = null,
        bool preserveAssociatedPrompt = false)
    {
        var normalizedScript = _execution.SetScript(script ?? string.Empty, state);

        if (!preserveAssociatedPrompt &&
            !string.Equals(normalizedScript, _lastGeneratedScript, StringComparison.Ordinal))
        {
            _lastScriptGenerationPrompt = null;
            _lastGeneratedScript = null;
        }
        else if (preserveAssociatedPrompt && !string.IsNullOrWhiteSpace(_lastScriptGenerationPrompt))
        {
            // Keep prompt association in sync with the edited script.
            _lastGeneratedScript = normalizedScript;
        }
    }

    public async Task<string> GenerateScriptFromUserInputAsync(
        string userInput,
        GameState state,
        int maxAttempts = 3)
    {
        var generation = await _scriptGeneration.GenerateScriptFromUserInputAsync(
            userInput,
            state,
            maxAttempts);

        // Persist only the raw user request for prompt->script examples.
        _lastScriptGenerationPrompt = generation.UserPrompt;
        _lastGeneratedScript = generation.Script;

        return generation.Script;
    }

    public void ActivateScriptControl()
    {
        _execution.ActivateScriptControl();
    }

    public bool InterruptActiveCommand(string reason = "Interrupted")
    {
        return _execution.InterruptActiveCommand(reason);
    }

    public void Halt(string reason = "Halted")
    {
        _execution.Halt(reason);
    }

    public void ResumeFromHalt(string reason = "Resumed")
    {
        _execution.ResumeFromHalt(reason);
    }

    public async Task<(bool Added, string Message)> AddCurrentScriptAsExampleAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentControlInput))
            return (false, "No script loaded to save as an example.");

        if (string.IsNullOrWhiteSpace(_lastScriptGenerationPrompt))
            return (false, "No tracked generation prompt for this script.");

        var added = await _exampleStore.TryAddAsync(
            _lastScriptGenerationPrompt,
            CurrentControlInput);

        if (!added)
            return (false, "That script example is already saved.");

        return (true, $"Saved script example #{_exampleStore.Count}.");
    }

    public IReadOnlyList<string> GetMemoryList()
    {
        return _execution.GetMemoryList();
    }

    public string BuildMemoryBlock(int? maxRecent = null)
    {
        return _execution.BuildMemoryBlock(maxRecent);
    }

    public IReadOnlyList<string> GetAvailableActions(GameState state)
    {
        return _execution.GetAvailableActions(state);
    }

    public (
        string SpaceStateMarkdown,
        string? TradeStateMarkdown,
        string? ShipyardStateMarkdown,
        string? CantinaStateMarkdown,
        string? CatalogStateMarkdown)
        BuildUiState(GameState state)
    {
        return _uiStateBuilder.BuildUiState(state);
    }

    public Task ExecuteAsync(
        IRuntimeTransport client,
        CommandResult result,
        GameState state)
    {
        return _execution.ExecuteAsync(client, result, state);
    }

    public Task<CommandResult?> DecideAsync(GameState state)
    {
        return _execution.DecideAsync(state);
    }

    public bool TryRestoreCheckpoint(
        CommandExecutionCheckpoint? checkpoint,
        GameState? state = null)
    {
        return _execution.TryRestoreCheckpoint(checkpoint, state);
    }

    public void RequeueScriptStep(CommandResult step)
    {
        _execution.RequeueScriptStep(step);
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
