using System;

internal sealed class SpaceMoltSessionInfo
{
    public required string Id { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
