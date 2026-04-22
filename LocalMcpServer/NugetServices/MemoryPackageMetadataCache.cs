using Microsoft.Extensions.Caching.Memory;

namespace NuGetExplorer.Services;

public class MemoryPackageMetadataCache : IPackageMetadataCache
{
    private readonly IMemoryCache _cache;
    private readonly MemoryCacheEntryOptions _cacheOptions;

    public MemoryPackageMetadataCache(IMemoryCache cache)
    {
        _cache = cache;
        _cacheOptions = new MemoryCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromHours(24),
            AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(7)
        };
    }

    public PackageMetadata? Get(string key)
        => _cache.Get<PackageMetadata>(key);

    public void Set(string key, PackageMetadata metadata)
        => _cache.Set(key, metadata, _cacheOptions);

    public bool TryGet(string key, out PackageMetadata? metadata)
        => _cache.TryGetValue(key, out metadata);

    // In-process memory — no I/O, Task.FromResult is correct here.
    public Task<PackageMetadata?> TryGetAsync(string key)
        => Task.FromResult(_cache.Get<PackageMetadata>(key));

    public Task SetAsync(string key, PackageMetadata metadata)
    {
        _cache.Set(key, metadata, _cacheOptions);
        return Task.CompletedTask;
    }
}
