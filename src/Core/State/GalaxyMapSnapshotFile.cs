using System.Collections.Generic;
using System.IO;
using System.Text.Json;

internal static class GalaxyMapSnapshotFile
{
    public static GalaxyMapSnapshot Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new GalaxyMapSnapshot();

            string raw = File.ReadAllText(path);
            return Parse(raw);
        }
        catch
        {
            return new GalaxyMapSnapshot();
        }
    }

    public static GalaxyMapSnapshot Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new GalaxyMapSnapshot();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return Parse(doc.RootElement);
        }
        catch
        {
            return new GalaxyMapSnapshot();
        }
    }

    public static GalaxyMapSnapshot Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new GalaxyMapSnapshot();

        // Backward compatibility for old parsed cache files.
        if (root.TryGetProperty("Systems", out var legacySystems) &&
            legacySystems.ValueKind == JsonValueKind.Array)
        {
            return ParseLegacySnapshot(legacySystems);
        }

        var systems = new List<GalaxySystemInfo>();

        foreach (var systemObj in EnumerateSystemsFromMap(root))
        {
            if (systemObj.ValueKind != JsonValueKind.Object)
                continue;

            string? systemId = TryGetString(systemObj, "id")
                               ?? TryGetString(systemObj, "system_id")
                               ?? TryGetString(systemObj, "Id");

            if (string.IsNullOrWhiteSpace(systemId))
                continue;

            string empire = TryGetString(systemObj, "empire")
                            ?? TryGetString(systemObj, "Empire")
                            ?? "";

            double? x = null;
            double? y = null;
            if (TryGetObject(systemObj, "position", out var positionObj) ||
                TryGetObject(systemObj, "Position", out positionObj))
            {
                x = TryGetDouble(positionObj, "x") ?? TryGetDouble(positionObj, "X");
                y = TryGetDouble(positionObj, "y") ?? TryGetDouble(positionObj, "Y");
            }

            var connections = new List<string>();
            if (TryGetArray(systemObj, "connections", out var connectionsArray) ||
                TryGetArray(systemObj, "Connections", out connectionsArray))
            {
                foreach (var connection in connectionsArray.EnumerateArray())
                {
                    string? connectionId = connection.ValueKind == JsonValueKind.String
                        ? connection.GetString()
                        : TryGetString(connection, "system_id")
                          ?? TryGetString(connection, "id")
                          ?? TryGetString(connection, "Id");

                    if (!string.IsNullOrWhiteSpace(connectionId))
                        connections.Add(connectionId);
                }
            }

            var poiList = new List<GalaxyPoiInfo>();

            if (systemObj.TryGetProperty("pois", out var pois) &&
                pois.ValueKind == JsonValueKind.Array)
            {
                foreach (var poi in pois.EnumerateArray())
                {
                    string? poiId = TryGetString(poi, "id")
                                    ?? TryGetString(poi, "poi_id")
                                    ?? TryGetString(poi, "Id");

                    if (string.IsNullOrWhiteSpace(poiId))
                        continue;

                    poiList.Add(new GalaxyPoiInfo { Id = poiId });
                }
            }

            systems.Add(new GalaxySystemInfo
            {
                Id = systemId,
                Empire = empire,
                X = x,
                Y = y,
                Connections = connections,
                Pois = poiList
            });
        }

        return new GalaxyMapSnapshot { Systems = systems };
    }

    private static GalaxyMapSnapshot ParseLegacySnapshot(JsonElement systemsArray)
    {
        var systems = new List<GalaxySystemInfo>();

        foreach (var systemObj in systemsArray.EnumerateArray())
        {
            if (systemObj.ValueKind != JsonValueKind.Object)
                continue;

            string? systemId = TryGetString(systemObj, "Id")
                               ?? TryGetString(systemObj, "id")
                               ?? TryGetString(systemObj, "system_id");

            if (string.IsNullOrWhiteSpace(systemId))
                continue;

            string empire = TryGetString(systemObj, "Empire")
                            ?? TryGetString(systemObj, "empire")
                            ?? "";
            double? x = TryGetDouble(systemObj, "X");
            double? y = TryGetDouble(systemObj, "Y");
            if (TryGetObject(systemObj, "Position", out var legacyPosition) ||
                TryGetObject(systemObj, "position", out legacyPosition))
            {
                x ??= TryGetDouble(legacyPosition, "X") ?? TryGetDouble(legacyPosition, "x");
                y ??= TryGetDouble(legacyPosition, "Y") ?? TryGetDouble(legacyPosition, "y");
            }

            var connections = new List<string>();
            if (TryGetArray(systemObj, "Connections", out var legacyConnections) ||
                TryGetArray(systemObj, "connections", out legacyConnections))
            {
                foreach (var connection in legacyConnections.EnumerateArray())
                {
                    string? connectionId = connection.ValueKind == JsonValueKind.String
                        ? connection.GetString()
                        : TryGetString(connection, "Id")
                          ?? TryGetString(connection, "id")
                          ?? TryGetString(connection, "system_id");

                    if (!string.IsNullOrWhiteSpace(connectionId))
                        connections.Add(connectionId);
                }
            }

            var poiList = new List<GalaxyPoiInfo>();

            if (TryGetArray(systemObj, "Pois", out var legacyPois) ||
                TryGetArray(systemObj, "pois", out legacyPois))
            {
                foreach (var poi in legacyPois.EnumerateArray())
                {
                    string? poiId = TryGetString(poi, "Id")
                                    ?? TryGetString(poi, "id")
                                    ?? TryGetString(poi, "poi_id");

                    if (string.IsNullOrWhiteSpace(poiId))
                        continue;

                    poiList.Add(new GalaxyPoiInfo { Id = poiId });
                }
            }

            systems.Add(new GalaxySystemInfo
            {
                Id = systemId,
                Empire = empire,
                X = x,
                Y = y,
                Connections = connections,
                Pois = poiList
            });
        }

        return new GalaxyMapSnapshot { Systems = systems };
    }

    private static IEnumerable<JsonElement> EnumerateSystemsFromMap(JsonElement map)
    {
        if (map.ValueKind != JsonValueKind.Object)
            yield break;

        if (map.TryGetProperty("systems", out var systems) &&
            systems.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in systems.EnumerateArray())
                yield return s;
            yield break;
        }

        if (map.TryGetProperty("map", out var mapObj) &&
            mapObj.ValueKind == JsonValueKind.Object &&
            mapObj.TryGetProperty("systems", out var nestedSystems) &&
            nestedSystems.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in nestedSystems.EnumerateArray())
                yield return s;
        }
    }

    private static bool TryGetArray(JsonElement obj, string key, out JsonElement array)
    {
        array = default;

        if (obj.ValueKind != JsonValueKind.Object)
            return false;

        if (!obj.TryGetProperty(key, out var prop) ||
            prop.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        array = prop;
        return true;
    }

    private static bool TryGetObject(JsonElement obj, string key, out JsonElement value)
    {
        value = default;

        if (obj.ValueKind != JsonValueKind.Object)
            return false;

        if (!obj.TryGetProperty(key, out var prop) ||
            prop.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        value = prop;
        return true;
    }

    private static string? TryGetString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(key, out var prop))
            return null;

        return prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static double? TryGetDouble(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(key, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out double value))
            return value;

        return null;
    }
}
