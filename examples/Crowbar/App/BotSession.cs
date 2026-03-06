using System.Collections.Generic;

public sealed class BotSession
{
    public BotSession(
        string id,
        string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }
    public string Label { get; }
    public string? PrayerSessionId { get; set; }
    public bool LoopEnabled { get; set; }
    public List<string> ExecutionStatusLines { get; } = new();
    public AppPrayerRuntimeState? LastPrayerState { get; set; }
    public long PrayerStateVersion { get; set; }
}
