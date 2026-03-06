using System.Collections.Generic;
using System.Threading.Channels;

public interface IAppUi
{
    void SetRuntimeCommandWriter(ChannelWriter<RuntimeCommandRequest> writer);
    void SetSwitchBotWriter(ChannelWriter<string> writer);
    void SetAddBotWriter(ChannelWriter<AddBotRequest> writer);
    void SetLlmSelectionWriter(ChannelWriter<LlmProviderSelection> writer);

    void ConfigureInitialLlmSelection(string provider, string model);
    void SetAvailableProviders(IReadOnlyList<string> providers);
    void SetProviderModels(string provider, IReadOnlyList<string> models);

    void Render(
        string spaceStateMarkdown,
        IReadOnlyList<string> spaceConnectedSystems,
        string? tradeStateMarkdown,
        TradeUiModel? tradeModel,
        string? shipyardStateMarkdown,
        ShipyardUiModel? shipyardModel,
        string? missionsStateMarkdown,
        string? catalogStateMarkdown,
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
        IReadOnlyList<MissionPromptOption> availableMissionPrompts,
        IReadOnlyList<string> memory,
        IReadOnlyList<string> executionStatusLines,
        string? controlInput,
        int? currentScriptLine,
        string? lastGenerationPrompt,
        IReadOnlyList<BotTab> bots,
        string? activeBotId);

    void Run();
}
