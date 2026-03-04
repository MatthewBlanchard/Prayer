public sealed record BotTab(string Id, string Label);
public sealed record MissionPromptOption(string MissionId, string Label, string Prompt, string IssuingPoiId);
public sealed record LoopUpdate(bool? Enabled);

public enum AddBotMode
{
    Login,
    Register
}

public sealed record AddBotRequest(
    AddBotMode Mode,
    string Username,
    string? Password = null,
    string? RegistrationCode = null,
    string? Empire = null);
