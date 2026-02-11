using MCP.Core.Configuration;
using MCP.Core.Models;
using MCP.Core.Services;

namespace MCP.Core.Services;

/// <summary>
/// Decorator over IProjectSkeletonService.
/// Intercepts AnalyzeCSharpFileAsync — checks Redis first, falls back to
/// live Roslyn parse on miss, then backfills the cache asynchronously.
/// All other methods pass straight through to the inner service.
///
/// Background indexer and file watcher both use ProjectSkeletonService
/// (concrete, not this decorator) so they always write fresh data to cache
/// without reading stale values from it.Testing file updte watcher
/// </summary>
public class CachedProjectSkeletonService : IProjectSkeletonService
{
    private readonly ProjectSkeletonService _inner;  // concrete — breaks circular dependency
    private readonly IAnalysisCacheService _cache;
    private readonly ILogger<CachedProjectSkeletonService> _logger;

    public CachedProjectSkeletonService(
        ProjectSkeletonService inner,               // concrete — not IProjectSkeletonService
        IAnalysisCacheService cache,
        ILogger<CachedProjectSkeletonService> logger)
    {
        _inner = inner;
        _cache = cache;
        _logger = logger;
    }

    public async Task<CSharpFileAnalysis> AnalyzeCSharpFileAsync(
        string projectName,
        string relativeFilePath,
        bool includePrivateMembers = false,
        CancellationToken cancellationToken = default)
    {
        // Background indexer always stores with includePrivateMembers=true,
        // so a cache hit always contains the full member set — safe to return
        // regardless of what the caller requested.
        var cached = await _cache.GetAsync(projectName, relativeFilePath);

        if (cached != null)
        {
            _logger.LogDebug("Cache HIT: {Project}:{Path}", projectName, relativeFilePath);
            return cached;
        }

        _logger.LogDebug("Cache MISS: {Project}:{Path} — falling back to live analysis",
            projectName, relativeFilePath);

        // Live analysis — always use includePrivateMembers=true so the
        // backfilled value is maximally useful for future callers
        var analysis = await _inner.AnalyzeCSharpFileAsync(
            projectName, relativeFilePath, includePrivateMembers: true, cancellationToken);

        // Fire-and-forget backfill — don't block the caller
        _ = Task.Run(async () =>
        {
            try
            {
                await _cache.SetAsync(projectName, relativeFilePath, analysis);
                await _cache.AddToIndexAsync(projectName, relativeFilePath);
                _logger.LogDebug("Cache backfilled: {Project}:{Path}", projectName, relativeFilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cache backfill failed: {Project}:{Path}", projectName, relativeFilePath);
            }
        }, CancellationToken.None);

        return analysis;
    }

    // ── All other methods delegate directly — no caching needed ──────────────

    public Task<FileContentResponse> ReadFileContentAsync(
        string projectName, string relativeFilePath, CancellationToken cancellationToken = default)
        => _inner.ReadFileContentAsync(projectName, relativeFilePath, cancellationToken);

    public Task<MethodImplementationInfo> FetchMethodImplementationAsync(
        string projectName, string relativeFilePath, string methodName,
        string? className = null, CancellationToken cancellationToken = default)
        => _inner.FetchMethodImplementationAsync(projectName, relativeFilePath, methodName, className, cancellationToken);

    public Task<List<MethodImplementationInfo>> FetchMethodImplementationsBatchAsync(
        string projectName, string relativeFilePath, string[] methodNames,
        string? className = null, CancellationToken cancellationToken = default)
        => _inner.FetchMethodImplementationsBatchAsync(projectName, relativeFilePath, methodNames, className, cancellationToken);

    public IReadOnlyDictionary<string, string> GetAvailableProjects()
        => _inner.GetAvailableProjects();

    public IReadOnlyDictionary<string, ProjectInfo> GetAvailableProjectsWithInfo()
        => _inner.GetAvailableProjectsWithInfo();

    public Task<string> GetProjectSkeletonAsync(
        string projectName, string? sinceTimestamp = null, CancellationToken cancellationToken = default)
        => _inner.GetProjectSkeletonAsync(projectName, sinceTimestamp, cancellationToken);

    public Task<FolderSearchResponse> SearchFolderFilesAsync(
        string projectName, string folderPath, string? searchPattern = null,
        int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
        => _inner.SearchFolderFilesAsync(projectName, folderPath, searchPattern, page, pageSize, cancellationToken);

    public string GetToolDescription()
        => _inner.GetToolDescription();
}