using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

public class MineCommand : IMultiTurnCommand, IDslCommandGrammar
{
    public string Name => "mine";
    public DslCommandSyntax GetDslSyntax() => new(
        ArgSpecs: new[]
        {
            new DslArgumentSpec(
                DslArgKind.Enum,
                Required: false,
                EnumType: "mining_target")
        });

    private bool _stopRequested;
    private string? _stopReason;
    private string? _targetPoiId;

    public bool IsAvailable(GameState state)
        => !state.Docked &&
           state.CurrentPOI?.IsMiningTarget == true &&
           state.CargoUsed < state.CargoCapacity;
    public string BuildHelp(GameState state)
        => "- mine [asteroid_belt|asteroid|gas_cloud|ice_field] → mine here, or auto-go to local POI type";

    public async Task<CommandExecutionResult?> StartAsync(
        SpaceMoltHttpClient client,
        CommandResult cmd,
        GameState state)
    {
        _stopRequested = false;
        _stopReason = null;
        _targetPoiId = null;

        var requestedType = NormalizeMiningType(cmd.Arg1);
        if (!string.IsNullOrWhiteSpace(cmd.Arg1) && requestedType == null)
        {
            var valid = string.Join(", ", SupportedMiningTypes);
            _stopRequested = true;
            _stopReason = $"Unsupported mine target '{cmd.Arg1}'. Use one of: {valid}.";
            return new CommandExecutionResult { ResultMessage = _stopReason };
        }

        if (requestedType != null)
        {
            var matchingLocalPoi = state.POIs
                .Where(p => string.Equals(p.Type, requestedType, StringComparison.Ordinal))
                .OrderByDescending(p => p.Online)
                .FirstOrDefault();

            if (matchingLocalPoi == null)
            {
                _stopRequested = true;
                _stopReason = $"No local POI of type '{requestedType}' in system {state.System}.";
                return new CommandExecutionResult { ResultMessage = _stopReason };
            }

            _targetPoiId = matchingLocalPoi.Id;
        }
        else
        {
            var defaultLocalPoi = SelectDefaultMiningPoi(state);
            if (defaultLocalPoi == null)
            {
                _stopRequested = true;
                _stopReason = $"No local mining POI found in system {state.System}.";
                return new CommandExecutionResult { ResultMessage = _stopReason };
            }

            _targetPoiId = defaultLocalPoi.Id;
        }

        var response = await ExecuteStepAsync(client, state);

        return new CommandExecutionResult
        {
            ResultMessage = _stopReason ?? CommandJson.TryGetResultMessage(response)
        };
    }

    public async Task<(bool finished, CommandExecutionResult? result)> ContinueAsync(
        SpaceMoltHttpClient client,
        GameState state)
    {
        if (_stopRequested)
        {
            var reason = _stopReason ?? "Mining stopped.";
            _stopRequested = false;
            _stopReason = null;

            return (true, new CommandExecutionResult
            {
                ResultMessage = reason
            });
        }

        bool navigatingToTarget = !string.IsNullOrWhiteSpace(_targetPoiId) &&
                                  !string.Equals(state.CurrentPOI?.Id, _targetPoiId, StringComparison.Ordinal);

        // Stop conditions (once we're at the chosen target/current context).
        if (!navigatingToTarget && !IsAvailable(state))
        {
            return (true, new CommandExecutionResult
            {
                ResultMessage = "Mining complete."
            });
        }

        var response = await ExecuteStepAsync(client, state);

        if (_stopRequested)
        {
            var reason = _stopReason ?? "Mining stopped.";
            _stopRequested = false;
            _stopReason = null;

            return (true, new CommandExecutionResult
            {
                ResultMessage = reason
            });
        }

        return (false, new CommandExecutionResult
        {
            ResultMessage = CommandJson.TryGetResultMessage(response)
        });
    }

    private async Task<JsonElement> ExecuteStepAsync(
        SpaceMoltHttpClient client,
        GameState state)
    {
        if (!string.IsNullOrWhiteSpace(_targetPoiId) &&
            !string.Equals(state.CurrentPOI?.Id, _targetPoiId, StringComparison.Ordinal))
        {
            if (state.Docked)
            {
                await client.ExecuteAsync("undock");
                return default;
            }

            await client.ExecuteAsync("travel", new { target_poi = _targetPoiId });
            return default;
        }

        if (state.Docked)
        {
            await client.ExecuteAsync("undock");
            return default;
        }

        JsonElement response = await client.ExecuteAsync("mine");
        CaptureStopReasonFromResponse(response);
        return response;
    }

    private static readonly HashSet<string> SupportedMiningTypes =
        new(StringComparer.Ordinal)
        {
            "asteroid_belt",
            "asteroid",
            "gas_cloud",
            "ice_field"
        };

    private static readonly string[] DefaultMiningPriority =
    {
        "asteroid_belt",
        "asteroid",
        "ice_field",
        "gas_cloud"
    };

    private static string? NormalizeMiningType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var token = raw.Trim().ToLowerInvariant();
        return SupportedMiningTypes.Contains(token) ? token : null;
    }

    private static POIInfo? SelectDefaultMiningPoi(GameState state)
    {
        foreach (var type in DefaultMiningPriority)
        {
            var match = state.POIs
                .Where(p => string.Equals(p.Type, type, StringComparison.Ordinal))
                .OrderByDescending(p => p.Online)
                .FirstOrDefault();

            if (match != null)
                return match;
        }

        return null;
    }

    private void CaptureStopReasonFromResponse(JsonElement response)
    {
        if (!CommandJson.TryGetError(response, out var code, out var message))
            return;

        if (string.Equals(code, "depleted", StringComparison.OrdinalIgnoreCase))
            return;

        _stopRequested = true;
        _stopReason = $"Error: {(string.IsNullOrWhiteSpace(code) ? "" : $" ({code})")}: {message ?? "unknown"}";
    }
}

// =====================================================
// HALT (PAUSE AUTONOMY)
// =====================================================
