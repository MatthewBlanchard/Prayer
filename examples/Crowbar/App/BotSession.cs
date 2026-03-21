using System.Collections.Generic;

public sealed class BotSession
{
    public BotSession(
        string id,
        string label,
        string colorHex)
    {
        Id = id;
        Label = label;
        ColorHex = colorHex;
    }

    public string Id { get; }
    public string Label { get; }
    public string ColorHex { get; }
    public string? PrayerSessionId { get; set; }
    public List<string> ExecutionStatusLines { get; } = new();
    public AppPrayerRuntimeState? LastPrayerState { get; set; }
    public long PrayerStateVersion { get; set; }
    public Prayer.Contracts.ActiveGoRouteDto? LastActiveRoute { get; set; }
    public List<string> RoleplayToolTraceLines { get; } = new();
    public AutonomousDriver? Driver { get; set; }
    public string? RoleplayPersona { get; set; }
}
