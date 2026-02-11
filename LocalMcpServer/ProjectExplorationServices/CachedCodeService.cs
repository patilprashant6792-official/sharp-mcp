using MCP.Core.Models;
using MCP.Core.Services;
using System.Diagnostics;

namespace MCP.Core.Services;

/// <summary>
/// Drop-in replacement for CodeSearchService.
/// Reads pre-analysed CSharpFileAnalysis from Redis instead of doing live
/// Roslyn parses per query. Falls back to live analysis on cache miss so
/// searches never fail during initial warm-up.
/// </summary>
public class CachedCodeSearchService : ICodeSearchService
{
    private readonly IAnalysisCacheService _cache;
    private readonly IProjectSkeletonService _skeleton;
    private readonly ICodeSearchService _fallback;
    private readonly ILogger<CachedCodeSearchService> _logger;

    public CachedCodeSearchService(
        IAnalysisCacheService cache,
        IProjectSkeletonService skeleton,
        CodeSearchService fallback,               // concrete type injected for fallback
        ILogger<CachedCodeSearchService> logger)
    {
        _cache = cache;
        _skeleton = skeleton;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<CodeSearchResponse> SearchGloballyAsync(
        CodeSearchRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Search query cannot be empty", nameof(request));

        var sw = Stopwatch.StartNew();
        var allResults = new List<CodeSearchResult>();
        var filesScanned = 0;

        var projects = _skeleton.GetAvailableProjectsWithInfo();
        var projectsToSearch = request.ProjectName == "*"
            ? projects.Keys.ToList()
            : [request.ProjectName];

        _logger.LogInformation("CachedSearch: {Count} project(s) for '{Query}'",
            projectsToSearch.Count, request.Query);

        foreach (var projectName in projectsToSearch)
        {
            if (ct.IsCancellationRequested) break;

            var (results, scanned) = await SearchProjectAsync(projectName, request, ct);
            allResults.AddRange(results);
            filesScanned += scanned;
        }

        var ranked = RankAndFilter(allResults, request);
        sw.Stop();

        return new CodeSearchResponse
        {
            Query = request.Query,
            ProjectName = request.ProjectName,
            TotalResults = allResults.Count,
            Results = ranked.Take(request.TopK).ToList(),
            SearchDuration = sw.Elapsed,
            FilesScanned = filesScanned,
            ProjectsSearched = projectsToSearch.Count
        };
    }

    // ── Per-project search ────────────────────────────────────────────────────

    private async Task<(List<CodeSearchResult> Results, int FilesScanned)> SearchProjectAsync(
        string projectName,
        CodeSearchRequest request,
        CancellationToken ct)
    {
        var index = await _cache.GetIndexAsync(projectName);

        if (index.Count == 0)
        {
            _logger.LogWarning(
                "No cache index for '{Project}' — falling back to live search", projectName);
            var fallbackReq = new CodeSearchRequest
            {
                ProjectName = projectName,
                Query = request.Query,
                MemberType = request.MemberType,
                CaseSensitive = request.CaseSensitive,
                TopK = request.TopK
            };
            var fallbackResp = await _fallback.SearchGloballyAsync(fallbackReq, ct);
            return (fallbackResp.Results, fallbackResp.FilesScanned);
        }

        var results = new List<CodeSearchResult>();
        var scanned = 0;
        var liveFallbacks = 0;

        // Fetch all cached analyses in parallel (Redis GET is I/O, not CPU)
        var fetchTasks = index.Select(async relPath =>
        {
            var analysis = await _cache.GetAsync(projectName, relPath);

            if (analysis == null)
            {
                // Cache miss (TTL expired or race) — live fallback + async backfill
                Interlocked.Increment(ref liveFallbacks);
                analysis = await LiveAnalyseAndBackfillAsync(projectName, relPath, ct);
            }

            return analysis;
        });

        var analyses = await Task.WhenAll(fetchTasks);

        foreach (var analysis in analyses)
        {
            if (analysis == null || ct.IsCancellationRequested) continue;

            var fileResults = SearchInAnalysis(analysis, request);
            foreach (var r in fileResults)
                r.ProjectName = projectName;

            results.AddRange(fileResults);
            scanned++;
        }

        if (liveFallbacks > 0)
            _logger.LogInformation(
                "Project '{Project}': {Miss} cache misses required live analysis", projectName, liveFallbacks);

        _logger.LogInformation(
            "CachedSearch '{Project}': {Results} results from {Scanned} files",
            projectName, results.Count, scanned);

        return (results, scanned);
    }

    private async Task<CSharpFileAnalysis?> LiveAnalyseAndBackfillAsync(
        string projectName, string relativePath, CancellationToken ct)
    {
        try
        {
            var analysis = await _skeleton.AnalyzeCSharpFileAsync(
                projectName, relativePath, includePrivateMembers: true, ct);

            // Fire-and-forget backfill — don't block the search result
            _ = Task.Run(() => _cache.SetAsync(projectName, relativePath, analysis), CancellationToken.None);

            return analysis;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Live fallback failed for {Project}:{Path}", projectName, relativePath);
            return null;
        }
    }

    // ── Search logic (mirrors CodeSearchService, extracted here to avoid coupling) ──

    private List<CodeSearchResult> SearchInAnalysis(CSharpFileAnalysis analysis, CodeSearchRequest request)
    {
        var results = new List<CodeSearchResult>();
        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var cls in analysis.Classes)
        {
            // Class/Interface match
            if (ShouldInclude(request.MemberType, CodeMemberType.Class) ||
                ShouldInclude(request.MemberType, CodeMemberType.Interface))
            {
                var score = FuzzyScore(cls.Name, request.Query, comparison);
                if (score > 0)
                {
                    results.Add(new CodeSearchResult
                    {
                        Name = cls.Name,
                        MemberType = cls.Interfaces.Count > 0 ? CodeMemberType.Interface : CodeMemberType.Class,
                        FilePath = analysis.FilePath,
                        LineNumber = cls.LineNumber,
                        Modifiers = cls.Modifiers,
                        RelevanceScore = score,
                        TypeInfo = BuildClassTypeInfo(cls)
                    });
                }
            }

            if (!ShouldInclude(request.MemberType, CodeMemberType.Method) &&
                !ShouldInclude(request.MemberType, CodeMemberType.Property) &&
                !ShouldInclude(request.MemberType, CodeMemberType.Field) &&
                request.MemberType != CodeMemberType.All)
                continue;

            // Methods
            foreach (var m in cls.Methods)
            {
                var score = FuzzyScore(m.Name, request.Query, comparison);
                if (score > 0 && ShouldInclude(request.MemberType, CodeMemberType.Method))
                    results.Add(new CodeSearchResult
                    {
                        Name = m.Name,
                        MemberType = CodeMemberType.Method,
                        FilePath = analysis.FilePath,
                        LineNumber = m.LineNumber,
                        ParentClass = cls.Name,
                        Signature = BuildMethodSig(m),
                        Modifiers = m.Modifiers,
                        RelevanceScore = score
                    });
            }

            // Properties
            foreach (var p in cls.Properties)
            {
                var score = FuzzyScore(p.Name, request.Query, comparison);
                if (score > 0 && ShouldInclude(request.MemberType, CodeMemberType.Property))
                    results.Add(new CodeSearchResult
                    {
                        Name = p.Name,
                        MemberType = CodeMemberType.Property,
                        FilePath = analysis.FilePath,
                        LineNumber = p.LineNumber,
                        ParentClass = cls.Name,
                        TypeInfo = p.Type,
                        Modifiers = p.Modifiers,
                        RelevanceScore = score
                    });
            }

            // Fields
            foreach (var f in cls.Fields)
            {
                var score = FuzzyScore(f.Name, request.Query, comparison);
                if (score > 0 && ShouldInclude(request.MemberType, CodeMemberType.Field))
                    results.Add(new CodeSearchResult
                    {
                        Name = f.Name,
                        MemberType = CodeMemberType.Field,
                        FilePath = analysis.FilePath,
                        LineNumber = f.LineNumber,
                        ParentClass = cls.Name,
                        TypeInfo = f.Type,
                        Modifiers = f.Modifiers,
                        RelevanceScore = score
                    });
            }
        }

        return results;
    }

    // ── Scoring & helpers ─────────────────────────────────────────────────────

    private static double FuzzyScore(string name, string query, StringComparison cmp)
    {
        if (name.Equals(query, cmp)) return 1.0;
        if (name.StartsWith(query, cmp)) return 0.9;
        if (name.Contains(query, cmp)) return 0.7;
        return 0;
    }

    private static bool ShouldInclude(CodeMemberType filter, CodeMemberType type) =>
        filter == CodeMemberType.All || filter == type;

    private static string BuildClassTypeInfo(ClassInfo c) =>
        c.BaseClass != null
            ? $"extends {c.BaseClass}" + (c.Interfaces.Count > 0 ? $", implements {string.Join(", ", c.Interfaces)}" : "")
            : c.Interfaces.Count > 0 ? $"implements {string.Join(", ", c.Interfaces)}" : string.Empty;

    private static string BuildMethodSig(MethodInfo m)
    {
        var ps = string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"));
        return $"{m.ReturnType} {m.Name}({ps})";
    }

    private static List<CodeSearchResult> RankAndFilter(List<CodeSearchResult> results, CodeSearchRequest req) =>
        results.OrderByDescending(r => r.RelevanceScore).ToList();
}