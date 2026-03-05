using System.Collections.Concurrent;
using System.Threading.Channels;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RuntimeSessionStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    service = "Prayer",
    status = "ok",
    utc = DateTime.UtcNow
}));

app.MapGet("/api/runtime/sessions", (RuntimeSessionStore store) =>
    Results.Ok(store.GetAll().Select(SessionSummary.FromSession)));

app.MapPost("/api/runtime/sessions", async (CreateSessionRequest request, RuntimeSessionStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest("username and password are required");

    try
    {
        var session = await store.CreateAsync(request);
        return Results.Created($"/api/runtime/sessions/{session.Id}", SessionSummary.FromSession(session));
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"failed to create session: {ex.Message}");
    }
});

app.MapDelete("/api/runtime/sessions/{id}", (string id, RuntimeSessionStore store) =>
{
    return store.Remove(id)
        ? Results.NoContent()
        : Results.NotFound();
});

app.MapGet("/api/runtime/sessions/{id}", (string id, RuntimeSessionStore store) =>
{
    return store.TryGet(id, out var session)
        ? Results.Ok(SessionSummary.FromSession(session))
        : Results.NotFound();
});

app.MapGet("/api/runtime/sessions/{id}/snapshot", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    var snapshot = session.RuntimeHost.GetSnapshot();
    var state = session.LatestState;

    return Results.Ok(new RuntimeSnapshotResponse(
        SessionId: session.Id,
        Snapshot: snapshot,
        LatestSystem: state?.System,
        LatestPoi: state?.CurrentPOI.Id,
        Fuel: state?.Fuel,
        MaxFuel: state?.MaxFuel,
        Credits: state?.Credits,
        LastUpdatedUtc: session.LastUpdatedUtc));
});

app.MapGet("/api/runtime/sessions/{id}/status", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    return Results.Ok(session.GetStatusLines());
});

app.MapPost("/api/runtime/sessions/{id}/script", (string id, SetScriptRequest request, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TrySetScript(request.Script, out var message))
        return Results.BadRequest(message);

    return Results.Ok(new CommandAckResponse(session.Id, PrayerRuntimeCommandNames.SetScript, message));
});

app.MapPost("/api/runtime/sessions/{id}/script/generate", (string id, GenerateScriptRequest request, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TryGenerateScript(request.Prompt, out var message))
        return Results.BadRequest(message);

    return Results.Ok(new CommandAckResponse(session.Id, PrayerRuntimeCommandNames.GenerateScript, message));
});

app.MapPost("/api/runtime/sessions/{id}/script/execute", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TryExecuteScript(out var message))
        return Results.BadRequest(message);

    return Results.Ok(new CommandAckResponse(session.Id, PrayerRuntimeCommandNames.ExecuteScript, message));
});

app.MapPost("/api/runtime/sessions/{id}/halt", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TryHalt(out var message))
        return Results.BadRequest(message);

    return Results.Ok(new CommandAckResponse(session.Id, PrayerRuntimeCommandNames.Halt, message));
});

app.MapPost("/api/runtime/sessions/{id}/save-example", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TrySaveExample(out var message))
        return Results.BadRequest(message);

    return Results.Ok(new CommandAckResponse(session.Id, PrayerRuntimeCommandNames.SaveExample, message));
});

app.MapPut("/api/runtime/sessions/{id}/loop", (string id, LoopUpdateRequest request, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    session.SetLoopEnabled(request.Enabled);
    return Results.Ok(new LoopUpdateResponse(session.Id, session.LoopEnabled));
});

app.MapPost("/api/runtime/sessions/{id}/commands", (string id, PrayerRuntimeCommandRequest request, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TryApplyCommand(request, out var message))
        return Results.BadRequest(message);

    return Results.Ok(new CommandAckResponse(session.Id, request.Command, message));
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    var store = app.Services.GetRequiredService<RuntimeSessionStore>();
    store.Dispose();
});

app.Run();

internal sealed class RuntimeSessionStore : IDisposable
{
    private readonly ConcurrentDictionary<string, PrayerRuntimeSession> _sessions =
        new(StringComparer.Ordinal);

    public IReadOnlyList<PrayerRuntimeSession> GetAll()
    {
        return _sessions.Values
            .OrderBy(s => s.CreatedUtc)
            .ToList();
    }

