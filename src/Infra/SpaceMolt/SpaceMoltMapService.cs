using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal sealed class SpaceMoltMapService
{
    private readonly string _mapFile;
    private readonly Func<string, object?, Task<JsonElement>> _executeAsync;
    private readonly SemaphoreSlim _mapCacheLock = new(1, 1);
    private GalaxyMapSnapshot? _cachedMap;

    public SpaceMoltMapService(
        string mapFile,
        Func<string, object?, Task<JsonElement>> executeAsync)
    {
        _mapFile = mapFile;
        _executeAsync = executeAsync;
    }

    public void PromoteCachedMapFromDisk()
    {
        var cachedMap = GalaxyMapSnapshotFile.Load(_mapFile);
        if (cachedMap.Systems.Count > 0)
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
            if (!forceRefresh && _cachedMap != null && _cachedMap.Systems.Count > 0)
                return _cachedMap;

            if (!forceRefresh && File.Exists(_mapFile))
            {
                try
                {
                    string rawCache = await File.ReadAllTextAsync(_mapFile);
                    var hydrated = GalaxyMapSnapshotFile.Parse(rawCache);
                    if (hydrated.Systems.Count > 0)
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
            GalaxyStateHub.MergeMap(_cachedMap);

            return _cachedMap;
        }
        finally
        {
            _mapCacheLock.Release();
        }
    }
}
