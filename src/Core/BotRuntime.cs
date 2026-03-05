using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class BotRuntime
{
    private readonly RuntimeHost _host;

    public BotRuntime(
        string label,
        SpaceMoltAgent agent,
        SpaceMoltHttpClient client,
        IRuntimeStateProvider stateProvider,
        ChannelReader<string> controlInputReader,
        ChannelReader<string> generateScriptReader,
        ChannelReader<bool> saveExampleReader,
        ChannelReader<bool> haltNowReader,
        Func<bool> isLoopEnabled,
        Func<GameState?> getLatestState,
        Action<GameState> setLatestState,
        Func<DateTime> getLastHaltedSnapshotAt,
        Action<DateTime> setLastHaltedSnapshotAt,
        Action<GameState> publishSnapshot,
        Action<string> publishStatus,
        Action<string> log,
        Action<string> triggerGlobalStop,
        int scriptGenerationMaxAttempts)
    {
        _host = new RuntimeHost(
            label,
            agent,
            client,
            stateProvider,
            controlInputReader,
            generateScriptReader,
            saveExampleReader,
            haltNowReader,
            isLoopEnabled,
            getLatestState,
            setLatestState,
            getLastHaltedSnapshotAt,
            setLastHaltedSnapshotAt,
            publishSnapshot,
            publishStatus,
            log,
            triggerGlobalStop,
            scriptGenerationMaxAttempts);
    }

    public Task RunAsync(CancellationToken token)
    {
        return _host.RunAsync(token);
    }
}
