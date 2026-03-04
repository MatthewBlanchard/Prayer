using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

internal sealed class SpaceMoltCatalogService
{
    internal const string FullShipCatalogueCacheFileKey = "ships_full_catalog";
    internal const string FullItemCatalogueCacheFileKey = "items_full_catalog";

    private readonly Func<string, object?, Task<JsonElement>> _executeAsync;
    private readonly SpaceMoltCacheRepository _cacheRepository;
    private readonly TimeSpan _catalogueCacheTtl;
    private readonly int _catalogFetchPageSize;
    private readonly Dictionary<string, SpaceMoltCatalogueCacheEntry> _catalogueCache
        = new(StringComparer.Ordinal);

    private Dictionary<string, CatalogueEntry>? _itemCatalogByIdCache;
    private DateTime? _itemCatalogByIdFetchedAtUtc;
    private Dictionary<string, CatalogueEntry>? _shipCatalogByIdCache;
    private DateTime? _shipCatalogByIdFetchedAtUtc;

    public SpaceMoltCatalogService(
        Func<string, object?, Task<JsonElement>> executeAsync,
        SpaceMoltCacheRepository cacheRepository,
        TimeSpan catalogueCacheTtl,
        int catalogFetchPageSize)
    {
        _executeAsync = executeAsync;
        _cacheRepository = cacheRepository;
        _catalogueCacheTtl = catalogueCacheTtl;
        _catalogFetchPageSize = catalogFetchPageSize;
    }

    public IReadOnlyDictionary<string, SpaceMoltCatalogueCacheEntry> CatalogueCache => _catalogueCache;

    public void LoadCachesFromDisk()
    {
        _cacheRepository.LoadCatalogueCachesFromDisk(_catalogueCache, _catalogueCacheTtl);
    }

    public void PromoteCachedCatalogState()
    {
        if (_cacheRepository.TryLoadCatalogByIdCacheFromDisk(
            AppPaths.ItemCatalogByIdCacheFile,
            out var byId,
            out var fetchedAtUtc))
        {
            _itemCatalogByIdCache = byId;
            _itemCatalogByIdFetchedAtUtc = fetchedAtUtc;
            GalaxyStateHub.MergeItemCatalog(byId);
        }

        if (_cacheRepository.TryLoadCatalogByIdCacheFromDisk(
            AppPaths.ShipCatalogByIdCacheFile,
            out var shipById,
            out var shipFetchedAtUtc))
        {
            _shipCatalogByIdCache = shipById;
            _shipCatalogByIdFetchedAtUtc = shipFetchedAtUtc;
            GalaxyStateHub.MergeShipCatalog(shipById);
        }
    }

    public bool TryGetCachedCatalogue(string fileKey, out SpaceMoltCatalogueCacheEntry entry)
    {
        return _catalogueCache.TryGetValue(fileKey, out entry!);
    }

    public async Task EnsureFreshCataloguesAsync()
    {
        try
        {
            await GetFullItemCatalogByIdAsync(forceRefresh: false);
        }
        catch
        {
            // Best-effort: state refresh should not fail because catalog refresh failed.
        }

        try
        {
            await GetFullShipCatalogByIdAsync(forceRefresh: false);
        }
        catch
        {
            // Best-effort: state refresh should not fail because catalog refresh failed.
        }
    }

