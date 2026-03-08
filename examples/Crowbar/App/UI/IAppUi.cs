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
        SpaceUiModel? spaceModel,
        IReadOnlyList<string> spaceConnectedSystems,
        TradeUiModel? tradeModel,
        ShipyardUiModel? shipyardModel,
        CatalogUiModel? catalogModel,
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
        IReadOnlyList<MissionPromptOption> availableMissionPrompts,
        IReadOnlyList<string> memory,
        IReadOnlyList<string> executionStatusLines,
        string? controlInput,
        int? currentScriptLine,
        string? lastGenerationPrompt,
        int? currentTick,
        System.DateTime? lastSpaceMoltPostUtc,
        IReadOnlyList<BotTab> bots,
        string? activeBotId,
        CraftingUiModel? craftingModel = null);

    void Run();
}