    public async Task<PrayerRuntimeSession> CreateAsync(CreateSessionRequest request)
    {
        string label = string.IsNullOrWhiteSpace(request.Label)
            ? request.Username.Trim()
            : request.Label.Trim();

        var client = new SpaceMoltHttpClient
        {
            DebugContext = label
        };

        try
        {
            await client.LoginAsync(request.Username.Trim(), request.Password);

            var transport = new SpaceMoltRuntimeTransportAdapter(client);
            var stateProvider = new SpaceMoltRuntimeStateProvider(client);

            var llamaCppBaseUrl = Environment.GetEnvironmentVariable("LLAMACPP_BASE_URL")
                ?? "http://localhost:8080";
            var llamaCppModel = Environment.GetEnvironmentVariable("LLAMACPP_MODEL")
                ?? "model";
            ILLMClient planner = new LlamaCppClient(llamaCppBaseUrl, llamaCppModel);

            var agent = new SpaceMoltAgent(planner, planner, scriptExampleRag: null, saveCheckpoint: null);
            agent.Halt("Awaiting script input");

            var now = DateTime.UtcNow;
            var session = new PrayerRuntimeSession(
                id: Guid.NewGuid().ToString("N"),
                label: label,
                createdUtc: now,
                agent: agent,
                client: client,
                runtimeTransport: transport,
                runtimeStateProvider: stateProvider);

            _sessions[session.Id] = session;
            return session;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public bool TryGet(string id, out PrayerRuntimeSession session)
    {
        return _sessions.TryGetValue(id, out session!);
    }

    public bool Remove(string id)
    {
        if (!_sessions.TryRemove(id, out var session))
            return false;

        session.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (var key in _sessions.Keys.ToList())
            Remove(key);
    }
}

internal sealed class PrayerRuntimeSession : IDisposable
{
    private readonly object _stateLock = new();
    private readonly List<string> _executionStatus = new();

    public PrayerRuntimeSession(
        string id,
        string label,
        DateTime createdUtc,
        SpaceMoltAgent agent,
        SpaceMoltHttpClient client,
        IRuntimeTransport runtimeTransport,
        IRuntimeStateProvider runtimeStateProvider)
    {
        Id = id;
        Label = label;
        CreatedUtc = createdUtc;

        Agent = agent;
        Client = client;
        RuntimeTransport = runtimeTransport;
        RuntimeStateProvider = runtimeStateProvider;

        RuntimeHost = new RuntimeHost(
            label,
            agent,
            runtimeTransport,
            runtimeStateProvider,
            ControlInputQueue.Reader,
            GenerateScriptQueue.Reader,
            SaveExampleQueue.Reader,
            HaltNowQueue.Reader,
            () => LoopEnabled,
            () => LatestState,
            state =>
            {
                LatestState = state;
                LastUpdatedUtc = DateTime.UtcNow;
            },
            () => LastHaltedSnapshotAt,
            value => LastHaltedSnapshotAt = value,
            state =>
            {
                LatestState = state;
                LastUpdatedUtc = DateTime.UtcNow;
            },
            AppendStatus,
            _ => { },
            reason =>
            {
                LoopEnabled = false;
                AppendStatus($"[{Label}] Global stop requested: {reason}");
            },
            PrayerDefaults.ScriptGenerationMaxAttempts);

        WorkerTask = Task.Run(() => RuntimeHost.RunAsync(WorkerCts.Token), WorkerCts.Token);
    }

    public string Id { get; }
    public string Label { get; }
    public DateTime CreatedUtc { get; }
    public DateTime LastUpdatedUtc { get; private set; }

    public SpaceMoltAgent Agent { get; }
    public SpaceMoltHttpClient Client { get; }
    public IRuntimeTransport RuntimeTransport { get; }
    public IRuntimeStateProvider RuntimeStateProvider { get; }
    public IRuntimeHost RuntimeHost { get; }

    public bool LoopEnabled { get; private set; }
    public GameState? LatestState { get; private set; }
    public DateTime LastHaltedSnapshotAt { get; private set; } = DateTime.MinValue;

    public Channel<string> ControlInputQueue { get; } = Channel.CreateUnbounded<string>();
    public Channel<string> GenerateScriptQueue { get; } = Channel.CreateUnbounded<string>();
    public Channel<bool> SaveExampleQueue { get; } = Channel.CreateUnbounded<bool>();
    public Channel<bool> HaltNowQueue { get; } = Channel.CreateUnbounded<bool>();
    public CancellationTokenSource WorkerCts { get; } = new();
    public Task WorkerTask { get; }

    public IReadOnlyList<string> GetStatusLines()
    {
        lock (_stateLock)
            return _executionStatus.ToList();
    }

    public bool TryApplyCommand(PrayerRuntimeCommandRequest request, out string message)
    {
        var command = Normalize(request.Command);
        var argument = request.Argument ?? string.Empty;
        return TryApplyCommand(command, argument, out message);
    }

    public bool TrySetScript(string script, out string message)
    {
        return TryApplyCommand(PrayerRuntimeCommandNames.SetScript, script ?? string.Empty, out message);
    }

    public bool TryGenerateScript(string prompt, out string message)
    {
        return TryApplyCommand(PrayerRuntimeCommandNames.GenerateScript, prompt ?? string.Empty, out message);
    }

    public bool TryExecuteScript(out string message)
    {
        return TryApplyCommand(PrayerRuntimeCommandNames.ExecuteScript, string.Empty, out message);
    }

    public bool TryHalt(out string message)
    {
        return TryApplyCommand(PrayerRuntimeCommandNames.Halt, string.Empty, out message);
    }

    public bool TrySaveExample(out string message)
    {
        return TryApplyCommand(PrayerRuntimeCommandNames.SaveExample, string.Empty, out message);
    }

    public void SetLoopEnabled(bool enabled)
    {
        LoopEnabled = enabled;
        AppendStatus($"[{Label}] Loop {(enabled ? "enabled" : "disabled")}");
    }

    private bool TryApplyCommand(string command, string argument, out string message)
    {
        switch (command)
        {
            case PrayerRuntimeCommandNames.SetScript:
            {
                if (string.IsNullOrWhiteSpace(argument))
                {
                    message = "script cannot be empty";
                    return false;
                }

                ControlInputQueue.Writer.TryWrite(argument);
                message = "script queued";
                return true;
            }
            case PrayerRuntimeCommandNames.GenerateScript:
            {
                if (string.IsNullOrWhiteSpace(argument))
                {
                    message = "prompt cannot be empty";
                    return false;
                }

                GenerateScriptQueue.Writer.TryWrite(argument);
                message = "generation queued";
                return true;
            }
            case PrayerRuntimeCommandNames.ExecuteScript:
            {
                var script = Agent.CurrentControlInput;
                if (string.IsNullOrWhiteSpace(script))
                {
                    message = "no script loaded";
                    return false;
                }

                ControlInputQueue.Writer.TryWrite(script);
                message = "script execution restarted";
                return true;
            }
            case PrayerRuntimeCommandNames.Halt:
                HaltNowQueue.Writer.TryWrite(true);
                message = "halt requested";
                return true;
            case PrayerRuntimeCommandNames.SaveExample:
                SaveExampleQueue.Writer.TryWrite(true);
                message = "save example requested";
                return true;
            case "loop_on":
                LoopEnabled = true;
                message = "loop enabled";
                return true;
            case "loop_off":
                LoopEnabled = false;
                message = "loop disabled";
                return true;
            default:
                message = $"unknown command: {command}";
                return false;
        }
    }

    public void Dispose()
    {
        WorkerCts.Cancel();
        Client.Dispose();
    }

    private void AppendStatus(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        lock (_stateLock)
        {
            _executionStatus.Add(line);
            while (_executionStatus.Count > PrayerDefaults.ExecutionStatusHistoryLimit)
                _executionStatus.RemoveAt(0);
        }

        LastUpdatedUtc = DateTime.UtcNow;
    }

    private static string Normalize(string? command)
    {
        return (command ?? string.Empty).Trim().ToLowerInvariant();
    }
}

public sealed record CreateSessionRequest(string Username, string Password, string? Label = null);

public sealed record PrayerRuntimeCommandRequest(string Command, string? Argument = null);
public sealed record SetScriptRequest(string Script);
public sealed record GenerateScriptRequest(string Prompt);
public sealed record LoopUpdateRequest(bool Enabled);

public sealed record CommandAckResponse(string SessionId, string Command, string Message);
public sealed record LoopUpdateResponse(string SessionId, bool Enabled);

public sealed record RuntimeSnapshotResponse(
    string SessionId,
    RuntimeHostSnapshot Snapshot,
    string? LatestSystem,
    string? LatestPoi,
    int? Fuel,
    int? MaxFuel,
    int? Credits,
    DateTime LastUpdatedUtc);

public sealed record SessionSummary(
    string Id,
    string Label,
    DateTime CreatedUtc,
    DateTime LastUpdatedUtc,
    bool LoopEnabled,
    bool IsHalted,
    bool HasActiveCommand,
    int? CurrentScriptLine)
{
    internal static SessionSummary FromSession(PrayerRuntimeSession session)
    {
        var snapshot = session.RuntimeHost.GetSnapshot();
        return new SessionSummary(
            session.Id,
            session.Label,
            session.CreatedUtc,
            session.LastUpdatedUtc,
            session.LoopEnabled,
            snapshot.IsHalted,
            snapshot.HasActiveCommand,
            snapshot.CurrentScriptLine);
    }
}

internal static class PrayerDefaults
{
    public const int ScriptGenerationMaxAttempts = 3;
    public const int ExecutionStatusHistoryLimit = 200;
}

internal static class PrayerRuntimeCommandNames
{
    public const string SetScript = "set_script";
    public const string GenerateScript = "generate_script";
    public const string ExecuteScript = "execute_script";
    public const string Halt = "halt";
    public const string SaveExample = "save_example";
}
