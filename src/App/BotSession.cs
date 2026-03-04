using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class BotSession
{
    public BotSession(
        string id,
        string label,
        SpaceMoltAgent agent,
        SpaceMoltHttpClient client)
    {
        Id = id;
        Label = label;
        Agent = agent;
        Client = client;
    }

    public string Id { get; }
    public string Label { get; }
    public SpaceMoltAgent Agent { get; }
    public SpaceMoltHttpClient Client { get; }
    public GameState? LatestState { get; set; }
    public DateTime LastHaltedSnapshotAt { get; set; } = DateTime.MinValue;
    public List<string> ExecutionStatusLines { get; } = new();
    public Channel<string> ControlInputQueue { get; } = Channel.CreateUnbounded<string>();
    public Channel<string> GenerateScriptQueue { get; } = Channel.CreateUnbounded<string>();
    public Channel<bool> SaveExampleQueue { get; } = Channel.CreateUnbounded<bool>();
    public CancellationTokenSource WorkerCts { get; } = new();
    public Task? WorkerTask { get; set; }
}