    public async Task<IReadOnlyDictionary<string, CatalogueEntry>> GetFullItemCatalogByIdAsync(
        bool forceRefresh = false)
    {
        if (!forceRefresh &&
            _itemCatalogByIdCache != null &&
            _itemCatalogByIdCache.Count > 0 &&
            _itemCatalogByIdFetchedAtUtc.HasValue)
        {
            var age = DateTime.UtcNow - _itemCatalogByIdFetchedAtUtc.Value;
            if (age <= _catalogueCacheTtl)
            {
                GalaxyStateHub.MergeItemCatalog(_itemCatalogByIdCache);
                return _itemCatalogByIdCache;
            }
        }

        if (!forceRefresh &&
            _cacheRepository.TryLoadCatalogByIdCacheFromDisk(
                AppPaths.ItemCatalogByIdCacheFile,
                out var diskCached,
                out var diskFetchedAtUtc))
        {
            var age = DateTime.UtcNow - diskFetchedAtUtc;
            if (age <= _catalogueCacheTtl)
            {
                _itemCatalogByIdCache = diskCached;
                _itemCatalogByIdFetchedAtUtc = diskFetchedAtUtc;
                GalaxyStateHub.MergeItemCatalog(diskCached);
                return diskCached;
            }
        }

        Catalogue fullCatalogue = await GetOrRefreshFullCatalogueAsync(
            type: "items",
            fullCacheFileKey: FullItemCatalogueCacheFileKey,
            forceRefresh: forceRefresh);

        var dictionary = BuildCatalogById(fullCatalogue.NormalizedEntries);
        var fetchedAtUtc = DateTime.UtcNow;
        _cacheRepository.SaveCatalogByIdCacheToDisk(AppPaths.ItemCatalogByIdCacheFile, dictionary, fetchedAtUtc);
        _itemCatalogByIdCache = dictionary;
        _itemCatalogByIdFetchedAtUtc = fetchedAtUtc;
        GalaxyStateHub.MergeItemCatalog(dictionary);
        return dictionary;
    }

    public async Task<IReadOnlyDictionary<string, CatalogueEntry>> GetFullShipCatalogByIdAsync(
        bool forceRefresh = false)
    {
        if (!forceRefresh &&
            _shipCatalogByIdCache != null &&
            _shipCatalogByIdCache.Count > 0 &&
            _shipCatalogByIdFetchedAtUtc.HasValue)
        {
            var age = DateTime.UtcNow - _shipCatalogByIdFetchedAtUtc.Value;
            if (age <= _catalogueCacheTtl)
            {
                GalaxyStateHub.MergeShipCatalog(_shipCatalogByIdCache);
                return _shipCatalogByIdCache;
            }
        }

        if (!forceRefresh &&
            _cacheRepository.TryLoadCatalogByIdCacheFromDisk(
                AppPaths.ShipCatalogByIdCacheFile,
                out var diskCached,
                out var diskFetchedAtUtc))
        {
            var age = DateTime.UtcNow - diskFetchedAtUtc;
            if (age <= _catalogueCacheTtl)
            {
                _shipCatalogByIdCache = diskCached;
                _shipCatalogByIdFetchedAtUtc = diskFetchedAtUtc;
                GalaxyStateHub.MergeShipCatalog(diskCached);
                return diskCached;
            }
        }

        Catalogue fullCatalogue = await GetOrRefreshFullCatalogueAsync(
            type: "ships",
            fullCacheFileKey: FullShipCatalogueCacheFileKey,
            forceRefresh: forceRefresh);

        var dictionary = BuildCatalogById(fullCatalogue.NormalizedEntries);
        var fetchedAtUtc = DateTime.UtcNow;
        _cacheRepository.SaveCatalogByIdCacheToDisk(AppPaths.ShipCatalogByIdCacheFile, dictionary, fetchedAtUtc);
        _shipCatalogByIdCache = dictionary;
        _shipCatalogByIdFetchedAtUtc = fetchedAtUtc;
        GalaxyStateHub.MergeShipCatalog(dictionary);
        return dictionary;
    }

    public async Task<Catalogue> GetCatalogueAsync(
        string type,
        string? category = null,
        string? id = null,
        int? page = null,
        int? pageSize = null,
        string? search = null)
    {
        string key = SpaceMoltCacheRepository.BuildCatalogueCacheKey(type, category, id, page, pageSize, search);
        string fileKey = SpaceMoltCacheRepository.SanitizeFileName(key);
        if (_catalogueCache.TryGetValue(fileKey, out var cached))
        {
            var age = DateTime.UtcNow - cached.CachedAtUtc;
            if (age <= _catalogueCacheTtl)
            {
                if (string.Equals(type, "items", StringComparison.OrdinalIgnoreCase))
                {
                    await SpaceMoltHttpLogging.LogItemCatalogAsync(
                        type,
                        category,
                        id,
                        page,
                        pageSize,
                        search,
                        "cache",
                        JsonSerializer.Serialize(cached.Catalogue));
                }

                return cached.Catalogue;
            }

            _cacheRepository.DeleteCatalogueCache(fileKey, _catalogueCache);
        }

        JsonElement result = await _executeAsync(
            "catalog",
            new
            {
                type,
                category,
                id,
                page,
                page_size = pageSize,
                search
            });

        Catalogue catalogue = Catalogue.FromJson(result);
        _cacheRepository.SaveCatalogueCacheToDisk(fileKey, result.GetRawText(), catalogue, _catalogueCache);
        if (string.Equals(type, "items", StringComparison.OrdinalIgnoreCase))
        {
            await SpaceMoltHttpLogging.LogItemCatalogAsync(
                type,
                category,
                id,
                page,
                pageSize,
                search,
                "api",
                result.GetRawText());
        }

        return catalogue;
    }

