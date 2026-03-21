using System.Text.Json;
using Prayer.Contracts;

var snapshot = new RuntimeStateResponse(
    State: new RuntimeGameStateDto
    {
        System = "core",
        CurrentPOI = new RuntimePoiInfoDto
        {
            Id = "core.station",
            SystemId = "core",
            Name = "Core Station",
            Type = "station",
            Description = "main hub",
            Online = 42
        },
        POIs = new[]
        {
            new RuntimePoiInfoDto
            {
                Id = "core.station",
                SystemId = "core",
                Name = "Core Station",
                Type = "station"
            }
        },
        Systems = new[] { "core" },
        Ship = new RuntimePlayerShipDto
        {
            Name = "Starter",
            ClassId = "fighter_scout",
            Fuel = 12,
            MaxFuel = 20,
            CargoUsed = 2,
            CargoCapacity = 8
        }
    },
    Memory: new[] { "m1" },
    ExecutionStatusLines: new[] { "ok" },
    ControlInput: "wait",
    CurrentScriptLine: 1,
    LastGenerationPrompt: null);

var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
using var doc = JsonDocument.Parse(json);
var root = doc.RootElement;

AssertType(root, "state", JsonValueKind.Object);
AssertType(root, "memory", JsonValueKind.Array);
AssertType(root, "executionStatusLines", JsonValueKind.Array);
AssertType(root, "controlInput", JsonValueKind.String);
AssertType(root, "currentScriptLine", JsonValueKind.Number);

var state = root.GetProperty("state");
AssertType(state, "system", JsonValueKind.String);
AssertType(state, "currentPOI", JsonValueKind.Object);
AssertType(state, "systems", JsonValueKind.Array);
AssertType(state, "ship", JsonValueKind.Object);

var ship = state.GetProperty("ship");
AssertType(ship, "name", JsonValueKind.String);
AssertType(ship, "classId", JsonValueKind.String);
AssertType(ship, "fuel", JsonValueKind.Number);
AssertType(ship, "maxFuel", JsonValueKind.Number);
AssertType(ship, "cargoUsed", JsonValueKind.Number);
AssertType(ship, "cargoCapacity", JsonValueKind.Number);

Console.WriteLine("OK: RuntimeStateResponse contract shape is typed and stable.");

static void AssertType(JsonElement parent, string name, JsonValueKind expected)
{
    if (!parent.TryGetProperty(name, out var element))
        throw new InvalidOperationException($"Missing property '{name}'.");

    bool kindMatches = expected == JsonValueKind.True || expected == JsonValueKind.False
        ? element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False
        : element.ValueKind == expected;

    if (!kindMatches)
        throw new InvalidOperationException(
            $"Property '{name}' has kind '{element.ValueKind}', expected '{expected}'.");
}
