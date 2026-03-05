using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class CommandExecutionResult
{
    public string? ResultMessage { get; set; }
}

public interface ICommand
{
    string Name { get; }

    bool IsAvailable(GameState state);

    string BuildHelp(GameState state);
}

[Flags]
public enum DslArgKind
{
    None = 0,
    Any = 1 << 0,
    Integer = 1 << 1,
    System = 1 << 2,
    Item = 1 << 3,
    Enum = 1 << 4,

    // Backward-compat aliases for older command syntax declarations.
    Identifier = Any,
    String = Any
}

public sealed record DslArgumentSpec(
    DslArgKind Kind,
    bool Required = true,
    string? DefaultValue = null,
    string? EnumType = null,
    IReadOnlyList<string>? EnumValues = null,
    IReadOnlyDictionary<string, double>? ArgTypeWeights = null);

public sealed record DslCommandSyntax(
    DslArgKind ArgKind = DslArgKind.None,
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
    Task<CommandExecutionResult?> StartAsync(
        IRuntimeTransport client,
        CommandResult result,
        GameState state);

    Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        IRuntimeTransport client,
        GameState state);
}
