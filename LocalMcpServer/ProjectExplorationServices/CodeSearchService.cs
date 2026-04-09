using MCP.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MCP.Core.Services;

public class CodeSearchService : ICodeSearchService
{
    private readonly IProjectSkeletonService _skeletonService;
    private readonly ILogger<CodeSearchService> _logger;

    public CodeSearchService(
        IProjectSkeletonService skeletonService,
        ILogger<CodeSearchService> logger)
    {
        _skeletonService = skeletonService;
        _logger = logger;
    }

    public async Task<CodeSearchResponse> SearchGloballyAsync(
        CodeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
            throw new ArgumentException("Search query cannot be empty", nameof(request));

        var sw = Stopwatch.StartNew();
        var allResults = new List<CodeSearchResult>();
        var filesScanned = 0;

        try
        {
            var projects = _skeletonService.GetAvailableProjectsWithInfo();

            // Support wildcard "*" to search all projects
            var projectsToSearch = request.ProjectName == "*"
                ? projects.Keys.ToList()
                : new List<string> { request.ProjectName };

            _logger.LogInformation(
                "Searching {ProjectCount} project(s) for '{Query}'",
                projectsToSearch.Count,
                request.Query);

            // Search projects in parallel
            var searchTasks = projectsToSearch.Select(async projectName =>
            {
                try
                {
                    if (!projects.TryGetValue(projectName, out var projectInfo))
                    {
                        _logger.LogWarning("Project '{ProjectName}' not found, skipping", projectName);
                        return (ProjectName: projectName, Results: new List<CodeSearchResult>(), FilesScanned: 0);
                    }

                    var projectPath = projectInfo.Path;
                    if (!Directory.Exists(projectPath))
                    {
                        _logger.LogWarning("Project path not found: {ProjectPath}, skipping", projectPath);
                        return (projectName, new List<CodeSearchResult>(), 0);
                    }

                    var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                        .Where(f => !IsExcludedPath(f))
                        .ToList();

                    var projectResults = new List<CodeSearchResult>();
                    var projectFilesScanned = 0;

                    foreach (var filePath in csFiles)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        try
                        {
                            var relativeFilePath = Path.GetRelativePath(projectPath, filePath);
                            var analysis = await _skeletonService.AnalyzeCSharpFileAsync(
                                projectName,
                                relativeFilePath,
                                includePrivateMembers: true,
                                cancellationToken);

                            var searchResults = SearchInAnalysis(analysis, request);

                            // Tag results with project name for cross-project context
                            foreach (var result in searchResults)
                            {
                                result.ProjectName = projectName;
                            }

                            projectResults.AddRange(searchResults);
                            projectFilesScanned++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to analyze file: {FilePath}", filePath);
                        }
                    }

                    _logger.LogInformation(
                        "Project '{ProjectName}': {ResultCount} results from {FileCount} files",
                        projectName, projectResults.Count, projectFilesScanned);

                    return (projectName, projectResults, projectFilesScanned);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to search project '{ProjectName}'", projectName);
                    return (projectName, new List<CodeSearchResult>(), 0);
                }
            });

            var searchResults = await Task.WhenAll(searchTasks);

            // Aggregate results from all projects
            foreach (var (projectName, results, scanned) in searchResults)
            {
                allResults.AddRange(results);
                filesScanned += scanned;
            }

            var rankedResults = RankAndFilterResults(allResults, request);
            sw.Stop();

            var pageSize = request.EffectivePageSize;
            var totalPages = rankedResults.Count == 0 ? 1 : (int)Math.Ceiling((double)rankedResults.Count / pageSize);
            var page = rankedResults.Count == 0 ? 1 : Math.Clamp(request.Page, 1, totalPages);

            return new CodeSearchResponse
            {
                Query = request.Query,
                ProjectName = request.ProjectName,
                TotalResults = allResults.Count,
                Results = rankedResults.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                SearchDuration = sw.Elapsed,
                FilesScanned = filesScanned,
                ProjectsSearched = projectsToSearch.Count,
                Page = page,
                PageSize = pageSize
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Global code search failed for project '{ProjectName}', query '{Query}'",
                request.ProjectName, request.Query);
            throw;
        }
    }

    private static bool IsExcludedPath(string path)
    {
        var excludedDirs = new[] { "bin", "obj", "node_modules", ".git", ".vs" };
        return excludedDirs.Any(dir => path.Contains(Path.DirectorySeparatorChar + dir + Path.DirectorySeparatorChar));
    }

    private List<CodeSearchResult> SearchInAnalysis(CSharpFileAnalysis analysis, CodeSearchRequest request)
    {
        var results = new List<CodeSearchResult>();
        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var classInfo in analysis.Classes)
        {
            // ══════════════════════════════════════════════════════════════════════════════
            // CLASSES
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Class))
            {
                var score = CalculateFuzzyScore(classInfo.Name, request.Query, comparison);
                if (score > 0)
                {
                    results.Add(new CodeSearchResult
                    {
                        Name = classInfo.Name,
                        MemberType = CodeMemberType.Class,
                        FilePath = analysis.FilePath,
                        LineNumber = classInfo.LineNumber,
                        Modifiers = classInfo.Modifiers,
                        TypeInfo = BuildClassTypeInfo(classInfo),
                        RelevanceScore = score,
                        ParentMember = null
                    });
                }
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // INTERFACES (NEW - Search in implemented interfaces)
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Interface))
            {
                foreach (var interfaceName in classInfo.Interfaces)
                {
                    var score = CalculateFuzzyScore(interfaceName, request.Query, comparison);
                    if (score > 0)
                    {
                        results.Add(new CodeSearchResult
                        {
                            Name = interfaceName,
                            MemberType = CodeMemberType.Interface,
                            FilePath = analysis.FilePath,
                            LineNumber = classInfo.LineNumber,
                            ParentClass = classInfo.Name,
                            TypeInfo = $"Implemented by {classInfo.Name}",
                            Modifiers = new List<string>(),
                            RelevanceScore = score,
                            ParentMember = null
                        });
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // CLASS-LEVEL ATTRIBUTES
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Attribute))
            {
                SearchAttributes(
                    results,
                    classInfo.Attributes,
                    request.Query,
                    comparison,
                    analysis.FilePath,
                    classInfo.LineNumber,
                    classInfo.Name,
                    parentMember: null
                );
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // METHODS
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Method))
            {
                foreach (var method in classInfo.Methods)
                {
                    var score = CalculateFuzzyScore(method.Name, request.Query, comparison);
                    if (score > 0)
                    {
                        results.Add(new CodeSearchResult
                        {
                            Name = method.Name,
                            MemberType = CodeMemberType.Method,
                            FilePath = analysis.FilePath,
                            LineNumber = method.LineNumber,
                            ParentClass = classInfo.Name,
                            Signature = BuildMethodSignature(method),
                            Modifiers = method.Modifiers,
                            RelevanceScore = score,
                            ParentMember = null
                        });
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // METHOD-LEVEL ATTRIBUTES (SEPARATE LOOP - CRITICAL FOR FINDING [HttpPost])
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Attribute))
            {
                foreach (var method in classInfo.Methods)
                {
                    SearchAttributes(
                        results,
                        method.Attributes,
                        request.Query,
                        comparison,
                        analysis.FilePath,
                        method.LineNumber,
                        classInfo.Name,
                        parentMember: method.Name
                    );
                }
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // PROPERTIES
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Property))
            {
                foreach (var property in classInfo.Properties)
                {
                    var score = CalculateFuzzyScore(property.Name, request.Query, comparison);
                    if (score > 0)
                    {
                        results.Add(new CodeSearchResult
                        {
                            Name = property.Name,
                            MemberType = CodeMemberType.Property,
                            FilePath = analysis.FilePath,
                            LineNumber = property.LineNumber,
                            ParentClass = classInfo.Name,
                            TypeInfo = property.Type,
                            Modifiers = property.Modifiers,
                            RelevanceScore = score,
                            ParentMember = null
                        });
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // PROPERTY-LEVEL ATTRIBUTES
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Attribute))
            {
                foreach (var property in classInfo.Properties)
                {
                    SearchAttributes(
                        results,
                        property.Attributes,
                        request.Query,
                        comparison,
                        analysis.FilePath,
                        property.LineNumber,
                        classInfo.Name,
                        parentMember: property.Name
                    );
                }
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // FIELDS
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Field))
            {
                foreach (var field in classInfo.Fields)
                {
                    var score = CalculateFuzzyScore(field.Name, request.Query, comparison);
                    if (score > 0)
                    {
                        results.Add(new CodeSearchResult
                        {
                            Name = field.Name,
                            MemberType = CodeMemberType.Field,
                            FilePath = analysis.FilePath,
                            LineNumber = field.LineNumber,
                            ParentClass = classInfo.Name,
                            TypeInfo = field.Type,
                            Modifiers = field.Modifiers,
                            RelevanceScore = score,
                            ParentMember = null
                        });
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════════════════════
            // FIELD-LEVEL ATTRIBUTES
            // ══════════════════════════════════════════════════════════════════════════════
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Attribute))
            {
                foreach (var field in classInfo.Fields)
                {
                    SearchAttributes(
                        results,
                        field.Attributes,
                        request.Query,
                        comparison,
                        analysis.FilePath,
                        field.LineNumber,
                        classInfo.Name,
                        parentMember: field.Name
                    );
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Searches attributes and adds results with proper parent context
    /// </summary>
    private void SearchAttributes(
        List<CodeSearchResult> results,
        List<AttributeInfo>? attributes,
        string query,
        StringComparison comparison,
        string filePath,
        int lineNumber,
        string parentClass,
        string? parentMember)
    {
        if (attributes == null || attributes.Count == 0)
            return;

        foreach (var attr in attributes)
        {
            var attrName = ExtractAttributeName(attr.Name);
            var score = CalculateFuzzyScore(attrName, query, comparison);

            if (score > 0)
            {
                results.Add(new CodeSearchResult
                {
                    Name = attrName,
                    MemberType = CodeMemberType.Attribute,
                    FilePath = filePath,
                    LineNumber = lineNumber,
                    ParentClass = parentClass,
                    ParentMember = parentMember, // ✨ Use the model field properly
                    Signature = parentMember != null ? $"On {parentMember}" : "Class-level",
                    Modifiers = new List<string>(),
                    RelevanceScore = score
                });
            }
        }
    }

    /// <summary>
    /// Removes "Attribute" suffix for cleaner matching (e.g., "HttpPost" from "HttpPostAttribute")
    /// </summary>
    private static string ExtractAttributeName(string attributeText)
    {
        const string suffix = "Attribute";
        return attributeText.EndsWith(suffix, StringComparison.Ordinal)
            ? attributeText[..^suffix.Length]
            : attributeText;
    }

    /// <summary>
    /// ✨ ENHANCED Visual Studio-style fuzzy scoring with 7-tier ranking:
    /// 1. Exact match → 1000 pts
    /// 2. Prefix match → 500 pts  
    /// 3. Camel case match → 300 pts (PSS matches ProjectSkeletonService)
    /// 4. Multi-word match → 150 pts 🆕 (search global matches SearchGloballyAsync)
    /// 5. Word boundary → 200 pts (Skeleton matches ProjectSkeletonService)
    /// 6. Substring → 100 pts
    /// 7. Fuzzy (chars in order) → 1-50 pts
    /// </summary>
    private double CalculateFuzzyScore(
        string name,
        string query,
        StringComparison comparison)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(query))
            return 0;

        // 1. Exact match
        if (name.Equals(query, comparison))
            return 1000;

        // 2. Prefix match
        if (name.StartsWith(query, comparison))
            return 500;

        // 3. Camel case match
        if (IsCamelCaseMatch(name, query, comparison))
            return 300;

        // 4. Word boundary match
        if (IsWordBoundaryMatch(name, query, comparison))
            return 200;

        // 4.5. 🆕 Multi-word match (split on spaces and match all tokens)
        if (query.Contains(' '))
        {
            var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.All(token => name.Contains(token, comparison)))
            {
                return 150; // Between word boundary (200) and substring (100)
            }
        }

        // 5. Substring match
        if (name.Contains(query, comparison))
            return 100;

        // 6. Fuzzy match (all chars in order)
        var fuzzyScore = CalculateFuzzyMatchScore(name, query, comparison);
        if (fuzzyScore > 0)
            return fuzzyScore;

        return 0;
    }

    /// <summary>
    /// Matches CamelCase patterns: PSS → ProjectSkeletonService
    /// </summary>
    private bool IsCamelCaseMatch(
        string name,
        string query,
        StringComparison comparison)
    {
        var upperChars = string.Concat(name.Where(char.IsUpper));
        return upperChars.StartsWith(query, comparison);
    }

    /// <summary>
    /// Matches word boundaries: Skeleton → ProjectSkeletonService
    /// </summary>
    private bool IsWordBoundaryMatch(
        string name,
        string query,
        StringComparison comparison)
    {
        var words = Regex.Split(name, @"(?=[A-Z])").Where(w => !string.IsNullOrEmpty(w));
        return words.Any(w => w.StartsWith(query, comparison));
    }

    /// <summary>
    /// Fuzzy matching: all characters in order with points based on density
    /// </summary>
    private double CalculateFuzzyMatchScore(
        string name,
        string query,
        StringComparison comparison)
    {
        int nameIdx = 0, queryIdx = 0, matches = 0;
        int totalDistance = 0;
        int lastMatchPos = -1;

        while (nameIdx < name.Length && queryIdx < query.Length)
        {
            if (name[nameIdx].ToString().Equals(query[queryIdx].ToString(), comparison))
            {
                matches++;
                if (lastMatchPos >= 0)
                {
                    totalDistance += nameIdx - lastMatchPos - 1;
                }
                lastMatchPos = nameIdx;
                queryIdx++;
            }
            nameIdx++;
        }

        if (matches != query.Length)
            return 0;

        // Score based on match density (closer chars = higher score)
        var avgDistance = matches > 1 ? (double)totalDistance / (matches - 1) : 0;
        var densityScore = Math.Max(1, 50 - (int)(avgDistance * 5));
        return densityScore;
    }

    private static bool ShouldIncludeMemberType(CodeMemberType filter, CodeMemberType type)
        => filter == CodeMemberType.All || filter == type;

    private static string BuildClassTypeInfo(ClassInfo classInfo)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(classInfo.BaseClass))
            parts.Add($"Extends: {classInfo.BaseClass}");
        if (classInfo.Interfaces.Any())
            parts.Add($"Implements: {string.Join(", ", classInfo.Interfaces)}");
        return parts.Any() ? string.Join(" | ", parts) : string.Empty;
    }

    private static string BuildMethodSignature(MethodInfo method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(p =>
            $"{p.Type} {p.Name}" + (p.DefaultValue != null ? $" = {p.DefaultValue}" : "")));
        return $"{method.ReturnType} {method.Name}({parameters})";
    }
    private List<CodeSearchResult> RankAndFilterResults(List<CodeSearchResult> results, CodeSearchRequest request)
    {
        return results
            .OrderByDescending(r => r.RelevanceScore)
            .ThenBy(r => r.Name)
            .ToList();
    }
}
