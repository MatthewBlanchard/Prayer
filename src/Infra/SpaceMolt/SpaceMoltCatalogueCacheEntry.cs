using System;

internal sealed class SpaceMoltCatalogueCacheEntry
{
    public Catalogue Catalogue { get; init; } = new();
    public DateTime CachedAtUtc { get; init; }
}
