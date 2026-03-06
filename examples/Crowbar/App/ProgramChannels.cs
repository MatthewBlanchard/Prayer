using System.Threading.Channels;

public sealed class ProgramChannels
{
    private ProgramChannels(
        Channel<string> status,
        Channel<RuntimeCommandRequest> runtimeCommands,
        Channel<LoopUpdate> loopUpdates,
        Channel<string> switchBot,
        Channel<AddBotRequest> addBot,
        Channel<LlmProviderSelection> llmSelection,
        Channel<UiSnapshot> uiSnapshots)
    {
        Status = status;
        RuntimeCommands = runtimeCommands;
        LoopUpdates = loopUpdates;
        SwitchBot = switchBot;
        AddBot = addBot;
        LlmSelection = llmSelection;
        UiSnapshots = uiSnapshots;
    }

    public Channel<string> Status { get; }
    public Channel<RuntimeCommandRequest> RuntimeCommands { get; }
    public Channel<LoopUpdate> LoopUpdates { get; }
    public Channel<string> SwitchBot { get; }
    public Channel<AddBotRequest> AddBot { get; }
    public Channel<LlmProviderSelection> LlmSelection { get; }
    public Channel<UiSnapshot> UiSnapshots { get; }

    public static ProgramChannels CreateAndBind(IAppUi ui)
    {
        var channels = new ProgramChannels(
            Channel.CreateUnbounded<string>(),
            Channel.CreateUnbounded<RuntimeCommandRequest>(),
            Channel.CreateUnbounded<LoopUpdate>(),
            Channel.CreateUnbounded<string>(),
            Channel.CreateUnbounded<AddBotRequest>(),
            Channel.CreateUnbounded<LlmProviderSelection>(),
            Channel.CreateUnbounded<UiSnapshot>());

        ui.SetRuntimeCommandWriter(channels.RuntimeCommands.Writer);
        ui.SetLoopUpdateWriter(channels.LoopUpdates.Writer);
        ui.SetSwitchBotWriter(channels.SwitchBot.Writer);
        ui.SetAddBotWriter(channels.AddBot.Writer);
        ui.SetLlmSelectionWriter(channels.LlmSelection.Writer);

        return channels;
    }
}
