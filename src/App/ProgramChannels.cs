using System.Threading.Channels;

public sealed class ProgramChannels
{
    private ProgramChannels(
        Channel<string> status,
        Channel<string> controlInput,
        Channel<string> generateScript,
        Channel<bool> saveExample,
        Channel<bool> executeScript,
        Channel<string> switchBot,
        Channel<AddBotRequest> addBot,
        Channel<LlmProviderSelection> llmSelection,
        Channel<UiSnapshot> uiSnapshots)
    {
        Status = status;
        ControlInput = controlInput;
        GenerateScript = generateScript;
        SaveExample = saveExample;
        ExecuteScript = executeScript;
        SwitchBot = switchBot;
        AddBot = addBot;
        LlmSelection = llmSelection;
        UiSnapshots = uiSnapshots;
    }

    public Channel<string> Status { get; }
    public Channel<string> ControlInput { get; }
    public Channel<string> GenerateScript { get; }
    public Channel<bool> SaveExample { get; }
    public Channel<bool> ExecuteScript { get; }
    public Channel<string> SwitchBot { get; }
    public Channel<AddBotRequest> AddBot { get; }
    public Channel<LlmProviderSelection> LlmSelection { get; }
    public Channel<UiSnapshot> UiSnapshots { get; }

    public static ProgramChannels CreateAndBind(BotWindow ui)
    {
        var channels = new ProgramChannels(
            Channel.CreateUnbounded<string>(),
            Channel.CreateUnbounded<string>(),
            Channel.CreateUnbounded<string>(),
            Channel.CreateUnbounded<bool>(),
            Channel.CreateUnbounded<bool>(),
            Channel.CreateUnbounded<string>(),
            Channel.CreateUnbounded<AddBotRequest>(),
            Channel.CreateUnbounded<LlmProviderSelection>(),
            Channel.CreateUnbounded<UiSnapshot>());

        ui.SetControlInputWriter(channels.ControlInput.Writer);
        ui.SetGenerateScriptWriter(channels.GenerateScript.Writer);
        ui.SetSaveExampleWriter(channels.SaveExample.Writer);
        ui.SetExecuteScriptWriter(channels.ExecuteScript.Writer);
        ui.SetSwitchBotWriter(channels.SwitchBot.Writer);
        ui.SetAddBotWriter(channels.AddBot.Writer);
        ui.SetLlmSelectionWriter(channels.LlmSelection.Writer);

        return channels;
    }
}