    public async Task<Catalogue> GetFullShipCatalogueAsync(bool forceRefresh = false)
    {
        return await GetOrRefreshFullCatalogueAsync(
            type: "ships",
            fullCacheFileKey: FullShipCatalogueCacheFileKey,
            forceRefresh: forceRefresh);
    }

    public static Catalogue BuildShipCatalogPage(Catalogue fullCatalogue, int requestedPage, int pageSize)
    {
        var entries = fullCatalogue.NormalizedEntries;
        int total = fullCatalogue.Total ?? fullCatalogue.TotalItems ?? entries.Length;
        if (total <= 0)
            total = entries.Length;

        int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)Math.Max(1, pageSize)));
        int page = Math.Clamp(requestedPage, 1, totalPages);
        int skip = (page - 1) * pageSize;
        var pageEntries = entries.Skip(skip).Take(pageSize).ToArray();

        return new Catalogue
        {
            Type = "ships",
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Total = total,
            TotalItems = total,
            Items = pageEntries
        };
    }

    private async Task<Catalogue> GetOrRefreshFullCatalogueAsync(
        string type,
        string fullCacheFileKey,
        bool forceRefresh)
    {
        if (!forceRefresh &&
            _catalogueCache.TryGetValue(fullCacheFileKey, out var cached))
        {
            var age = DateTime.UtcNow - cached.CachedAtUtc;
            if (age <= _catalogueCacheTtl && cached.Catalogue.NormalizedEntries.Length > 0)
                return cached.Catalogue;
        }

        return await FetchFullCatalogueFromApiAsync(type, fullCacheFileKey);
    }

    private async Task<Catalogue> FetchFullCatalogueFromApiAsync(string type, string fullCacheFileKey)
    {
        var firstResult = await _executeAsync(
            "catalog",
            new
            {
                type,
                page = 1,
                page_size = _catalogFetchPageSize
            });

        Catalogue firstPage = Catalogue.FromJson(firstResult);
        int totalPages = Math.Max(1, firstPage.TotalPages ?? 1);
        int pageSize = Math.Clamp(firstPage.PageSize ?? _catalogFetchPageSize, 1, _catalogFetchPageSize);

        var allEntries = firstPage.NormalizedEntries.ToList();

        for (int page = 2; page <= totalPages; page++)
        {
            var pageResult = await _executeAsync(
                "catalog",
                new
                {
                    type,
                    page,
                    page_size = pageSize
                });

            Catalogue parsedPage = Catalogue.FromJson(pageResult);
            allEntries.AddRange(parsedPage.NormalizedEntries);
        }

        var fullCatalogue = new Catalogue
        {
            Type = type,
            Page = 1,
            PageSize = allEntries.Count,
            TotalPages = 1,
            Total = allEntries.Count,
            TotalItems = allEntries.Count,
            Items = allEntries.ToArray()
        };

        string serialized = JsonSerializer.Serialize(fullCatalogue);
        _cacheRepository.SaveCatalogueCacheToDisk(fullCacheFileKey, serialized, fullCatalogue, _catalogueCache);
        return fullCatalogue;
    }

    private static Dictionary<string, CatalogueEntry> BuildCatalogById(IEnumerable<CatalogueEntry> entries)
    {
        var map = new Dictionary<string, CatalogueEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Id))
                continue;

            map[entry.Id] = entry;
        }

        return map;
    }
}
