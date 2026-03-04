using System.Collections.Generic;
using System.Threading.Channels;

public interface IAppUi
{
    bool LoopEnabled { get; }

    void SetControlInputWriter(ChannelWriter<string> writer);
    void SetGenerateScriptWriter(ChannelWriter<string> writer);
    void SetSaveExampleWriter(ChannelWriter<bool> writer);
    void SetExecuteScriptWriter(ChannelWriter<bool> writer);
    void SetHaltNowWriter(ChannelWriter<bool> writer);
    void SetSwitchBotWriter(ChannelWriter<string> writer);
    void SetAddBotWriter(ChannelWriter<AddBotRequest> writer);
    void SetLlmSelectionWriter(ChannelWriter<LlmProviderSelection> writer);

    void ConfigureInitialLlmSelection(string provider, string model);
    void SetAvailableProviders(IReadOnlyList<string> providers);
    void SetProviderModels(string provider, IReadOnlyList<string> models);

    void Render(
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
        string? activeBotId);

    void Run();
}
