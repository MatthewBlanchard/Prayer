using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Contracts = Prayer.Contracts;

AppPaths.EnsureDirectories();

var sink = new ChannelLogSink();
LogSink.SetInstance(sink);
sink.Start();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
});
builder.Services.AddSingleton<PrayerLlmRegistry>();
builder.Services.AddSingleton<RuntimeSessionStore>();
builder.Services.AddSingleton<PrayerPreferenceStore>();

var app = builder.Build();
var logger = app.Logger;

app.Use(async (context, next) =>
{
    var started = Stopwatch.StartNew();
    try
    {
        await next();
    }
    finally
    {
        started.Stop();
        var elapsedMs = started.Elapsed.TotalMilliseconds;
        var path = context.Request.Path.Value ?? "/";
        var method = context.Request.Method;
        var status = context.Response.StatusCode;
        PrayerTelemetry.RecordHttpRequest(method, path, status, elapsedMs);

        if (!path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            if (status >= 500 || elapsedMs >= PrayerDefaults.SlowRequestThresholdMs)
            {
                logger.LogWarning(
                    "HTTP {Method} {Path} -> {StatusCode} in {ElapsedMs} ms",
                    method,
                    path,
                    status,
                    elapsedMs);
            }
            else
            {
                logger.LogDebug(
                    "HTTP {Method} {Path} -> {StatusCode} in {ElapsedMs} ms",
                    method,
                    path,
                    status,
                    elapsedMs);
            }
        }
    }
});

app.MapGet("/health", () => Results.Ok(new
{
    service = "Prayer",
    status = "ok",
    utc = DateTime.UtcNow
}));

app.MapGet("/api/llm/catalog", async (PrayerLlmRegistry registry) =>
    Results.Ok(await registry.BuildCatalogAsync()));

app.MapGet("/api/preferences/bots", (PrayerPreferenceStore store) =>
    Results.Ok(new Contracts.BotProfilesResponse(store.LoadBots())));

app.MapPut("/api/preferences/bots", (Contracts.UpsertBotProfileRequest request, PrayerPreferenceStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest("username and password are required");

    store.UpsertBot(request.Username, request.Password);
    logger.LogInformation("Saved bot profile for username {Username}", request.Username.Trim());
    return Results.NoContent();
});

app.MapGet("/api/preferences/llm", (PrayerPreferenceStore store) =>
{
    var preference = store.LoadDefaultLlmPreference();
    return Results.Ok(new Contracts.DefaultLlmPreferenceResponse(preference?.Provider, preference?.Model));
});

app.MapPut("/api/preferences/llm", (Contracts.UpdateDefaultLlmPreferenceRequest request, PrayerPreferenceStore store, PrayerLlmRegistry registry) =>
{
    var provider = registry.NormalizeProvider(request.Provider);
    var model = registry.ResolveModel(provider, request.Model);
    store.SaveDefaultLlmPreference(provider, model);
    logger.LogInformation("Saved default LLM preference {Provider}/{Model}", provider, model);
    return Results.NoContent();
});

app.MapGet("/api/runtime/sessions", (RuntimeSessionStore store) =>
    Results.Ok(store.GetAll().Select(ToSessionSummary)));

app.MapPost("/api/runtime/sessions", async (Contracts.CreateSessionRequest request, RuntimeSessionStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest("username and password are required");

    try
    {
        var session = await store.CreateAsync(request);
        return Results.Created($"/api/runtime/sessions/{session.Id}", ToSessionSummary(session));
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"failed to create session: {ex.Message}");
    }
});

app.MapPost("/api/runtime/sessions/register", async (Contracts.RegisterSessionRequest request, RuntimeSessionStore store) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) ||
        string.IsNullOrWhiteSpace(request.Empire) ||
        string.IsNullOrWhiteSpace(request.RegistrationCode))
    {
        return Results.BadRequest("username, empire, and registrationCode are required");
    }

    try
    {
        var (session, password) = await store.RegisterAsync(request);
        return Results.Created(
            $"/api/runtime/sessions/{session.Id}",
            new Contracts.RegisterSessionResponse(session.Id, password));
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"failed to register session: {ex.Message}");
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
        ? Results.Ok(ToSessionSummary(session))
        : Results.NotFound();
});

app.MapGet("/api/runtime/sessions/{id}/llm", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    return Results.Ok(new Contracts.SessionLlmConfigResponse(
        session.CurrentLlmProvider,
        session.CurrentLlmModel));
});

