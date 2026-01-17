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
            if (!projects.TryGetValue(request.ProjectName, out var projectInfo))
                throw new ArgumentException($"Project '{request.ProjectName}' not found", nameof(request.ProjectName));

            var projectPath = projectInfo.Path;
            if (!Directory.Exists(projectPath))
                throw new DirectoryNotFoundException($"Project path not found: {projectPath}");

            var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsExcludedPath(f))
                .ToList();

            _logger.LogInformation("Searching {FileCount} C# files in project {ProjectName}", csFiles.Count, request.ProjectName);

            foreach (var filePath in csFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var relativeFilePath = Path.GetRelativePath(projectPath, filePath);
                    var analysis = await _skeletonService.AnalyzeCSharpFileAsync(
                        request.ProjectName,
                        relativeFilePath,
                        includePrivateMembers: true,
                        cancellationToken);

                    allResults.AddRange(SearchInAnalysis(analysis, request));
                    filesScanned++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to analyze file: {FilePath}", filePath);
                }
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
                FilesScanned = filesScanned
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Global code search failed for project {ProjectName}, query '{Query}'",
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
                }
            }

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
                }
            }

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
                }
            }
        }

        return results;
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
            _ => 0
        };

        return score;
    }

    private static List<CodeSearchResult> RankAndFilterResults(
        List<CodeSearchResult> results,
        CodeSearchRequest request)
    {
        return results
            .OrderByDescending(r => r.RelevanceScore)
            .ThenBy(r => r.Name)
            .ToList();
    }
}