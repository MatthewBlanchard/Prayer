using System;
using System.Collections.Generic;
using System.Linq;

public class DslCommand
{
    public DslCommand(string name, IReadOnlyList<string>? args = null)
    {
        Name = name ?? "";
        Args = args ?? Array.Empty<string>();
    }

    public string Name { get; }
    public IReadOnlyList<string> Args { get; }

    public virtual CommandResult ToValidCommand(GameState? state, DslCommand self)
    {
        var normalized = DslParser.NormalizeCommandStep(self.Name, self.Args);
        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var action = parts.ElementAtOrDefault(0) ?? "";
        var args = parts.Skip(1).ToList();

        if (state != null && !string.IsNullOrWhiteSpace(action) && args.Count > 0)
        {
            var specs = DslParser.GetArgSpecsForCommand(action);
            DslFuzzyMatcher.ValidateArguments(action, args, specs, state);
        }

        var arg2Raw = args.ElementAtOrDefault(1);
        return new CommandResult
        {
            Action = action,
            Arg1 = args.ElementAtOrDefault(0),
            Arg2 = arg2Raw,
            Quantity = int.TryParse(arg2Raw, out int n) ? n : null
        };
    }
}