app.MapPut("/api/runtime/sessions/{id}/llm", (string id, Contracts.UpdateSessionLlmRequest request, RuntimeSessionStore store, PrayerLlmRegistry registry) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TrySetLlm(request.Provider, request.Model, registry, out var message))
        return Results.BadRequest(message);

    return Results.Ok(new Contracts.CommandAckResponse(session.Id, "set_llm", message));
});

app.MapGet("/api/runtime/sessions/{id}/snapshot", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    var snapshot = session.RuntimeHost.GetSnapshot();
    var state = session.LatestState;

    return Results.Ok(new Contracts.RuntimeSnapshotResponse(
        SessionId: session.Id,
        Snapshot: new Contracts.RuntimeHostSnapshotDto(
            snapshot.IsHalted,
            snapshot.HasActiveCommand,
            snapshot.CurrentScriptLine,
            snapshot.CurrentScript),
        LatestSystem: state?.System,
        LatestPoi: state?.CurrentPOI.Id,
        Fuel: state?.Ship.Fuel,
        MaxFuel: state?.Ship.MaxFuel,
        Credits: state?.Credits,
        LastUpdatedUtc: session.LastUpdatedUtc));
});

app.MapGet("/api/runtime/sessions/{id}/status", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    return Results.Ok(session.GetStatusLines());
});

app.MapGet("/api/runtime/sessions/{id}/spacemolt/stats", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    return Results.Ok(session.Client.GetApiStatsSnapshot());
});

app.MapGet("/api/runtime/sessions/{id}/state", async (string id, HttpContext http, RuntimeSessionStore store, CancellationToken cancellationToken) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    long since = 0;
    if (http.Request.Query.TryGetValue("since", out var sinceValues))
        _ = long.TryParse(sinceValues.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out since);

    int waitMs = 0;
    if (http.Request.Query.TryGetValue("wait_ms", out var waitValues))
        _ = int.TryParse(waitValues.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out waitMs);

    if (waitMs > 0)
    {
        waitMs = Math.Clamp(waitMs, 0, PrayerDefaults.MaxStateLongPollWaitMs);
        bool changed = await session.WaitForStateChangeAsync(since, waitMs, cancellationToken);
        if (!changed)
            return Results.NoContent();
    }

    http.Response.Headers["X-Prayer-State-Version"] = session.StateVersion.ToString(CultureInfo.InvariantCulture);
    return Results.Ok(session.BuildRuntimeStateSnapshot());
});

app.MapPost("/api/runtime/sessions/{id}/script", (string id, Contracts.SetScriptRequest request, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TrySetScript(request.Script, out var message))
        return Results.BadRequest(message);

    return Results.Ok(new Contracts.CommandAckResponse(session.Id, PrayerRuntimeCommandNames.SetScript, message));
});

app.MapPost("/api/runtime/sessions/{id}/script/generate", async (string id, Contracts.GenerateScriptRequest request, RuntimeSessionStore store, CancellationToken cancellationToken) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    try
    {
        var generatedScript = await session.GenerateScriptAsync(request.Prompt, cancellationToken);
        if (string.IsNullOrWhiteSpace(generatedScript))
            return Results.BadRequest("prompt did not produce a script");

        return Results.Ok(new Contracts.GenerateScriptResponse(generatedScript));
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Script generation failed for session {SessionId}", id);
        return Results.BadRequest($"script generation failed: {ex.Message}");
    }
});

app.MapPost("/api/runtime/sessions/{id}/script/execute", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TryExecuteScript(out var message))
        return Results.BadRequest(message);

    return Results.Ok(new Contracts.CommandAckResponse(session.Id, PrayerRuntimeCommandNames.ExecuteScript, message));
});

app.MapPost("/api/runtime/sessions/{id}/halt", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TryHalt(out var message))
        return Results.BadRequest(message);

    return Results.Ok(new Contracts.CommandAckResponse(session.Id, PrayerRuntimeCommandNames.Halt, message));
});

app.MapPost("/api/runtime/sessions/{id}/save-example", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TrySaveExample(out var message))
        return Results.BadRequest(message);

    return Results.Ok(new Contracts.CommandAckResponse(session.Id, PrayerRuntimeCommandNames.SaveExample, message));
});

app.MapPost("/api/runtime/sessions/{id}/commands", (string id, Contracts.RuntimeCommandRequest request, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    if (!session.TryApplyCommand(request, out var message))
        return Results.BadRequest(message);

    return Results.Ok(new Contracts.CommandAckResponse(session.Id, request.Command, message));
});

