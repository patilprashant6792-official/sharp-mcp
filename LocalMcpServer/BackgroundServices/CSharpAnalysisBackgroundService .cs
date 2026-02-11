using MCP.Core.Configuration;
using MCP.Core.Services;
using Microsoft.Extensions.Options;

namespace MCP.Core.BackgroundServices;

/// <summary>
/// Continuously indexes all .cs files across all configured projects into Redis.
/// Runs a full pass on startup, then repeats every RefreshIntervalMinutes.
/// This ensures SearchCodeGlobally reads pre-analysed data instead of doing
/// live Roslyn parses per query.
/// </summary>
public class CSharpAnalysisBackgroundService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly AnalysisCacheConfig _config;
    private readonly ILogger<CSharpAnalysisBackgroundService> _logger;

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".vs", ".git", "node_modules", "packages" };

    public CSharpAnalysisBackgroundService(
        IServiceProvider sp,
        IOptions<AnalysisCacheConfig> config,
        ILogger<CSharpAnalysisBackgroundService> logger)
    {
        _sp = sp;
        _config = config.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Yield once so the host can finish startup before the CPU-heavy work begins
        await Task.Yield();

        _logger.LogInformation("CSharpAnalysisBackgroundService started. Refresh interval: {Minutes} min",
            _config.RefreshIntervalMinutes);

        while (!ct.IsCancellationRequested)
        {
            await RunFullIndexPassAsync(ct);

            if (ct.IsCancellationRequested) break;

            _logger.LogInformation("Analysis index pass complete. Next pass in {Minutes} min",
                _config.RefreshIntervalMinutes);

            await Task.Delay(TimeSpan.FromMinutes(_config.RefreshIntervalMinutes), ct)
                      .ContinueWith(_ => { }, CancellationToken.None); // swallow TaskCanceledException on shutdown
        }

        _logger.LogInformation("CSharpAnalysisBackgroundService stopping.");
    }

    private async Task RunFullIndexPassAsync(CancellationToken ct)
    {
        // Resolve scoped/singleton services fresh each pass
        using var scope = _sp.CreateScope();
        var configService = scope.ServiceProvider.GetRequiredService<IProjectConfigService>();
        // Use concrete type directly — bypasses CachedProjectSkeletonService decorator
        // so the indexer always writes fresh data to cache, never reads its own output
        var skeletonService = scope.ServiceProvider.GetRequiredService<ProjectSkeletonService>();
        var cache = scope.ServiceProvider.GetRequiredService<IAnalysisCacheService>();

        var projects = configService.LoadProjects().Projects
            .Where(p => p.Enabled)
            .ToList();

        if (projects.Count == 0)
        {
            _logger.LogDebug("No enabled projects found, skipping index pass.");
            return;
        }

        _logger.LogInformation("Starting index pass for {Count} project(s)", projects.Count);

        var sem = new SemaphoreSlim(_config.IndexingConcurrency);

        foreach (var project in projects)
        {
            if (ct.IsCancellationRequested) break;
            await IndexProjectAsync(project.Name, project.Path, skeletonService, cache, sem, ct);
        }
    }

    private async Task IndexProjectAsync(
        string projectName,
        string projectPath,
        IProjectSkeletonService skeletonService,
        IAnalysisCacheService cache,
        SemaphoreSlim sem,
        CancellationToken ct)
    {
        if (!Directory.Exists(projectPath))
        {
            _logger.LogWarning("Project path does not exist, skipping: {Path}", projectPath);
            return;
        }

        var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(f))
            .ToList();

        _logger.LogInformation("Indexing project '{Project}': {Count} .cs files found", projectName, csFiles.Count);

        var indexed = new List<string>();
        var failed = 0;

        var tasks = csFiles.Select(async filePath =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var rel = Path.GetRelativePath(projectPath, filePath);
                var analysis = await skeletonService.AnalyzeCSharpFileAsync(
                    projectName, rel, includePrivateMembers: true, ct);

                await cache.SetAsync(projectName, rel, analysis);

                lock (indexed) indexed.Add(rel);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                _logger.LogWarning(ex, "Failed to index {File} in project '{Project}'", filePath, projectName);
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Atomically replace the index for this project
        await cache.SetIndexAsync(projectName, indexed);

        _logger.LogInformation(
            "Project '{Project}' indexed: {Ok} ok, {Failed} failed, {Total} total",
            projectName, indexed.Count, failed, csFiles.Count);
    }

    private static bool IsExcluded(string filePath)
    {
        var parts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => ExcludedDirs.Contains(p));
    }
}