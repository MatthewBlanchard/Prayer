using System;
using System.Collections.Generic;
using System.IO;
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
        var cachedMap = GalaxyMapSnapshotFile.Load(_mapFile);
        var knownPois = GalaxyKnownPoiSnapshotFile.Load(_knownPoisFile);
        GalaxyMapSnapshotFile.MergeKnownPois(cachedMap, knownPois);
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
                (_cachedMap.Systems.Count > 0 || _cachedMap.KnownPois.Count > 0))
                return _cachedMap;

            if (!forceRefresh && File.Exists(_mapFile))
            {
                try
                {
                    string rawCache = await File.ReadAllTextAsync(_mapFile);
                    var hydrated = GalaxyMapSnapshotFile.Parse(rawCache);
                    var knownPois = GalaxyKnownPoiSnapshotFile.Load(_knownPoisFile);
                    GalaxyMapSnapshotFile.MergeKnownPois(hydrated, knownPois);
                    if (hydrated.Systems.Count > 0 || hydrated.KnownPois.Count > 0)
                    {
                        _cachedMap = hydrated;
                        GalaxyStateHub.MergeMap(_cachedMap);
                        return _cachedMap;
                    }
                }
                catch
                {
                    // Ignore cache read/parse errors and refresh from API.
                }
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

            _cachedMap = GalaxyMapSnapshotFile.Parse(mapResult);
            GalaxyMapSnapshotFile.MergeKnownPois(
                _cachedMap,
                GalaxyKnownPoiSnapshotFile.Load(_knownPoisFile));
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
            _cachedMap ??= GalaxyMapSnapshotFile.Load(_mapFile);
            GalaxyMapSnapshotFile.MergeKnownPois(
                _cachedMap,
                GalaxyKnownPoiSnapshotFile.Load(_knownPoisFile));

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
}
