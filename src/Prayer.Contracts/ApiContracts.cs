using System;
using System.Collections.Generic;

namespace Prayer.Contracts;

public sealed record CreateSessionRequest(string Username, string Password, string? Label = null);

public sealed record RegisterSessionRequest(
    string Username,
    string Empire,
    string RegistrationCode,
    string? Label = null);

public sealed record RegisterSessionResponse(string SessionId, string Password);

public sealed record RuntimeCommandRequest(string Command, string? Argument = null);

public sealed record SetScriptRequest(string Script);

public sealed record GenerateScriptRequest(string Prompt);
public sealed record GenerateScriptResponse(string Script);

public sealed record LoopUpdateRequest(bool Enabled);

public sealed record LoopUpdateResponse(string SessionId, bool Enabled);

public sealed record CommandAckResponse(string SessionId, string Command, string Message);

public sealed record RuntimeHostSnapshotDto(
    bool IsHalted,
    bool HasActiveCommand,
    int? CurrentScriptLine,
    string? CurrentScript);

public sealed record RuntimeSnapshotResponse(
    string SessionId,
    RuntimeHostSnapshotDto Snapshot,
    string? LatestSystem,
    string? LatestPoi,
    int? Fuel,
    int? MaxFuel,
    int? Credits,
    DateTime LastUpdatedUtc);

public sealed record RuntimeStateResponse(
    RuntimeGameStateDto? State,
    IReadOnlyList<string> Memory,
    IReadOnlyList<string> ExecutionStatusLines,
    string? ControlInput,
    int? CurrentScriptLine,
    string? LastGenerationPrompt,
    bool LoopEnabled);

public sealed record SessionSummary(
    string Id,
    string Label,
    DateTime CreatedUtc,
    DateTime LastUpdatedUtc,
    bool LoopEnabled,
    bool IsHalted,
    bool HasActiveCommand,
    int? CurrentScriptLine);

public sealed record LlmProviderCatalogEntry(
    string ProviderId,
    string DefaultModel,
    IReadOnlyList<string> Models);

public sealed record LlmCatalogResponse(
    string DefaultProvider,
    string DefaultModel,
    IReadOnlyList<LlmProviderCatalogEntry> Providers);

public sealed record SessionLlmConfigResponse(
    string Provider,
    string Model);

public sealed record UpdateSessionLlmRequest(
    string Provider,
    string Model);

public sealed record BotProfile(
    string Username,
    string Password);

public sealed record BotProfilesResponse(
    IReadOnlyList<BotProfile> Bots);

public sealed record UpsertBotProfileRequest(
    string Username,
    string Password);

public sealed record DefaultLlmPreferenceResponse(
    string? Provider,
    string? Model);

public sealed record UpdateDefaultLlmPreferenceRequest(
    string Provider,
    string Model);
