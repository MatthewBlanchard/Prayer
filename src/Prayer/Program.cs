using System.Collections.Concurrent;

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
    Results.Ok(store.GetAll()));

app.MapPost("/api/runtime/sessions", (CreateSessionRequest? request, RuntimeSessionStore store) =>
{
    var session = store.Create(request?.Label);
    return Results.Created($"/api/runtime/sessions/{session.Id}", session);
});

app.MapGet("/api/runtime/sessions/{id}", (string id, RuntimeSessionStore store) =>
{
    return store.TryGet(id, out var session)
        ? Results.Ok(session)
        : Results.NotFound();
});

app.MapGet("/api/runtime/sessions/{id}/snapshot", (string id, RuntimeSessionStore store) =>
{
    if (!store.TryGet(id, out var session))
        return Results.NotFound();

    return Results.Ok(new RuntimeSnapshotResponse(
        SessionId: session.Id,
        IsHalted: session.IsHalted,
        CurrentScript: session.CurrentScript,
        LastCommand: session.LastCommand,
        LastUpdatedUtc: session.LastUpdatedUtc));
});

app.MapPost("/api/runtime/sessions/{id}/commands", (string id, RuntimeCommandRequest request, RuntimeSessionStore store) =>
{
    if (!store.TryApplyCommand(id, request, out var updated))
        return Results.NotFound();

    return Results.Ok(updated);
});

app.Run();

internal sealed class RuntimeSessionStore
{
    private readonly ConcurrentDictionary<string, RuntimeSessionState> _sessions =
        new(StringComparer.Ordinal);

    public IReadOnlyList<RuntimeSessionState> GetAll()
    {
        return _sessions.Values
            .OrderBy(s => s.CreatedUtc)
            .ToList();
    }

    public RuntimeSessionState Create(string? label)
    {
        var now = DateTime.UtcNow;
        var session = new RuntimeSessionState(
            Id: Guid.NewGuid().ToString("N"),
            Label: string.IsNullOrWhiteSpace(label) ? "runtime" : label.Trim(),
            CreatedUtc: now,
            LastUpdatedUtc: now,
            IsHalted: true,
            CurrentScript: null,
            LastCommand: null);

        _sessions[session.Id] = session;
        return session;
    }

    public bool TryGet(string id, out RuntimeSessionState session)
    {
        return _sessions.TryGetValue(id, out session!);
    }

    public bool TryApplyCommand(string id, RuntimeCommandRequest request, out RuntimeSessionState updated)
    {
        updated = default!;

        if (!_sessions.TryGetValue(id, out var current))
            return false;

        var command = Normalize(request.Command);
        var now = DateTime.UtcNow;

        updated = command switch
        {
            "set_script" => current with
            {
                CurrentScript = request.Argument,
                IsHalted = false,
                LastCommand = command,
                LastUpdatedUtc = now
            },
            "execute_script" => current with
            {
                IsHalted = false,
                LastCommand = command,
                LastUpdatedUtc = now
            },
            "generate_script" => current with
            {
                IsHalted = false,
                LastCommand = command,
                LastUpdatedUtc = now
            },
            "halt" => current with
            {
                IsHalted = true,
                LastCommand = command,
                LastUpdatedUtc = now
            },
            "save_example" => current with
            {
                LastCommand = command,
                LastUpdatedUtc = now
            },
            _ => current with
            {
                LastCommand = command,
                LastUpdatedUtc = now
            }
        };

        _sessions[id] = updated;
        return true;
    }

    private static string Normalize(string? command)
    {
        return (command ?? string.Empty).Trim().ToLowerInvariant();
    }
}

public sealed record CreateSessionRequest(string? Label);

public sealed record RuntimeCommandRequest(string Command, string? Argument);

public sealed record RuntimeSnapshotResponse(
    string SessionId,
    bool IsHalted,
    string? CurrentScript,
    string? LastCommand,
    DateTime LastUpdatedUtc);

public sealed record RuntimeSessionState(
    string Id,
    string Label,
    DateTime CreatedUtc,
    DateTime LastUpdatedUtc,
    bool IsHalted,
    string? CurrentScript,
    string? LastCommand);
