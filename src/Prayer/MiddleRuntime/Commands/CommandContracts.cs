using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class CommandExecutionResult
{
    public string? ResultMessage { get; set; }
    public bool HaltScript { get; set; }
}

public interface ICommand
{
    string Name { get; }

    bool IsAvailable(GameState state);

    string BuildHelp(GameState state);

    DslCommandSyntax GetDslSyntax();
}

public enum DslArgType
{
    None = 0,
    Any,
    Integer,
    ItemId,
    SystemId,
    PoiId,
    GoTarget,
    ShipId,
    ListingId,
    MissionId,
    ModuleId,
    RecipeId,
    Enum
}

public sealed record DslArgumentSpec(
    DslArgType Type,
    bool Required = true,
    string? DefaultValue = null,
    string? EnumType = null,
    IReadOnlyList<string>? EnumValues = null);

public sealed record DslCommandSyntax(
    DslArgType ArgType = DslArgType.None,
    bool ArgRequired = false,
    string? DefaultArg = null,
    IReadOnlyList<DslArgumentSpec>? ArgSpecs = null);

public interface IDslCommandGrammar
{
    DslCommandSyntax GetDslSyntax();
}

public interface ISingleTurnCommand : ICommand
{
    Task<CommandExecutionResult?> ExecuteAsync(
        IRuntimeTransport client,
        CommandResult result,
        GameState state);
}

public interface IMultiTurnCommand : ICommand
{
    Task<(bool finished, CommandExecutionResult? result)> StartAsync(
        IRuntimeTransport client,
        CommandResult result,
        GameState state);

    Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state);
}

public sealed record RouteInfo(
    string Target,
    IReadOnlyList<string> Hops,
    int EstimatedFuelUse,
    DateTimeOffset? ArrivalTime);
