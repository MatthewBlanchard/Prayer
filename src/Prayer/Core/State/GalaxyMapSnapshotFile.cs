using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

internal static class GalaxyMapSnapshotFile
{
    public static GalaxyMapSnapshot LoadWithKnownPois(string mapPath, string knownPoisPath)
    {
        var map = Load(mapPath);
        var knownPois = GalaxyKnownPoiSnapshotFile.Load(knownPoisPath);
        MergeKnownPois(map, knownPois);
        return map;
    }

    public static GalaxyMapSnapshot ParseWithKnownPois(JsonElement root, string knownPoisPath)
    {
        var map = Parse(root);
        var knownPois = GalaxyKnownPoiSnapshotFile.Load(knownPoisPath);
        MergeKnownPois(map, knownPois);
        return map;
    }

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
            bool isStronghold = TryGetBool(systemObj, "is_stronghold")
                                ?? TryGetBool(systemObj, "isStronghold")
                                ?? TryGetBool(systemObj, "IsStronghold")
                                ?? false;

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

                    TryGetPosition(poi, out var poiX, out var poiY);
                    poiList.Add(new GalaxyPoiInfo
                    {
                        Id = poiId,
                        X = poiX,
                        Y = poiY
                    });
                }
            }

            systems.Add(new GalaxySystemInfo
            {
                Id = systemId,
                Empire = empire,
                IsStronghold = isStronghold,
                X = x,
                Y = y,
                Connections = connections,
                Pois = poiList
            });
        }

        return new GalaxyMapSnapshot { Systems = systems };
    }

    public static void MergeKnownPois(
        GalaxyMapSnapshot map,
        IEnumerable<GalaxyKnownPoiInfo>? knownPois)
    {
        if (map == null)
            return;

        var knownList = (knownPois ?? Enumerable.Empty<GalaxyKnownPoiInfo>())
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => g
                .OrderByDescending(p => p.LastSeenUtc)
                .First())
            .ToList();

        map.KnownPois = knownList
            .Select(p => new GalaxyKnownPoiInfo
            {
                Id = p.Id,
                SystemId = p.SystemId,
                Name = p.Name,
                Type = p.Type,
                X = p.X,
                Y = p.Y,
                HasBase = p.HasBase,
                BaseId = p.BaseId,
                BaseName = p.BaseName,
                LastSeenUtc = p.LastSeenUtc
            })
            .ToList();

        foreach (var knownPoi in knownList)
        {
            string systemId = knownPoi.SystemId ?? "";
            if (string.IsNullOrWhiteSpace(systemId))
                continue;

            var system = map.Systems.FirstOrDefault(s =>
                string.Equals(s.Id, systemId, StringComparison.Ordinal));

            if (system == null)
            {
                system = new GalaxySystemInfo { Id = systemId };
                map.Systems.Add(system);
            }

            system.Pois ??= new List<GalaxyPoiInfo>();
            var existingPoi = system.Pois.FirstOrDefault(p =>
                string.Equals(p.Id, knownPoi.Id, StringComparison.Ordinal));

            if (existingPoi == null)
            {
                system.Pois.Add(new GalaxyPoiInfo
                {
                    Id = knownPoi.Id,
                    X = knownPoi.X,
                    Y = knownPoi.Y
                });
                continue;
            }

            existingPoi.X ??= knownPoi.X;
            existingPoi.Y ??= knownPoi.Y;
        }
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

    private static void TryGetPosition(JsonElement obj, out double? x, out double? y)
    {
        x = TryGetDouble(obj, "x") ?? TryGetDouble(obj, "X");
        y = TryGetDouble(obj, "y") ?? TryGetDouble(obj, "Y");

        if (TryGetObject(obj, "position", out var positionObj) ||
            TryGetObject(obj, "Position", out positionObj))
        {
            x ??= TryGetDouble(positionObj, "x") ?? TryGetDouble(positionObj, "X");
            y ??= TryGetDouble(positionObj, "y") ?? TryGetDouble(positionObj, "Y");
        }
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

    private static bool? TryGetBool(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(key, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}

internal static class GalaxyKnownPoiSnapshotFile
{
    public static List<GalaxyKnownPoiInfo> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new List<GalaxyKnownPoiInfo>();

            string raw = File.ReadAllText(path);
            return Parse(raw);
        }
        catch
        {
            return new List<GalaxyKnownPoiInfo>();
        }
    }

    public static List<GalaxyKnownPoiInfo> Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new List<GalaxyKnownPoiInfo>();

        try
        {
            using var doc = JsonDocument.Parse(raw);
            return Parse(doc.RootElement);
        }
        catch
        {
            return new List<GalaxyKnownPoiInfo>();
        }
    }

    public static List<GalaxyKnownPoiInfo> Parse(JsonElement root)
    {
        var list = new List<GalaxyKnownPoiInfo>();

        if (root.ValueKind == JsonValueKind.Array)
        {
            ParseArray(root, list);
            return list;
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("pois", out var poisArray) &&
            poisArray.ValueKind == JsonValueKind.Array)
        {
            ParseArray(poisArray, list);
        }

        return list;
    }

    public static void Save(string path, IEnumerable<GalaxyKnownPoiInfo>? knownPois)
    {
        var deduped = (knownPois ?? Enumerable.Empty<GalaxyKnownPoiInfo>())
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Id))
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .Select(g => g
                .OrderByDescending(p => p.LastSeenUtc)
                .First())
            .OrderBy(p => p.SystemId, StringComparer.Ordinal)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        var payload = new
        {
            pois = deduped.Select(p => new
            {
                id = p.Id,
                system_id = p.SystemId,
                name = p.Name,
                type = p.Type,
                x = p.X,
                y = p.Y,
                has_base = p.HasBase,
                base_id = p.BaseId,
                base_name = p.BaseName,
                last_seen_utc = p.LastSeenUtc
            }).ToList()
        };

        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(path, json);
    }

    private static void ParseArray(JsonElement array, List<GalaxyKnownPoiInfo> list)
    {
        foreach (var poi in array.EnumerateArray())
        {
            if (poi.ValueKind != JsonValueKind.Object)
                continue;

            string id = TryGetString(poi, "id") ?? TryGetString(poi, "Id") ?? "";
            if (string.IsNullOrWhiteSpace(id))
                continue;

            string systemId = TryGetString(poi, "system_id")
                              ?? TryGetString(poi, "SystemId")
                              ?? "";
            if (string.IsNullOrWhiteSpace(systemId))
                continue;

            DateTime lastSeenUtc = DateTime.UtcNow;
            string? lastSeenRaw = TryGetString(poi, "last_seen_utc")
                                  ?? TryGetString(poi, "LastSeenUtc");
            if (!string.IsNullOrWhiteSpace(lastSeenRaw) &&
                DateTime.TryParse(lastSeenRaw, out var parsed))
            {
                lastSeenUtc = parsed.ToUniversalTime();
            }

            double? x = TryGetDouble(poi, "x") ?? TryGetDouble(poi, "X");
            double? y = TryGetDouble(poi, "y") ?? TryGetDouble(poi, "Y");
            if (TryGetObject(poi, "position", out var positionObj) ||
                TryGetObject(poi, "Position", out positionObj))
            {
                x ??= TryGetDouble(positionObj, "x") ?? TryGetDouble(positionObj, "X");
                y ??= TryGetDouble(positionObj, "y") ?? TryGetDouble(positionObj, "Y");
            }

            list.Add(new GalaxyKnownPoiInfo
            {
                Id = id,
                SystemId = systemId,
                Name = TryGetString(poi, "name") ?? TryGetString(poi, "Name") ?? "",
                Type = TryGetString(poi, "type") ?? TryGetString(poi, "Type") ?? "",
                X = x,
                Y = y,
                HasBase = TryGetBool(poi, "has_base") ?? TryGetBool(poi, "HasBase") ?? false,
                BaseId = TryGetString(poi, "base_id") ?? TryGetString(poi, "BaseId"),
                BaseName = TryGetString(poi, "base_name") ?? TryGetString(poi, "BaseName"),
                LastSeenUtc = lastSeenUtc
            });
        }
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

    private static bool? TryGetBool(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(key, out var prop) ||
            (prop.ValueKind != JsonValueKind.True && prop.ValueKind != JsonValueKind.False))
        {
            return null;
        }

        return prop.GetBoolean();
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

    private static double? TryGetDouble(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        if (!obj.TryGetProperty(key, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out var value))
            return value;

        return null;
    }
}
