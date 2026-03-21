public sealed record BotTab(string Id, string Label, string ColorHex);
public sealed record BotMapMarker(string BotId, string Label, string SystemId, string ColorHex);
public sealed record MissionPromptOption(string MissionId, string Label, string Prompt, string IssuingPoiId);

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
