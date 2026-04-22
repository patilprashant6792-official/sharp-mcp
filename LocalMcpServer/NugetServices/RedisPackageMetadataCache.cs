using Microsoft.Extensions.Logging;
using System.Text.Json;
using StackExchange.Redis;

namespace NuGetExplorer.Services;

public class RedisPackageMetadataCache : IPackageMetadataCache, IDisposable
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly TimeSpan _expiration;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<RedisPackageMetadataCache> _logger;

    public RedisPackageMetadataCache(
        IConnectionMultiplexer redis,
        ILogger<RedisPackageMetadataCache> logger,
        TimeSpan? expiration = null)
    {
        _redis   = redis   ?? throw new ArgumentNullException(nameof(redis));
        _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
        _db      = _redis.GetDatabase();
        _expiration = expiration ?? TimeSpan.FromDays(7);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    // -------------------------------------------------------------------------
    // Async — preferred; used by NuGetPackageLoader
    // -------------------------------------------------------------------------

    public async Task<PackageMetadata?> TryGetAsync(string key)
    {
        try
        {
            var value = await _db.StringGetAsync(key);
            if (value.IsNullOrEmpty) return null;
            return JsonSerializer.Deserialize<PackageMetadata>((string)value!, _jsonOptions);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis read failed for key {Key}", key);
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Deserialization failed for cache key {Key} — evicting", key);
            try { await _db.KeyDeleteAsync(key); } catch { /* best-effort eviction */ }
            return null;
        }
    }

    public async Task SetAsync(string key, PackageMetadata metadata)
    {
        try
        {
            var json = JsonSerializer.Serialize(metadata, _jsonOptions);
            await _db.StringSetAsync(key, json, _expiration);
        }
        catch (RedisException ex)
        {
            _logger.LogWarning(ex, "Redis write failed for key {Key} — next request will reload", key);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Serialization failed for cache key {Key}", key);
        }
    }

    // -------------------------------------------------------------------------
    // Sync — kept for interface compat, delegates to async
    // -------------------------------------------------------------------------

    public bool TryGet(string key, out PackageMetadata? metadata)
    {
        metadata = TryGetAsync(key).GetAwaiter().GetResult();
        return metadata != null;
    }

    public void Set(string key, PackageMetadata metadata)
        => SetAsync(key, metadata).GetAwaiter().GetResult();

    public void Dispose()
    {
        // Multiplexer lifetime is managed by DI (registered as singleton externally).
        // Do not dispose here — other services share the same connection.
    }
}
