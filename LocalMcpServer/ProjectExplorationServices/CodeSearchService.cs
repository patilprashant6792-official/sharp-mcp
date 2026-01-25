using MCP.Core.Models;
using System.Diagnostics;

namespace MCP.Core.Services;

public class CodeSearchService : ICodeSearchService
{
    private readonly IProjectSkeletonService _skeletonService;
    private readonly ILogger<CodeSearchService> _logger;

    public CodeSearchService(
        IProjectSkeletonService skeletonService,
        ILogger<CodeSearchService> logger)
    {
        _skeletonService = skeletonService ?? throw new ArgumentNullException(nameof(skeletonService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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

            return new CodeSearchResponse
            {
                Query = request.Query,
                ProjectName = request.ProjectName,
                TotalResults = allResults.Count,
                Results = rankedResults.Take(request.TopK).ToList(),
                SearchDuration = sw.Elapsed,
                FilesScanned = filesScanned,
                ProjectsSearched = projectsToSearch.Count
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
        var excludedFolders = new[] { "bin", "obj", "node_modules", ".vs", ".git", "packages" };
        return excludedFolders.Any(folder => path.Contains($"{Path.DirectorySeparatorChar}{folder}{Path.DirectorySeparatorChar}"));
    }

    private List<CodeSearchResult> SearchInAnalysis(CSharpFileAnalysis analysis, CodeSearchRequest request)
    {
        var results = new List<CodeSearchResult>();
        var comparison = request.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (var classInfo in analysis.Classes)
        {
            // Search class names
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Class) &&
                classInfo.Name.Contains(request.Query, comparison))
            {
                results.Add(new CodeSearchResult
                {
                    Name = classInfo.Name,
                    MemberType = CodeMemberType.Class,
                    FilePath = analysis.FilePath,
                    LineNumber = classInfo.LineNumber,
                    Modifiers = classInfo.Modifiers,
                    TypeInfo = BuildClassTypeInfo(classInfo),
                    RelevanceScore = CalculateRelevance(classInfo.Name, request.Query, CodeMemberType.Class)
                });
            }

            // Search class-level attributes
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Attribute) &&
                classInfo.Attributes != null)
            {
                foreach (var attribute in classInfo.Attributes)
                {
                    var attributeName = ExtractAttributeName(attribute.Name);
                    if (attributeName.Contains(request.Query, comparison))
                    {
                        results.Add(new CodeSearchResult
                        {
                            Name = attributeName,
                            MemberType = CodeMemberType.Attribute,
                            FilePath = analysis.FilePath,
                            LineNumber = classInfo.LineNumber,
                            ParentClass = classInfo.Name,
                            ParentMember = null,
                            Signature = attribute.Name,
                            Modifiers = new List<string>(),
                            RelevanceScore = CalculateRelevance(attributeName, request.Query, CodeMemberType.Attribute)
                        });
                    }
                }
            }

            // Search methods
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Method))
            {
                foreach (var method in classInfo.Methods)
                {
                    if (method.Name.Contains(request.Query, comparison))
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
                            RelevanceScore = CalculateRelevance(method.Name, request.Query, CodeMemberType.Method)
                        });
                    }

                    // Search method-level attributes
                    if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Attribute) &&
                        method.Attributes != null)
                    {
                        foreach (var attribute in method.Attributes)
                        {
                            var attributeName = ExtractAttributeName(attribute.Name);
                            if (attributeName.Contains(request.Query, comparison))
                            {
                                results.Add(new CodeSearchResult
                                {
                                    Name = attributeName,
                                    MemberType = CodeMemberType.Attribute,
                                    FilePath = analysis.FilePath,
                                    LineNumber = method.LineNumber,
                                    ParentClass = classInfo.Name,
                                    ParentMember = method.Name,
                                    Signature = attribute.Name,
                                    Modifiers = new List<string>(),
                                    RelevanceScore = CalculateRelevance(attributeName, request.Query, CodeMemberType.Attribute)
                                });
                            }
                        }
                    }
                }
            }

            // Search properties
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Property))
            {
                foreach (var property in classInfo.Properties)
                {
                    if (property.Name.Contains(request.Query, comparison))
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
                            RelevanceScore = CalculateRelevance(property.Name, request.Query, CodeMemberType.Property)
                        });
                    }

                    // Search property-level attributes
                    if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Attribute) &&
                        property.Attributes != null)
                    {
                        foreach (var attribute in property.Attributes)
                        {
                            var attributeName = ExtractAttributeName(attribute.Name);
                            if (attributeName.Contains(request.Query, comparison))
                            {
                                results.Add(new CodeSearchResult
                                {
                                    Name = attributeName,
                                    MemberType = CodeMemberType.Attribute,
                                    FilePath = analysis.FilePath,
                                    LineNumber = property.LineNumber,
                                    ParentClass = classInfo.Name,
                                    ParentMember = property.Name,
                                    Signature = attribute.Name,
                                    Modifiers = new List<string>(),
                                    RelevanceScore = CalculateRelevance(attributeName, request.Query, CodeMemberType.Attribute)
                                });
                            }
                        }
                    }
                }
            }

            // Search fields
            if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Field))
            {
                foreach (var field in classInfo.Fields)
                {
                    if (field.Name.Contains(request.Query, comparison))
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
                            RelevanceScore = CalculateRelevance(field.Name, request.Query, CodeMemberType.Field)
                        });
                    }

                    // Search field-level attributes
                    if (ShouldIncludeMemberType(request.MemberType, CodeMemberType.Attribute) &&
                        field.Attributes != null)
                    {
                        foreach (var attribute in field.Attributes)
                        {
                            var attributeName = ExtractAttributeName(attribute.Name);
                            if (attributeName.Contains(request.Query, comparison))
                            {
                                results.Add(new CodeSearchResult
                                {
                                    Name = attributeName,
                                    MemberType = CodeMemberType.Attribute,
                                    FilePath = analysis.FilePath,
                                    LineNumber = field.LineNumber,
                                    ParentClass = classInfo.Name,
                                    ParentMember = field.Name,
                                    Signature = attribute.Name,
                                    Modifiers = new List<string>(),
                                    RelevanceScore = CalculateRelevance(attributeName, request.Query, CodeMemberType.Attribute)
                                });
                            }
                        }
                    }
                }
            }
        }

        return results;
    }

    private static string ExtractAttributeName(string attributeText)
    {
        // Examples:
        // "[HttpPost]" -> "HttpPost"
        // "[HttpPost("api/users")]" -> "HttpPost"
        // "[Authorize(Roles = "Admin")]" -> "Authorize"

        var cleaned = attributeText.Trim('[', ']', ' ');
        var parenIndex = cleaned.IndexOf('(');
        return parenIndex > 0 ? cleaned[..parenIndex] : cleaned;
    }

    private static bool ShouldIncludeMemberType(CodeMemberType filter, CodeMemberType type)
        => filter == CodeMemberType.All || filter == type;

    private static string BuildClassTypeInfo(ClassInfo classInfo)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(classInfo.BaseClass))
            parts.Add($"extends {classInfo.BaseClass}");
        if (classInfo.Interfaces.Any())
            parts.Add($"implements {string.Join(", ", classInfo.Interfaces)}");
        return parts.Any() ? string.Join(", ", parts) : "class";
    }

    private static string BuildMethodSignature(MethodInfo method)
    {
        var parameters = string.Join(", ", method.Parameters.Select(p =>
            $"{p.Type} {p.Name}" + (p.DefaultValue != null ? $" = {p.DefaultValue}" : "")));
        return $"{method.ReturnType} {method.Name}({parameters})";
    }

    private static double CalculateRelevance(string name, string query, CodeMemberType type)
    {
        double score = 0;

        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
            score += 100;
        else if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            score += 50;
        else if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
            score += 25;

        score += type switch
        {
            CodeMemberType.Class => 10,
            CodeMemberType.Interface => 9,
            CodeMemberType.Method => 5,
            CodeMemberType.Property => 3,
            CodeMemberType.Field => 2,
            CodeMemberType.Attribute => 4,
            _ => 0
        };

        return score;
    }

    private List<CodeSearchResult> RankAndFilterResults(List<CodeSearchResult> results, CodeSearchRequest request)
    {
        return results
            .OrderByDescending(r => r.RelevanceScore)
            .ThenBy(r => r.Name)
            .ToList();
    }
}