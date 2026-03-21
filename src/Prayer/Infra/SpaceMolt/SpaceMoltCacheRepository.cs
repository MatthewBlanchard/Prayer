using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

internal sealed class SpaceMoltCacheRepository
{
    private sealed class CatalogByIdSnapshot<TEntry> where TEntry : CatalogueEntry
    {
        public DateTime FetchedAtUtc { get; set; }
        public int EntryCount { get; set; }
        public Dictionary<string, TEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }

    public void LoadMarketCachesFromDisk(
        Dictionary<string, StationInfo> stationCache,
        TimeSpan marketCacheTtl)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.MarketsDir);
        }
        catch
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(AppPaths.MarketsDir, "*.json"))
        {
            try
            {
                string raw = File.ReadAllText(file);
                var snapshot = JsonSerializer.Deserialize<MarketCacheSnapshot>(raw);

                if (snapshot == null ||
                    string.IsNullOrWhiteSpace(snapshot.StationId) ||
                    snapshot.Market == null ||
                    snapshot.CapturedAtUtc == default)
                {
                    SafeDelete(file);
                    continue;
                }

                var age = DateTime.UtcNow - snapshot.CapturedAtUtc;
                if (age > marketCacheTtl)
                {
                    SafeDelete(file);
                    continue;
                }

                if (!stationCache.TryGetValue(snapshot.StationId, out var stationInfo))
                {
                    stationInfo = new StationInfo
                    {
                        StationId = snapshot.StationId
                    };
                    stationCache[snapshot.StationId] = stationInfo;
                }

                stationInfo.Market = snapshot.Market;
            }
            catch
            {
                SafeDelete(file);
            }
        }
    }

    public void LoadShipyardCachesFromDisk(
        Dictionary<string, StationInfo> stationCache,
        TimeSpan shipyardCacheTtl)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ShipyardsDir);
        }
        catch
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(AppPaths.ShipyardsDir, "*.json"))
        {
            try
            {
                string raw = File.ReadAllText(file);
                var snapshot = JsonSerializer.Deserialize<ShipyardCacheSnapshot>(raw);

                if (snapshot == null ||
                    string.IsNullOrWhiteSpace(snapshot.StationId) ||
                    snapshot.CapturedAtUtc == default)
                {
                    SafeDelete(file);
                    continue;
                }

                var age = DateTime.UtcNow - snapshot.CapturedAtUtc;
                if (age > shipyardCacheTtl)
                {
                    SafeDelete(file);
                    continue;
                }

                if (!stationCache.TryGetValue(snapshot.StationId, out var stationInfo))
                {
                    stationInfo = new StationInfo
                    {
                        StationId = snapshot.StationId
                    };
                    stationCache[snapshot.StationId] = stationInfo;
                }

                stationInfo.ShipyardShowroom = snapshot.Showroom ?? Array.Empty<ShipyardShowroomEntry>();
                stationInfo.ShipyardListings = snapshot.Listings ?? Array.Empty<ShipyardListingEntry>();
            }
            catch
            {
                SafeDelete(file);
            }
        }
    }

    public void LoadCatalogueCachesFromDisk(
        Dictionary<string, SpaceMoltCatalogueCacheEntry> catalogueCache,
        TimeSpan catalogueCacheTtl)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CatalogsDir);
        }
        catch
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(AppPaths.CatalogsDir, "*.json"))
        {
            try
            {
                DateTime capturedAtUtc = File.GetLastWriteTimeUtc(file);
                var age = DateTime.UtcNow - capturedAtUtc;
                if (age > catalogueCacheTtl)
                {
                    SafeDelete(file);
                    continue;
                }

                string raw = File.ReadAllText(file);
                JsonElement response = JsonSerializer.Deserialize<JsonElement>(raw);
                var catalogue = Catalogue.FromJson(response);
                string fileKey = Path.GetFileNameWithoutExtension(file);
                catalogueCache[fileKey] = new SpaceMoltCatalogueCacheEntry
                {
                    Catalogue = catalogue,
                    CachedAtUtc = capturedAtUtc
                };
            }
            catch
            {
                SafeDelete(file);
            }
        }
    }

    public void SaveMarketCacheToDisk(string stationId, MarketState market)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.MarketsDir);
            var snapshot = new MarketCacheSnapshot
            {
                StationId = stationId,
                CapturedAtUtc = DateTime.UtcNow,
                Market = market
            };

            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(GetMarketCachePath(stationId), json);
        }
        catch
        {
            // Ignore write failures; in-memory cache remains available.
        }
    }

    public void SaveShipyardCacheToDisk(
        string stationId,
        ShipyardShowroomEntry[] showroom,
        ShipyardListingEntry[] listings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ShipyardsDir);
            var snapshot = new ShipyardCacheSnapshot
            {
                StationId = stationId,
                CapturedAtUtc = DateTime.UtcNow,
                Showroom = showroom ?? Array.Empty<ShipyardShowroomEntry>(),
                Listings = listings ?? Array.Empty<ShipyardListingEntry>()
            };

            string json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(GetShipyardCachePath(stationId), json);
        }
        catch
        {
            // Ignore write failures; in-memory cache remains available.
        }
    }

    public void SaveCatalogueCacheToDisk(
        string fileKey,
        string rawResponse,
        Catalogue catalogue,
        Dictionary<string, SpaceMoltCatalogueCacheEntry> catalogueCache)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CatalogsDir);
            catalogueCache[fileKey] = new SpaceMoltCatalogueCacheEntry
            {
                CachedAtUtc = DateTime.UtcNow,
                Catalogue = catalogue ?? new Catalogue()
            };

            File.WriteAllText(GetCatalogueCachePath(fileKey), rawResponse);
        }
        catch
        {
            // Ignore write failures; in-memory cache remains available.
        }
    }

    public void DeleteCatalogueCache(
        string fileKey,
        Dictionary<string, SpaceMoltCatalogueCacheEntry>? catalogueCache = null)
    {
        catalogueCache?.Remove(fileKey);
        SafeDelete(GetCatalogueCachePath(fileKey));
    }

    public void SaveCatalogByIdCacheToDisk<TEntry>(
        string cachePath,
        IReadOnlyDictionary<string, TEntry> byId,
        DateTime fetchedAtUtc)
        where TEntry : CatalogueEntry
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDir);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var snapshot = new CatalogByIdSnapshot<TEntry>
            {
                FetchedAtUtc = fetchedAtUtc,
                EntryCount = byId.Count,
                Entries = new Dictionary<string, TEntry>(byId, StringComparer.Ordinal)
            };

            string json = JsonSerializer.Serialize(snapshot, options);
            File.WriteAllText(cachePath, json);
        }
        catch
        {
            // Ignore write failures; in-memory data remains available.
        }
    }

    public bool TryLoadCatalogByIdCacheFromDisk<TEntry>(
        string cachePath,
        out Dictionary<string, TEntry> byId,
        out DateTime fetchedAtUtc)
        where TEntry : CatalogueEntry
    {
        byId = new Dictionary<string, TEntry>(StringComparer.Ordinal);
        fetchedAtUtc = default;

        try
        {
            if (!File.Exists(cachePath))
                return false;

            string raw = File.ReadAllText(cachePath);
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var snapshot = JsonSerializer.Deserialize<CatalogByIdSnapshot<TEntry>>(raw);
            if (snapshot != null &&
                snapshot.FetchedAtUtc != default &&
                snapshot.Entries != null &&
                snapshot.Entries.Count > 0)
            {
                byId = new Dictionary<string, TEntry>(snapshot.Entries, StringComparer.Ordinal);
                fetchedAtUtc = snapshot.FetchedAtUtc;
                return true;
            }

            var legacy = JsonSerializer.Deserialize<Dictionary<string, TEntry>>(raw);
            if (legacy != null && legacy.Count > 0)
            {
                byId = new Dictionary<string, TEntry>(legacy, StringComparer.Ordinal);
                fetchedAtUtc = File.GetLastWriteTimeUtc(cachePath);
                return true;
            }
        }
        catch
        {
            // Fall through to false.
        }

        return false;
    }

    public static string BuildCatalogueCacheKey(
        string type,
        string? category,
        string? id,
        int? page,
        int? pageSize,
        string? search)
    {
        return string.Join("|",
            type ?? "",
            category ?? "",
            id ?? "",
            page?.ToString() ?? "",
            pageSize?.ToString() ?? "",
            search ?? "");
    }

    public static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown_station";

        var chars = value
            .Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_')
            .ToArray();

        return new string(chars);
    }

    private static string GetMarketCachePath(string stationId)
    {
        return Path.Combine(AppPaths.MarketsDir, SanitizeFileName(stationId) + ".json");
    }

    private static string GetShipyardCachePath(string stationId)
    {
        return Path.Combine(AppPaths.ShipyardsDir, SanitizeFileName(stationId) + ".json");
    }

    private static string GetCatalogueCachePath(string fileKey)
    {
        return Path.Combine(AppPaths.CatalogsDir, fileKey + ".json");
    }

    private static void SafeDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }
}
