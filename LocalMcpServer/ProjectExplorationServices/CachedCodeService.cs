using MCP.Core.Models;
using MCP.Core.Services;
using System.Diagnostics;

namespace MCP.Core.Services;

/// <summary>
/// Redis-cached code search service with method body content search support.
/// Searches both member names (fast, structured) and method body content (comprehensive).
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
        CodeSearchService fallback,
        ILogger<CachedCodeSearchService> logger)
    {
        _cache = cache;
        _skeleton = skeleton;
        _fallback = fallback;
        _logger = logger;
    }

    // Lines 30-71 in ProjectExplorationServices/CachedCodeService.cs
    // Replace the entire SearchGloballyAsync method

    public async Task<CodeSearchResponse> SearchGloballyAsync(
        CodeSearchRequest request,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        List<CodeSearchResult> allResults;
        int totalFilesScanned;

        if (request.ProjectName == "*")
        {
            var projects = _skeleton.GetAvailableProjects();

            var projectTasks = projects.Keys.Select(projectName =>
                SearchProjectAsync(projectName, request, ct));

            var projectResults = await Task.WhenAll(projectTasks);

            allResults = projectResults
                .SelectMany(r => r.Results)
                .ToList();

            totalFilesScanned = projectResults.Sum(r => r.FilesScanned);
        }
        else
        {
            var (results, scanned) = await SearchProjectAsync(request.ProjectName, request, ct);
            allResults = results;
            totalFilesScanned = scanned;
        }

        var ranked = RankAndFilter(allResults, request);

        var pageSize = request.EffectivePageSize;
        var page = Math.Max(1, request.Page);

        return new CodeSearchResponse
        {
            Query = request.Query,
            ProjectName = request.ProjectName,
            TotalResults = ranked.Count,
            Results = ranked.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            FilesScanned = totalFilesScanned,
            SearchDuration = sw.Elapsed,
            ProjectsSearched = request.ProjectName == "*"
                ? _skeleton.GetAvailableProjects().Count
                : 1,
            Page = page,
            PageSize = pageSize
        };
    }

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

            // STEP 1: Search member names (classes, methods, properties, fields)
            var fileResults = SearchInAnalysis(analysis, request);
            foreach (var r in fileResults)
                r.ProjectName = projectName;

            results.AddRange(fileResults);

            // STEP 2: 🆕 If query looks like content, search within method bodies
            if (ShouldSearchMethodBodies(request.Query))
            {
                var contentResults = await SearchMethodBodiesAsync(
                    projectName,
                    analysis,
                    request,
                    ct);

                foreach (var r in contentResults)
                    r.ProjectName = projectName;

                results.AddRange(contentResults);
            }

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
        string projectName,
        string relativePath,
        CancellationToken ct)
    {
        try
        {
            var analysis = await _skeleton.AnalyzeCSharpFileAsync(
                projectName, relativePath, includePrivateMembers: true, ct);
            _ = _cache.SetAsync(projectName, relativePath, analysis);
            return analysis;
        }
        catch { return null; }
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // 🆕 NEW: Method Body Content Search
    // ═══════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines if query should trigger method body content search.
    /// Searches content for: log messages, error strings, multi-word queries.
    /// </summary>
    private static bool ShouldSearchMethodBodies(string query)
    {
        // Search method bodies if query contains:
        // - Spaces (multi-word like "Controller: Received request")
        // - Colons/quotes (typical of log messages)
        // - Is longer than 15 chars (likely a phrase, not a method name)
        return query.Contains(' ') ||
               query.Contains(':') ||
               query.Contains('"') ||
               query.Length > 15;
    }

    /// <summary>
    /// Searches within method body content by reading the actual source file.
    /// Returns methods that contain the search query in their body.
    /// </summary>
    private async Task<List<CodeSearchResult>> SearchMethodBodiesAsync(
    string projectName,
    CSharpFileAnalysis analysis,
    CodeSearchRequest request,
    CancellationToken ct)
    {
        var results = new List<CodeSearchResult>();
        var cmp = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // Use the already-cached method bodies — zero disk I/O
        var methods = await _cache.GetMethodsAsync(projectName, analysis.FilePath);
        if (methods == null) return results;

        foreach (var (_, methodInfo) in methods)
        {
            if (!ShouldInclude(request.MemberType, CodeMemberType.Method)) continue;

            var bodyToSearch = methodInfo.FullMethodCode ?? methodInfo.MethodBody ?? string.Empty;
            if (!bodyToSearch.Contains(request.Query, cmp)) continue;

            // Find exact matching line within the cached code
            var lines = bodyToSearch.Split('\n');
            var matchOffset = Array.FindIndex(lines, l => l.Contains(request.Query, cmp));
            var matchLine = matchOffset >= 0
                ? methodInfo.LineNumber + matchOffset
                : methodInfo.LineNumber;

            results.Add(new CodeSearchResult
            {
                Name = methodInfo.MethodName,
                MemberType = CodeMemberType.Method,
                FilePath = analysis.FilePath,
                LineNumber = matchLine,
                ParentClass = methodInfo.ClassName,
                Signature = $"{methodInfo.ReturnType} {methodInfo.MethodName}({string.Join(", ", methodInfo.Parameters.Select(p => $"{p.Type} {p.Name}"))})",
                Modifiers = methodInfo.Modifiers,
                RelevanceScore = 0.8,
                TypeInfo = "💬 Content match in method body",
                ProjectName = projectName
            });
        }

        return results;
    }

    /// <summary>
    /// Extracts method body content between start and end lines.
    /// </summary>
    private static string ExtractMethodBody(string[] lines, int startIdx, int endIdx)
    {
        if (startIdx < 0 || endIdx >= lines.Length || startIdx > endIdx)
            return string.Empty;

        var bodyLines = lines[startIdx..(endIdx + 1)];
        return string.Join("\n", bodyLines);
    }

    /// <summary>
    /// Finds the exact line number where the query match occurs within method.
    /// Returns the line number of the first match.
    /// </summary>
    private static int FindMatchingLine(
        string[] lines,
        int startIdx,
        int endIdx,
        string query,
        StringComparison cmp)
    {
        for (int i = startIdx; i <= endIdx && i < lines.Length; i++)
        {
            if (lines[i].Contains(query, cmp))
            {
                return i + 1; // Convert 0-based to 1-based line number
            }
        }

        return startIdx + 1; // Fallback to method start
    }

    // ═══════════════════════════════════════════════════════════════════════════════
    // EXISTING: Member Name Search (Unchanged)
    // ═══════════════════════════════════════════════════════════════════════════════

    private static List<CodeSearchResult> SearchInAnalysis(CSharpFileAnalysis analysis, CodeSearchRequest request)
    {
        var results = new List<CodeSearchResult>();
        var cmp = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var cls in analysis.Classes)
        {
            if (ShouldInclude(request.MemberType, CodeMemberType.Class))
            {
                var s = Score(cls.Name, request.Query, cmp);
                if (s > 0) results.Add(new CodeSearchResult
                {
                    Name = cls.Name,
                    MemberType = CodeMemberType.Class,
                    FilePath = analysis.FilePath,
                    LineNumber = cls.LineNumber,
                    Modifiers = cls.Modifiers,
                    RelevanceScore = s,
                    TypeInfo = BuildClassTypeInfo(cls)
                });
            }

            if (ShouldInclude(request.MemberType, CodeMemberType.Interface))
            {
                foreach (var iface in cls.Interfaces)
                {
                    var s = Score(iface, request.Query, cmp);
                    if (s > 0) results.Add(new CodeSearchResult
                    {
                        Name = iface,
                        MemberType = CodeMemberType.Interface,
                        FilePath = analysis.FilePath,
                        LineNumber = cls.LineNumber,
                        ParentClass = cls.Name,
                        TypeInfo = $"Implemented by {cls.Name}",
                        Modifiers = [],
                        RelevanceScore = s
                    });
                }
            }

            if (ShouldInclude(request.MemberType, CodeMemberType.Attribute))
                SearchAttributes(results, cls.Attributes, request.Query, cmp,
                    analysis.FilePath, cls.LineNumber, cls.Name, null);

            foreach (var m in cls.Methods)
            {
                if (ShouldInclude(request.MemberType, CodeMemberType.Method))
                {
                    var s = Score(m.Name, request.Query, cmp);
                    if (s > 0) results.Add(new CodeSearchResult
                    {
                        Name = m.Name,
                        MemberType = CodeMemberType.Method,
                        FilePath = analysis.FilePath,
                        LineNumber = m.LineNumber,
                        ParentClass = cls.Name,
                        Signature = BuildMethodSig(m),
                        Modifiers = m.Modifiers,
                        RelevanceScore = s
                    });
                }

                if (ShouldInclude(request.MemberType, CodeMemberType.Attribute))
                    SearchAttributes(results, m.Attributes, request.Query, cmp,
                        analysis.FilePath, m.LineNumber, cls.Name, m.Name);
            }

            foreach (var p in cls.Properties)
            {
                if (ShouldInclude(request.MemberType, CodeMemberType.Property))
                {
                    var s = Score(p.Name, request.Query, cmp);
                    if (s > 0) results.Add(new CodeSearchResult
                    {
                        Name = p.Name,
                        MemberType = CodeMemberType.Property,
                        FilePath = analysis.FilePath,
                        LineNumber = p.LineNumber,
                        ParentClass = cls.Name,
                        TypeInfo = p.Type,
                        Modifiers = p.Modifiers,
                        RelevanceScore = s
                    });
                }

                if (ShouldInclude(request.MemberType, CodeMemberType.Attribute))
                    SearchAttributes(results, p.Attributes, request.Query, cmp,
                        analysis.FilePath, p.LineNumber, cls.Name, p.Name);
            }

            foreach (var f in cls.Fields)
            {
                if (ShouldInclude(request.MemberType, CodeMemberType.Field))
                {
                    var s = Score(f.Name, request.Query, cmp);
                    if (s > 0) results.Add(new CodeSearchResult
                    {
                        Name = f.Name,
                        MemberType = CodeMemberType.Field,
                        FilePath = analysis.FilePath,
                        LineNumber = f.LineNumber,
                        ParentClass = cls.Name,
                        TypeInfo = f.Type,
                        Modifiers = f.Modifiers,
                        RelevanceScore = s
                    });
                }

                if (ShouldInclude(request.MemberType, CodeMemberType.Attribute))
                    SearchAttributes(results, f.Attributes, request.Query, cmp,
                        analysis.FilePath, f.LineNumber, cls.Name, f.Name);
            }
        }

        return results;
    }

    // Helper methods
    private static double Score(
        string name,
        string query,
        StringComparison cmp)
    {
        if (name.Equals(query, cmp)) return 1.0;
        if (name.Contains(query, cmp)) return 0.8;
        if (IsCamelCaseAcronym(name, query)) return 0.7;
        return 0.0;
    }

    private static bool IsCamelCaseAcronym(string name, string query)
    {
        var caps = new string(name.Where(char.IsUpper).ToArray());
        return caps.Equals(query, StringComparison.OrdinalIgnoreCase);
    }

    private static void SearchAttributes(
        List<CodeSearchResult> results,
        List<AttributeInfo>? attributes,
        string query,
        StringComparison cmp,
        string filePath,
        int lineNumber,
        string parentClass,
        string? parentMember)
    {
        if (attributes == null) return;

        foreach (var attr in attributes)
        {
            var attrName = ExtractAttributeName(attr.Name);
            if (attrName.Contains(query, cmp))
            {
                results.Add(new CodeSearchResult
                {
                    Name = attrName,
                    MemberType = CodeMemberType.Attribute,
                    FilePath = filePath,
                    LineNumber = lineNumber,
                    ParentClass = parentClass,
                    ParentMember = parentMember,
                    TypeInfo = attr.Name,
                    Modifiers = [],
                    RelevanceScore = 0.9
                });
            }
        }
    }

    private static string ExtractAttributeName(string attributeText)
    {
        return attributeText.Replace("Attribute", "").Trim();
    }

    private static bool ShouldInclude(CodeMemberType filter, CodeMemberType type) =>
        filter == CodeMemberType.All || filter == type;

    private static string BuildClassTypeInfo(ClassInfo c)
    {
        var parts = new List<string>();
        if (c.BaseClass != null) parts.Add($"extends {c.BaseClass}");
        if (c.Interfaces.Any()) parts.Add($"implements {string.Join(", ", c.Interfaces)}");
        return parts.Any() ? string.Join(", ", parts) : string.Empty;
    }

    private static string BuildMethodSig(MethodInfo m) =>
        $"{m.ReturnType} {m.Name}({string.Join(", ", m.Parameters.Select(p => $"{p.Type} {p.Name}"))})";

private static List<CodeSearchResult> RankAndFilter(List<CodeSearchResult> results, CodeSearchRequest req) =>
        results.OrderByDescending(r => r.RelevanceScore).ThenBy(r => r.Name).ToList(); // no Take — caller paginates
}