app.MapPost("/api/runtime/sessions/{id}/spacemolt/passthrough",
    async (string id, Contracts.SpaceMoltPassthroughRequest request, RuntimeSessionStore store, CancellationToken cancellationToken) =>
    {
        if (!store.TryGet(id, out var session))
            return Results.NotFound();

        if (string.IsNullOrWhiteSpace(request.Command))
            return Results.BadRequest("command is required");

        try
        {
            var (succeeded, result, error) = await session
                .ExecuteSpaceMoltCommandAsync(request.Command, request.Payload, cancellationToken);

            return Results.Ok(new Contracts.SpaceMoltPassthroughResponse(succeeded, result, error));
        }
        catch (RateLimitStopException)
        {
            return Results.StatusCode(429);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Passthrough {Command} failed for session {SessionId}", request.Command, id);
            return Results.BadRequest($"command failed: {ex.Message}");
        }
    });

app.Lifetime.ApplicationStopping.Register(() =>
{
    var store = app.Services.GetRequiredService<RuntimeSessionStore>();
    store.Dispose();
    sink.DrainAndStopAsync().GetAwaiter().GetResult();
});

static Contracts.SessionSummary ToSessionSummary(PrayerRuntimeSession session)
{
    var snapshot = session.RuntimeHost.GetSnapshot();
    return new Contracts.SessionSummary(
        session.Id,
        session.Label,
        session.CreatedUtc,
        session.LastUpdatedUtc,
        snapshot.IsHalted,
        snapshot.HasActiveCommand,
        snapshot.CurrentScriptLine);
}

app.Run();

internal sealed class RuntimeSessionStore : IDisposable
{
    private readonly PrayerLlmRegistry _llmRegistry;
    private readonly ILogger<RuntimeSessionStore> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, PrayerRuntimeSession> _sessions =
        new(StringComparer.Ordinal);

    public RuntimeSessionStore(
        PrayerLlmRegistry llmRegistry,
        ILogger<RuntimeSessionStore> logger,
        ILoggerFactory loggerFactory)
    {
        _llmRegistry = llmRegistry;
        _logger = logger;
        _loggerFactory = loggerFactory;
    }

    public IReadOnlyList<PrayerRuntimeSession> GetAll()
    {
        return _sessions.Values
            .OrderBy(s => s.CreatedUtc)
            .ToList();
    }

    public async Task<PrayerRuntimeSession> CreateAsync(Contracts.CreateSessionRequest request)
    {
        string label = ResolveLabel(request.Username, request.Label);
        var client = BuildClient(label);
        var started = Stopwatch.StartNew();
        _logger.LogInformation("Creating runtime session for label {Label}", label);
        try
        {
            await client.LoginAsync(request.Username.Trim(), request.Password);
            var session = CreateSessionFromAuthenticatedClient(label, client);
            started.Stop();
            PrayerTelemetry.RecordSessionProvision("create", true, started.Elapsed.TotalMilliseconds);
            _logger.LogInformation(
                "Created runtime session {SessionId} for label {Label} in {ElapsedMs} ms",
                session.Id,
                label,
                started.Elapsed.TotalMilliseconds);
            return session;
        }
        catch (Exception ex)
        {
            started.Stop();
            PrayerTelemetry.RecordSessionProvision("create", false, started.Elapsed.TotalMilliseconds);
            _logger.LogWarning(
                ex,
                "Failed to create runtime session for label {Label} after {ElapsedMs} ms",
                label,
                started.Elapsed.TotalMilliseconds);
            client.Dispose();
            throw;
        }
    }

