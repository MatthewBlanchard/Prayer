using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed class SpaceMoltMapService
{
    private readonly string _mapFile;
    private readonly string _knownPoisFile;
    private readonly Func<string, object?, Task<JsonElement>> _executeAsync;
    private readonly SemaphoreSlim _mapCacheLock = new(1, 1);
    private GalaxyMapSnapshot? _cachedMap;

    public SpaceMoltMapService(
        string mapFile,
        string knownPoisFile,
        Func<string, object?, Task<JsonElement>> executeAsync)
    {
        _mapFile = mapFile;
        _knownPoisFile = knownPoisFile;
        _executeAsync = executeAsync;
    }

    public void PromoteCachedMapFromDisk()
    {
        var cachedMap = GalaxyMapSnapshotFile.LoadWithKnownPois(_mapFile, _knownPoisFile);
        if (cachedMap.Systems.Count > 0 || cachedMap.KnownPois.Count > 0)
        {
            _cachedMap = cachedMap;
            GalaxyStateHub.MergeMap(cachedMap);
        }
    }

    public async Task<GalaxyMapSnapshot> GetMapSnapshotAsync(bool forceRefresh = false)
    {
        await _mapCacheLock.WaitAsync();
        try
        {
            if (!forceRefresh &&
                _cachedMap != null &&
                HasRouteTopology(_cachedMap))
                return _cachedMap;

            if (!forceRefresh)
            {
                var hydrated = GalaxyMapSnapshotFile.LoadWithKnownPois(_mapFile, _knownPoisFile);
                if (HasRouteTopology(hydrated))
                {
                    _cachedMap = hydrated;
                    GalaxyStateHub.MergeMap(_cachedMap);
                    return _cachedMap;
                }

                // Keep best-effort POI history available while we fetch a full map.
                _cachedMap = hydrated;
                if (_cachedMap.Systems.Count > 0 || _cachedMap.KnownPois.Count > 0)
                    GalaxyStateHub.MergeMap(_cachedMap);
            }

            JsonElement mapResult = await _executeAsync("get_map", null);

            try
            {
                string rawMap = JsonSerializer.Serialize(mapResult, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                await File.WriteAllTextAsync(_mapFile, rawMap);
            }
            catch
            {
                // Ignore map cache write failures and continue with in-memory map.
            }

            _cachedMap = GalaxyMapSnapshotFile.ParseWithKnownPois(mapResult, _knownPoisFile);
            GalaxyStateHub.MergeMap(_cachedMap);

            return _cachedMap;
        }
        finally
        {
            _mapCacheLock.Release();
        }
    }

    public async Task ObserveSeenPoisAsync(string systemId, IEnumerable<POIInfo> pois)
    {
        if (string.IsNullOrWhiteSpace(systemId) || pois == null)
            return;

        await _mapCacheLock.WaitAsync();
        try
        {
            _cachedMap ??= GalaxyMapSnapshotFile.LoadWithKnownPois(_mapFile, _knownPoisFile);

            var knownByPoiId = (_cachedMap.KnownPois ?? new List<GalaxyKnownPoiInfo>())
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .ToDictionary(p => p.Id, p => p, StringComparer.Ordinal);

            foreach (var poi in pois)
            {
                if (poi == null || string.IsNullOrWhiteSpace(poi.Id))
                    continue;

                knownByPoiId[poi.Id] = new GalaxyKnownPoiInfo
                {
                    Id = poi.Id,
                    SystemId = systemId,
                    Name = poi.Name ?? "",
                    Type = poi.Type ?? "",
                    X = poi.X,
                    Y = poi.Y,
                    HasBase = poi.HasBase,
                    BaseId = poi.BaseId,
                    BaseName = poi.BaseName,
                    LastSeenUtc = DateTime.UtcNow
                };
            }

            var knownList = knownByPoiId.Values.ToList();
            GalaxyMapSnapshotFile.MergeKnownPois(_cachedMap, knownList);
            GalaxyKnownPoiSnapshotFile.Save(_knownPoisFile, knownList);
            GalaxyStateHub.MergeMap(_cachedMap);
        }
        finally
        {
            _mapCacheLock.Release();
        }
    }

    private static bool HasRouteTopology(GalaxyMapSnapshot? map)
    {
        if (map == null || map.Systems == null || map.Systems.Count == 0)
            return false;

        return map.Systems.Any(s => s != null &&
                                    s.Connections != null &&
                                    s.Connections.Count > 0);
    }
}
