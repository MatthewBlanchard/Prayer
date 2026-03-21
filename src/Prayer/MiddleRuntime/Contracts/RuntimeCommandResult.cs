using System.Text.Json;

public sealed record RuntimeCommandResult(
    bool Succeeded,
    JsonElement Payload,
    string? ErrorMessage = null);