    public async Task<(PrayerRuntimeSession Session, string Password)> RegisterAsync(Contracts.RegisterSessionRequest request)
    {
        string label = ResolveLabel(request.Username, request.Label);
        var client = BuildClient(label);
        var started = Stopwatch.StartNew();
        _logger.LogInformation("Registering runtime session for label {Label}", label);
        try
        {
            var password = await client.RegisterAsync(
                request.Username.Trim(),
                request.Empire.Trim().ToLowerInvariant(),
                request.RegistrationCode.Trim());

            var session = CreateSessionFromAuthenticatedClient(label, client);
            started.Stop();
            PrayerTelemetry.RecordSessionProvision("register", true, started.Elapsed.TotalMilliseconds);
            _logger.LogInformation(
                "Registered runtime session {SessionId} for label {Label} in {ElapsedMs} ms",
                session.Id,
                label,
                started.Elapsed.TotalMilliseconds);
            return (session, password);
        }
        catch (Exception ex)
        {
            started.Stop();
            PrayerTelemetry.RecordSessionProvision("register", false, started.Elapsed.TotalMilliseconds);
            _logger.LogWarning(
                ex,
                "Failed to register runtime session for label {Label} after {ElapsedMs} ms",
                label,
                started.Elapsed.TotalMilliseconds);
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
        _logger.LogInformation("Removed runtime session {SessionId}", id);
        return true;
    }

    public void Dispose()
    {
        foreach (var key in _sessions.Keys.ToList())
            Remove(key);
    }

    private PrayerRuntimeSession CreateSessionFromAuthenticatedClient(string label, SpaceMoltHttpClient client)
    {
        var transport = new SpaceMoltRuntimeTransportAdapter(client);
        var stateProvider = new SpaceMoltRuntimeStateProvider(client);
        var (provider, model) = _llmRegistry.ResolveInitialSelection();
        var planner = new SwappableLlmClient(_llmRegistry.CreateClient(provider, model));
        var agent = new SpaceMoltAgent(planner, planner, scriptExampleRag: null, saveCheckpoint: null);
        agent.Halt("Awaiting script input");

        var now = DateTime.UtcNow;
        var session = new PrayerRuntimeSession(
            id: Guid.NewGuid().ToString("N"),
            label: label,
            createdUtc: now,
            agent: agent,
            client: client,
            plannerLlm: planner,
            llmProvider: provider,
            llmModel: model,
            runtimeTransport: transport,
            runtimeStateProvider: stateProvider,
            logger: _loggerFactory.CreateLogger<PrayerRuntimeSession>());

        _sessions[session.Id] = session;
        return session;
    }

    private static string ResolveLabel(string username, string? label)
    {
        return string.IsNullOrWhiteSpace(label)
            ? username.Trim()
            : label.Trim();
    }

    private static SpaceMoltHttpClient BuildClient(string label)
    {
        return new SpaceMoltHttpClient
        {
            DebugContext = label
        };
    }
}

internal sealed class PrayerRuntimeSession : IDisposable
{
    private readonly object _stateLock = new();
    private readonly List<string> _executionStatus = new();
    private readonly ILogger<PrayerRuntimeSession> _logger;
    private string? _latestRequestedScript;
    private long _stateVersion = 1;
    private TaskCompletionSource<long> _stateChanged = NewStateChangedSignal();

    public PrayerRuntimeSession(
        string id,
        string label,
        DateTime createdUtc,
        SpaceMoltAgent agent,
        SpaceMoltHttpClient client,
        SwappableLlmClient plannerLlm,
        string llmProvider,
        string llmModel,
        IRuntimeTransport runtimeTransport,
        IRuntimeStateProvider runtimeStateProvider,
        ILogger<PrayerRuntimeSession> logger)
    {
        Id = id;
        Label = label;
        CreatedUtc = createdUtc;
        _logger = logger;

        Agent = agent;
        Client = client;
        PlannerLlm = plannerLlm;
        CurrentLlmProvider = llmProvider;
        CurrentLlmModel = llmModel;
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
            () => LatestState,
            UpdateLatestState,
            () => LastHaltedSnapshotAt,
            value => LastHaltedSnapshotAt = value,
            UpdateLatestState,
            AppendStatus,
            line =>
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _logger.LogWarning("RuntimeHost: {Line}", line);
            },
            reason =>
            {
                AppendStatus($"[{Label}] Global stop requested: {reason}");
            },
            PrayerDefaults.ScriptGenerationMaxAttempts);

