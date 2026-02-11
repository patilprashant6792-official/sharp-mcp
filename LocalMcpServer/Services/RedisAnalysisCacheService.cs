using MCP.Core.Configuration;
using MCP.Core.Services;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using System.Text.Json;

namespace MCP.Core.Services;

public class RedisAnalysisCacheService : IAnalysisCacheService
{
    private readonly IDatabase _db;
    private readonly TimeSpan _ttl;
    private readonly ILogger<RedisAnalysisCacheService> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    // Key patterns
    // mcp:analysis:{project}:{normalizedPath}  → CSharpFileAnalysis JSON
    // mcp:analysis:index:{project}             → JSON array of relative paths
    private static string AnalysisKey(string project, string path) =>
        $"mcp:analysis:{Normalize(project)}:{Normalize(path)}";

    private static string IndexKey(string project) =>
        $"mcp:analysis:index:{Normalize(project)}";

    private static string Normalize(string s) =>
        s.Trim().ToLowerInvariant().Replace('\\', '/').TrimStart('/');

    public RedisAnalysisCacheService(
        IConnectionMultiplexer redis,
        IOptions<AnalysisCacheConfig> options,
        ILogger<RedisAnalysisCacheService> logger)
    {
        _db = redis.GetDatabase();
        _ttl = TimeSpan.FromHours(options.Value.TtlHours);
        _logger = logger;
    }

    public async Task<CSharpFileAnalysis?> GetAsync(string projectName, string relativePath)
    {
        try
        {
            var val = await _db.StringGetAsync(AnalysisKey(projectName, relativePath));
            if (val.IsNullOrEmpty) return null;
            return JsonSerializer.Deserialize<CSharpFileAnalysis>(val.ToString(), _json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache GET failed for {Project}:{Path}", projectName, relativePath);
            return null;
        }
    }

    public async Task SetAsync(string projectName, string relativePath, CSharpFileAnalysis analysis)
    {
        try
        {
            var json = JsonSerializer.Serialize(analysis, _json);
            await _db.StringSetAsync(AnalysisKey(projectName, relativePath), json, _ttl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache SET failed for {Project}:{Path}", projectName, relativePath);
        }
    }

    public async Task DeleteAsync(string projectName, string relativePath)
    {
        try
        {
            await _db.KeyDeleteAsync(AnalysisKey(projectName, relativePath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache DELETE failed for {Project}:{Path}", projectName, relativePath);
        }
    }

    public async Task<IReadOnlyList<string>> GetIndexAsync(string projectName)
    {
        try
        {
            var val = await _db.StringGetAsync(IndexKey(projectName));
            if (val.IsNullOrEmpty) return [];
            return JsonSerializer.Deserialize<List<string>>(val.ToString(), _json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache GET index failed for {Project}", projectName);
            return [];
        }
    }

    public async Task SetIndexAsync(string projectName, IEnumerable<string> relativePaths)
    {
        try
        {
            var json = JsonSerializer.Serialize(relativePaths.ToList(), _json);
            // Index has no TTL — it's the authoritative list; individual entries expire on their own
            await _db.StringSetAsync(IndexKey(projectName), json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache SET index failed for {Project}", projectName);
        }
    }

    public async Task RemoveFromIndexAsync(string projectName, string relativePath)
    {
        try
        {
            var index = (await GetIndexAsync(projectName)).ToList();
            var normalized = Normalize(relativePath);
            if (index.Remove(normalized))
                await SetIndexAsync(projectName, index);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache REMOVE from index failed for {Project}:{Path}", projectName, relativePath);
        }
    }

    public async Task AddToIndexAsync(string projectName, string relativePath)
    {
        try
        {
            var index = (await GetIndexAsync(projectName)).ToList();
            var normalized = Normalize(relativePath);
            if (!index.Contains(normalized))
            {
                index.Add(normalized);
                await SetIndexAsync(projectName, index);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache ADD to index failed for {Project}:{Path}", projectName, relativePath);
        }
    }

    public async Task PurgeProjectAsync(string projectName)
    {
        try
        {
            // Use the index to find every analysis key — avoids a KEYS scan on Redis
            var index = await GetIndexAsync(projectName);

            var keys = index
                .Select(p => (RedisKey)AnalysisKey(projectName, p))
                .Append((RedisKey)IndexKey(projectName))
                .ToArray();

            if (keys.Length > 0)
                await _db.KeyDeleteAsync(keys);

            _logger.LogInformation(
                "Purged {Count} cache keys for project '{Project}'", keys.Length, projectName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache PURGE failed for project '{Project}'", projectName);
        }
    }
}