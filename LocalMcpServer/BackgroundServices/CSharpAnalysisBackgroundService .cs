using MCP.Core.Configuration;
using MCP.Core.Services;
using Microsoft.Extensions.Options;

namespace MCP.Core.BackgroundServices;

public class CSharpAnalysisBackgroundService : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly AnalysisCacheConfig _config;
    private readonly IAnalysisTriggerService _trigger;
    private readonly ILogger<CSharpAnalysisBackgroundService> _logger;

    private static readonly HashSet<string> ExcludedDirs =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules" };

    public CSharpAnalysisBackgroundService(
        IServiceProvider sp,
        IOptions<AnalysisCacheConfig> config,
        IAnalysisTriggerService trigger,
        ILogger<CSharpAnalysisBackgroundService> logger)
    {
        _sp = sp;
        _config = config.Value;
        _trigger = trigger;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Background indexer starting");

        // Drain on-demand triggers concurrently with the scheduled full pass.
        // A separate loop handles triggers so the scheduled timer is never blocked.
        var triggerLoop = Task.Run(() => RunTriggerLoopAsync(ct), ct);
        var scheduledLoop = Task.Run(() => RunScheduledLoopAsync(ct), ct);

        await Task.WhenAll(triggerLoop, scheduledLoop);

        _logger.LogInformation("Background indexer stopped");
    }

    // ── Scheduled full-pass loop ─────────────────────────────────────────────

    private async Task RunScheduledLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunFullIndexPassAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled index pass failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(_config.RefreshIntervalMinutes), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── On-demand trigger loop ───────────────────────────────────────────────

    private async Task RunTriggerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var projectName = await _trigger.WaitForTriggerAsync(ct);
            if (projectName is null) break; // cancelled

            // Drain any duplicate triggers queued for the same project
            // (e.g., rapid add + update before indexing starts)
            var batch = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { projectName };
            while (TryDrainNext(out var next))
                batch.Add(next!);

            _logger.LogInformation(
                "On-demand indexing triggered for {Count} project(s): {Names}",
                batch.Count, string.Join(", ", batch));

            using var scope = _sp.CreateScope();
            var skeletonService = scope.ServiceProvider.GetRequiredService<ProjectSkeletonService>();
            var cache = scope.ServiceProvider.GetRequiredService<IAnalysisCacheService>();
            var configService = scope.ServiceProvider.GetRequiredService<IProjectConfigService>();

            // Only index projects that are still configured and enabled
            var allProjects = configService.LoadProjects().Projects
                .Where(p => p.Enabled && batch.Contains(p.Name))
                .ToList();

            if (allProjects.Count == 0)
            {
                _logger.LogWarning("On-demand trigger: none of the requested projects are enabled/found");
                continue;
            }

            var sem = new SemaphoreSlim(_config.IndexingConcurrency);
            var tasks = allProjects.Select(p =>
                IndexProjectAsync(p.Name, p.Path, skeletonService, cache, sem, ct));

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "On-demand index pass failed");
            }
        }
    }

    /// <summary>Non-blocking drain of any already-queued trigger names.</summary>
    private bool TryDrainNext([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name)
    {
        // IAnalysisTriggerService doesn't expose TryRead directly, but we can
        // check via a zero-timeout task — simplest is to cast to the concrete type.
        // To keep the interface clean we expose a sync TryRead on the implementation.
        if (_trigger is AnalysisTriggerService concrete)
            return concrete.TryRead(out name);

        name = null;
        return false;
    }

    // ── Full pass ────────────────────────────────────────────────────────────

    private async Task RunFullIndexPassAsync(CancellationToken ct)
    {
        using var scope = _sp.CreateScope();
        var configService = scope.ServiceProvider.GetRequiredService<IProjectConfigService>();
        var skeletonService = scope.ServiceProvider.GetRequiredService<ProjectSkeletonService>();
        var cache = scope.ServiceProvider.GetRequiredService<IAnalysisCacheService>();

        var projects = configService.LoadProjects().Projects
            .Where(p => p.Enabled)
            .ToList();

        if (projects.Count == 0)
        {
            _logger.LogDebug("No enabled projects found, skipping index pass");
            return;
        }

        _logger.LogInformation("Starting scheduled index pass for {Count} project(s)", projects.Count);

        var sem = new SemaphoreSlim(_config.IndexingConcurrency);
        var tasks = projects.Select(p =>
            IndexProjectAsync(p.Name, p.Path, skeletonService, cache, sem, ct));

        await Task.WhenAll(tasks);
    }

    // ── Per-project indexing (unchanged logic) ───────────────────────────────

    private async Task IndexProjectAsync(
        string projectName,
        string projectPath,
        ProjectSkeletonService skeletonService,
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

                var methodsAlreadyCached = await cache.MethodsExistAsync(projectName, rel);

                var setAnalysisTask = cache.SetAsync(projectName, rel, analysis);
                var setMethodsTask = methodsAlreadyCached
                    ? Task.CompletedTask
                    : IndexMethodsAsync(projectName, rel, filePath, analysis, cache, ct);

                await Task.WhenAll(setAnalysisTask, setMethodsTask);
                lock (indexed) indexed.Add(rel);
            }
            catch (OperationCanceledException) { throw; }
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
        await cache.SetIndexAsync(projectName, indexed);

        _logger.LogInformation(
            "Project '{Project}' indexed: {Ok} ok, {Failed} failed, {Total} total",
            projectName, indexed.Count, failed, csFiles.Count);
    }

    private static async Task IndexMethodsAsync(
        string projectName,
        string relativePath,
        string fullPath,
        CSharpFileAnalysis analysis,
        IAnalysisCacheService cache,
        CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(fullPath, ct);
        var dict = new Dictionary<string, MethodImplementationInfo>();

        foreach (var classInfo in analysis.Classes)
        {
            foreach (var method in classInfo.Methods)
            {
                var startIdx = Math.Max(0, method.LineNumberStart - 1);
                var endIdx = Math.Min(lines.Length - 1, method.LineNumberEnd - 1);
                var fullMethodCode = string.Join(Environment.NewLine, lines[startIdx..(endIdx + 1)]);

                var bodyStartOffset = Array.FindIndex(
                    lines, startIdx, endIdx - startIdx + 1,
                    l => l.TrimEnd().EndsWith('{'));

                var methodBody = bodyStartOffset >= 0
                    ? string.Join(Environment.NewLine, lines[(bodyStartOffset + 1)..(endIdx + 1)])
                    : string.Empty;

                var info = new MethodImplementationInfo
                {
                    ProjectName = projectName,
                    FilePath = relativePath,
                    ClassName = classInfo.Name,
                    Namespace = analysis.Namespace,
                    MethodName = method.Name,
                    FullSignature = method.Name,
                    ReturnType = method.ReturnType,
                    Modifiers = method.Modifiers,
                    Parameters = method.Parameters,
                    Attributes = method.Attributes,
                    XmlDocumentation = method.XmlDocumentation,
                    MethodBody = methodBody,
                    FullMethodCode = fullMethodCode,
                    LineNumber = method.LineNumberStart,
                    IsAsync = method.Modifiers.Contains("async"),
                    IsStatic = method.Modifiers.Contains("static"),
                    IsVirtual = method.Modifiers.Contains("virtual"),
                    IsOverride = method.Modifiers.Contains("override"),
                    IsAbstract = method.Modifiers.Contains("abstract")
                };

                var key = $"{classInfo.Name}::{method.Name}::{method.LineNumberStart}";
                dict[key] = info;
            }
        }

        await cache.SetMethodsAsync(projectName, relativePath, dict);
    }

    private static bool IsExcluded(string filePath)
        => ExcludedDirs.Any(d => filePath.Split(Path.DirectorySeparatorChar).Contains(d));
}