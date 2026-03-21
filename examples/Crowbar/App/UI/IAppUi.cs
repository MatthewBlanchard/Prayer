using System.Collections.Generic;
using System.Threading.Channels;

public interface IAppUi
{
    void SetRuntimeCommandWriter(ChannelWriter<RuntimeCommandRequest> writer);
    void SetAddBotWriter(ChannelWriter<AddBotRequest> writer);
    void SetLlmSelectionWriter(ChannelWriter<LlmProviderSelection> writer);

    void ConfigureInitialLlmSelection(string provider, string model);
    void SetAvailableProviders(IReadOnlyList<string> providers);
    void SetProviderModels(string provider, IReadOnlyList<string> models);

    void Render(
        IReadOnlyDictionary<string, BotStateEntry> botStates,
        IReadOnlyList<BotRouteOverlay> botRoutes,
        IReadOnlyList<BotTab> bots,
        IReadOnlyList<BotMapMarker> botMapMarkers,
        string? defaultBotId);

    void Run();
}