        WorkerTask = Task.Run(async () =>
        {
            var started = Stopwatch.StartNew();
            _logger.LogInformation("Runtime worker started for session {SessionId} ({Label})", Id, Label);
            try
            {
                await RuntimeHost.RunAsync(WorkerCts.Token);
            }
            catch (OperationCanceledException) when (WorkerCts.IsCancellationRequested)
            {
                // Normal shutdown path.
            }
            catch (Exception ex)
            {
                PrayerTelemetry.RecordRuntimeWorkerFault();
                _logger.LogError(ex, "Runtime worker faulted for session {SessionId} ({Label})", Id, Label);
                throw;
            }
            finally
            {
                started.Stop();
                PrayerTelemetry.RecordRuntimeWorkerLifetime(started.Elapsed.TotalSeconds);
                _logger.LogInformation(
                    "Runtime worker stopped for session {SessionId} ({Label}) after {ElapsedSeconds:F1}s",
                    Id,
                    Label,
                    started.Elapsed.TotalSeconds);
            }
        }, WorkerCts.Token);
    }

    public string Id { get; }
    public string Label { get; }
    public DateTime CreatedUtc { get; }
    public DateTime LastUpdatedUtc { get; private set; }

    public SpaceMoltAgent Agent { get; }
    public SpaceMoltHttpClient Client { get; }
    public SwappableLlmClient PlannerLlm { get; }
    public string CurrentLlmProvider { get; private set; }
    public string CurrentLlmModel { get; private set; }
    public IRuntimeTransport RuntimeTransport { get; }
    public IRuntimeStateProvider RuntimeStateProvider { get; }
    public IRuntimeHost RuntimeHost { get; }

    public GameState? LatestState { get; private set; }
    public DateTime LastHaltedSnapshotAt { get; private set; } = DateTime.MinValue;
    public long StateVersion => Interlocked.Read(ref _stateVersion);

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

    public Contracts.RuntimeStateResponse BuildRuntimeStateSnapshot()
    {
        Contracts.RuntimeGameStateDto? state = LatestState == null
            ? null
            : RuntimeStateContractMapper.Map(LatestState);
        var telemetry = Client.GetRuntimeTelemetrySnapshot();

        return new Contracts.RuntimeStateResponse(
            state,
            Agent.GetMemoryList(),
            GetStatusLines(),
            Agent.CurrentControlInput,
            Agent.CurrentScriptLine,
            Agent.LastScriptGenerationPrompt,
            telemetry.CurrentTick > 0 ? telemetry.CurrentTick : null,
            telemetry.LastMutationPostUtc);
    }

    public async Task<bool> WaitForStateChangeAsync(long sinceVersion, int waitMs, CancellationToken cancellationToken)
    {
        if (StateVersion > sinceVersion)
            return true;

        var signal = Volatile.Read(ref _stateChanged);
        if (StateVersion > sinceVersion)
            return true;

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitCts.CancelAfter(waitMs);

        try
        {
            await signal.Task.WaitAsync(waitCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Timeout/cancel path.
        }

        return StateVersion > sinceVersion;
    }

    public bool TryApplyCommand(Contracts.RuntimeCommandRequest request, out string message)
    {
        var command = Normalize(request.Command);
        var argument = request.Argument ?? string.Empty;
        return TryApplyCommand(command, argument, out message);
    }

    public bool TrySetScript(string script, out string message)
    {
        return TryApplyCommand(PrayerRuntimeCommandNames.SetScript, script ?? string.Empty, out message);
    }

    public Task<string?> GenerateScriptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return Task.FromResult<string?>(null);

        return RuntimeHost.GenerateScriptAsync(prompt);
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

    public async Task<(bool Succeeded, JsonElement Result, string? Error)> ExecuteSpaceMoltCommandAsync(
        string command, JsonElement? payload, CancellationToken ct)
    {
        object? payloadObj = payload is { } p &&
                             p.ValueKind != JsonValueKind.Undefined &&
                             p.ValueKind != JsonValueKind.Null
            ? (object?)p
            : null;

        var result = await Client.ExecuteAsync(command, payloadObj, ct);
        bool failed = SpaceMoltApiTransport.TryExtractApiError(result, out _, out var error, out _);
        return (!failed, result, failed ? error : null);
    }

    public bool TrySetLlm(string provider, string model, PrayerLlmRegistry registry, out string message)
    {
        try
        {
            var normalizedProvider = registry.NormalizeProvider(provider);
            var normalizedModel = registry.ResolveModel(normalizedProvider, model);
            var updatedClient = registry.CreateClient(normalizedProvider, normalizedModel);
            PlannerLlm.SetInner(updatedClient);
            CurrentLlmProvider = normalizedProvider;
            CurrentLlmModel = normalizedModel;
            AppendStatus($"[{Label}] LLM set to {CurrentLlmProvider}/{CurrentLlmModel}");
            message = $"llm set to {CurrentLlmProvider}/{CurrentLlmModel}";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private bool TryApplyCommand(string command, string argument, out string message)
    {
        var started = Stopwatch.StartNew();
        bool success;
        string responseMessage;

        switch (command)
        {
            case PrayerRuntimeCommandNames.SetScript:
            {
                if (string.IsNullOrWhiteSpace(argument))
                    (success, responseMessage) = (false, "script cannot be empty");
                else
                {
                    RuntimeHost.RequestHaltNow();
                    DrainPendingControlInputs();
                    _latestRequestedScript = argument;
                    ControlInputQueue.Writer.TryWrite(argument);
                    AppendStatus($"[{Label}] Script update requested");
                    (success, responseMessage) = (true, "script queued");
                }

                break;
            }
            case PrayerRuntimeCommandNames.GenerateScript:
                (success, responseMessage) = (false, "generate_script command is no longer supported; use /script/generate");
                break;
            case PrayerRuntimeCommandNames.ExecuteScript:
            {
                var script = !string.IsNullOrWhiteSpace(_latestRequestedScript)
                    ? _latestRequestedScript
                    : Agent.CurrentControlInput;
                if (string.IsNullOrWhiteSpace(script))
                    (success, responseMessage) = (false, "no script loaded");
                else
                {
                    HaltNowQueue.Writer.TryWrite(true);
                    RuntimeHost.RequestHaltNow();
                    DrainPendingControlInputs();
                    ControlInputQueue.Writer.TryWrite(script);
                    AppendStatus($"[{Label}] Script restart requested");
                    (success, responseMessage) = (true, "script execution restarted");
                }

                break;
            }
            case PrayerRuntimeCommandNames.Halt:
                HaltNowQueue.Writer.TryWrite(true);
                RuntimeHost.RequestHaltNow();
                AppendStatus($"[{Label}] Halt requested");
                (success, responseMessage) = (true, "halt requested");
                break;
            case PrayerRuntimeCommandNames.SaveExample:
                SaveExampleQueue.Writer.TryWrite(true);
                (success, responseMessage) = (true, "save example requested");
                break;
            default:
                (success, responseMessage) = (false, $"unknown command: {command}");
                break;
        }

        started.Stop();
        message = responseMessage;
        PrayerTelemetry.RecordRuntimeCommand(command, success, started.Elapsed.TotalMilliseconds);

        var controlQueueDepth = ControlInputQueue.Reader.CanCount ? ControlInputQueue.Reader.Count : -1;
        var generateQueueDepth = GenerateScriptQueue.Reader.CanCount ? GenerateScriptQueue.Reader.Count : -1;
        var haltQueueDepth = HaltNowQueue.Reader.CanCount ? HaltNowQueue.Reader.Count : -1;

        if (success)
        {
            _logger.LogInformation(
                "Session {SessionId} command {Command} accepted in {ElapsedMs} ms (control={ControlDepth}, generate={GenerateDepth}, halt={HaltDepth})",
                Id,
                command,
                started.Elapsed.TotalMilliseconds,
                controlQueueDepth,
                generateQueueDepth,
                haltQueueDepth);
        }
        else
        {
            _logger.LogWarning(
                "Session {SessionId} command {Command} rejected in {ElapsedMs} ms: {Message}",
                Id,
                command,
                started.Elapsed.TotalMilliseconds,
                responseMessage);
        }

        return success;
    }

    public void Dispose()
    {
        _logger.LogInformation("Disposing runtime session {SessionId} ({Label})", Id, Label);
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

        MarkStateChanged();
    }

    private void DrainPendingControlInputs()
    {
        while (ControlInputQueue.Reader.TryRead(out _))
        {
            // Drop stale queued control inputs so the newest script wins.
        }
    }

    private void UpdateLatestState(GameState state)
    {
        LatestState = state;
        MarkStateChanged();
    }

    private void MarkStateChanged()
    {
        LastUpdatedUtc = DateTime.UtcNow;
        var nextVersion = Interlocked.Increment(ref _stateVersion);
        var previous = Interlocked.Exchange(ref _stateChanged, NewStateChangedSignal());
        previous.TrySetResult(nextVersion);
    }

    private static string Normalize(string? command)
    {
        return (command ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static TaskCompletionSource<long> NewStateChangedSignal()
    {
        return new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal static class PrayerDefaults
{
    public const int ScriptGenerationMaxAttempts = 3;
    public const int ExecutionStatusHistoryLimit = 200;
    public const double SlowRequestThresholdMs = 500;
    public const int MaxStateLongPollWaitMs = 30000;
}

internal static class PrayerRuntimeCommandNames
{
    public const string SetScript = "set_script";
    public const string GenerateScript = "generate_script";
    public const string ExecuteScript = "execute_script";
    public const string Halt = "halt";
    public const string SaveExample = "save_example";
}

internal sealed class PrayerLlmRegistry
{
    private readonly Dictionary<string, ILLMProvider> _providersById =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _defaultProvider;
    private readonly string _defaultModel;

    public PrayerLlmRegistry()
    {
        var llamaCppBaseUrl = Environment.GetEnvironmentVariable("LLAMACPP_BASE_URL")
            ?? "http://localhost:8080";
        var llamaCppModel = Environment.GetEnvironmentVariable("LLAMACPP_MODEL")
            ?? "model";
        _providersById["llamacpp"] = new LlamaCppProvider(llamaCppBaseUrl, llamaCppModel);

        var openAiApiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var groqApiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        var openAiDefaultModel = Environment.GetEnvironmentVariable("OPENAI_MODEL")
            ?? "gpt-4o-mini";
        var anthropicDefaultModel = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL")
            ?? "claude-3-5-sonnet-latest";
        var groqDefaultModel = Environment.GetEnvironmentVariable("GROQ_MODEL")
            ?? "llama-3.3-70b-versatile";

        if (!string.IsNullOrWhiteSpace(openAiApiKey))
            _providersById["openai"] = new OpenAIProvider(openAiApiKey, openAiDefaultModel);
        if (!string.IsNullOrWhiteSpace(anthropicApiKey))
            _providersById["anthropic"] = new AnthropicProvider(anthropicApiKey, anthropicDefaultModel);
        if (!string.IsNullOrWhiteSpace(groqApiKey))
            _providersById["groq"] = new GroqProvider(groqApiKey, groqDefaultModel);

        var requestedProvider = NormalizeProvider(Environment.GetEnvironmentVariable("LLM_PROVIDER"));
        _defaultProvider = _providersById.ContainsKey(requestedProvider)
            ? requestedProvider
            : _providersById.ContainsKey("openai")
                ? "openai"
                : _providersById.ContainsKey("anthropic")
                    ? "anthropic"
                : _providersById.ContainsKey("groq")
                    ? "groq"
                    : "llamacpp";

        var envModel = Environment.GetEnvironmentVariable("LLM_MODEL");
        _defaultModel = ResolveModel(_defaultProvider, envModel);
    }

    public string NormalizeProvider(string? provider)
    {
        var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "openai" => "openai",
            "anthropic" => "anthropic",
            "groq" => "groq",
            "llamacpp" => "llamacpp",
            _ => "llamacpp"
        };
    }

    public string ResolveModel(string provider, string? model)
    {
        if (!_providersById.TryGetValue(provider, out var llmProvider))
            throw new InvalidOperationException($"Provider '{provider}' is not configured.");

        return string.IsNullOrWhiteSpace(model)
            ? llmProvider.DefaultModel
            : model.Trim();
    }

    public (string Provider, string Model) ResolveInitialSelection()
    {
        return (_defaultProvider, _defaultModel);
    }

    public ILLMClient CreateClient(string provider, string model)
    {
        if (!_providersById.TryGetValue(provider, out var llmProvider))
            throw new InvalidOperationException($"Provider '{provider}' is not configured.");

        return llmProvider.CreateClient(model);
    }

    public async Task<Contracts.LlmCatalogResponse> BuildCatalogAsync()
    {
        var entries = new List<Contracts.LlmProviderCatalogEntry>();
        foreach (var provider in _providersById.Values)
        {
            var models = new List<string> { provider.DefaultModel };
            try
            {
                var discovered = await provider.ListModelsAsync();
                if (discovered.Count > 0)
                    models = discovered.ToList();
            }
            catch
            {
                // Keep defaults if discovery fails.
            }

            entries.Add(new Contracts.LlmProviderCatalogEntry(
                provider.ProviderId,
                provider.DefaultModel,
                models));
        }

        return new Contracts.LlmCatalogResponse(
            _defaultProvider,
            _defaultModel,
            entries);
    }
}

internal sealed class PrayerPreferenceStore
{
    public IReadOnlyList<Contracts.BotProfile> LoadBots()
    {
        try
        {
            if (!File.Exists(AppPaths.SavedBotsFile))
                return Array.Empty<Contracts.BotProfile>();

            var raw = File.ReadAllText(AppPaths.SavedBotsFile);
            var loaded = JsonSerializer.Deserialize<List<Contracts.BotProfile>>(raw);
            if (loaded == null)
                return Array.Empty<Contracts.BotProfile>();

            return loaded
                .Where(b =>
                    !string.IsNullOrWhiteSpace(b.Username) &&
                    !string.IsNullOrWhiteSpace(b.Password))
                .Select(b => new Contracts.BotProfile(b.Username.Trim(), b.Password))
                .ToList();
        }
        catch
        {
            return Array.Empty<Contracts.BotProfile>();
        }
    }

    public void UpsertBot(string username, string password)
    {
        var bots = LoadBots().ToList();
        var normalizedUsername = username.Trim();
        var existing = bots.FindIndex(b =>
            string.Equals(b.Username, normalizedUsername, StringComparison.OrdinalIgnoreCase));

        var updated = new Contracts.BotProfile(normalizedUsername, password);
        if (existing >= 0)
            bots[existing] = updated;
        else
            bots.Add(updated);

        SaveBots(bots);
    }

    public Contracts.DefaultLlmPreferenceResponse? LoadDefaultLlmPreference()
    {
        try
        {
            if (!File.Exists(AppPaths.SavedLlmSelectionFile))
                return null;

            var raw = File.ReadAllText(AppPaths.SavedLlmSelectionFile);
            var loaded = JsonSerializer.Deserialize<Contracts.DefaultLlmPreferenceResponse>(raw);
            if (loaded == null)
                return null;

            var provider = (loaded.Provider ?? string.Empty).Trim().ToLowerInvariant();
            var model = (loaded.Model ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
                return null;

            return new Contracts.DefaultLlmPreferenceResponse(provider, model);
        }
        catch
        {
            return null;
        }
    }

    public void SaveDefaultLlmPreference(string provider, string model)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedModel = (model ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedProvider) || string.IsNullOrWhiteSpace(normalizedModel))
            return;

        var json = JsonSerializer.Serialize(
            new Contracts.DefaultLlmPreferenceResponse(normalizedProvider, normalizedModel),
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.SavedLlmSelectionFile, json);
    }

    private static void SaveBots(IReadOnlyList<Contracts.BotProfile> bots)
    {
        var json = JsonSerializer.Serialize(
            bots,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AppPaths.SavedBotsFile, json);
    }
}

internal static class PrayerTelemetry
{
    private static readonly Meter Meter = new("Prayer.Service", "1.0.0");
    private static readonly Histogram<double> HttpRequestDurationMs =
        Meter.CreateHistogram<double>("prayer.http.request.duration.ms", "ms");
    private static readonly Counter<long> HttpRequestCount =
        Meter.CreateCounter<long>("prayer.http.requests.total");
    private static readonly Histogram<double> RuntimeCommandDurationMs =
        Meter.CreateHistogram<double>("prayer.runtime.command.duration.ms", "ms");
    private static readonly Counter<long> RuntimeCommandCount =
        Meter.CreateCounter<long>("prayer.runtime.commands.total");
    private static readonly Histogram<double> SessionProvisionDurationMs =
        Meter.CreateHistogram<double>("prayer.runtime.session.provision.duration.ms", "ms");
    private static readonly Counter<long> SessionProvisionCount =
        Meter.CreateCounter<long>("prayer.runtime.session.provision.total");
    private static readonly Histogram<double> RuntimeWorkerLifetimeSeconds =
        Meter.CreateHistogram<double>("prayer.runtime.worker.lifetime.s", "s");
    private static readonly Counter<long> RuntimeWorkerFaultCount =
        Meter.CreateCounter<long>("prayer.runtime.worker.faults.total");

    public static void RecordHttpRequest(string method, string route, int statusCode, double elapsedMs)
    {
        var tags = new TagList
        {
            { "method", method },
            { "route", route },
            { "status_code", statusCode }
        };
        HttpRequestDurationMs.Record(elapsedMs, tags);
        HttpRequestCount.Add(1, tags);
    }

    public static void RecordRuntimeCommand(string command, bool success, double elapsedMs)
    {
        var tags = new TagList
        {
            { "command", command },
            { "success", success }
        };
        RuntimeCommandDurationMs.Record(elapsedMs, tags);
        RuntimeCommandCount.Add(1, tags);
    }

    public static void RecordSessionProvision(string operation, bool success, double elapsedMs)
    {
        var tags = new TagList
        {
            { "operation", operation },
            { "success", success }
        };
        SessionProvisionDurationMs.Record(elapsedMs, tags);
        SessionProvisionCount.Add(1, tags);
    }

    public static void RecordRuntimeWorkerLifetime(double elapsedSeconds)
    {
        RuntimeWorkerLifetimeSeconds.Record(elapsedSeconds);
    }

    public static void RecordRuntimeWorkerFault()
    {
        RuntimeWorkerFaultCount.Add(1);
    }
}
