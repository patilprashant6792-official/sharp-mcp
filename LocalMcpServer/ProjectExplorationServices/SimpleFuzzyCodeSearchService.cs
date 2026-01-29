using MCP.Core.Models;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MCP.Core.Services;

/// <summary>
/// Visual Studio-style fuzzy search (Ctrl+T / Ctrl+,)
/// Searches across all code symbols with intelligent ranking
/// </summary>
public class SimpleFuzzyCodeSearchService : ICodeSearchService
{
    private readonly IProjectSkeletonService _skeletonService;
    private readonly ILogger<SimpleFuzzyCodeSearchService> _logger;

    // In-memory cache of all searchable symbols
    private Dictionary<string, List<CodeSymbol>>? _symbolCache;
    private DateTime _cacheBuiltAt = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

    public SimpleFuzzyCodeSearchService(
        IProjectSkeletonService skeletonService,
        ILogger<SimpleFuzzyCodeSearchService> logger)
    {
        _skeletonService = skeletonService ?? throw new ArgumentNullException(nameof(skeletonService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<CodeSearchResponse> SearchGloballyAsync(
        CodeSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // Build/refresh cache if needed
        await EnsureCacheAsync(request.ProjectName, cancellationToken);

        // Get symbols for requested project(s)
        var symbols = request.ProjectName == "*"
            ? _symbolCache!.Values.SelectMany(s => s).ToList()
            : _symbolCache!.TryGetValue(request.ProjectName, out var projectSymbols)
                ? projectSymbols
                : new List<CodeSymbol>();

        // Fuzzy search with intelligent ranking
        var matches = FuzzySearch(symbols, request.Query, request.CaseSensitive);

        // Filter by member type
        if (request.MemberType != CodeMemberType.All)
        {
            matches = matches.Where(m => m.Result.MemberType == request.MemberType).ToList();
        }

        // Take top K
        var topResults = matches.Take(request.TopK).Select(m => m.Result).ToList();

        sw.Stop();

        return new CodeSearchResponse
        {
            Query = request.Query,
            ProjectName = request.ProjectName,
            TotalResults = matches.Count,
            Results = topResults,
            SearchDuration = sw.Elapsed,
            FilesScanned = symbols.Select(s => s.FilePath).Distinct().Count(),
            ProjectsSearched = request.ProjectName == "*" ? _symbolCache!.Count : 1
        };
    }

    /// <summary>
    /// Builds searchable symbol index for all projects
    /// </summary>
    private async Task EnsureCacheAsync(string requestedProject, CancellationToken cancellationToken)
    {
        // Check if cache needs refresh
        if (_symbolCache != null && DateTime.UtcNow - _cacheBuiltAt < _cacheExpiry)
            return;

        _logger.LogInformation("Building symbol cache...");
        var sw = Stopwatch.StartNew();

        var projects = _skeletonService.GetAvailableProjectsWithInfo();
        var newCache = new Dictionary<string, List<CodeSymbol>>();

        // Determine which projects to index
        var projectsToIndex = requestedProject == "*"
            ? projects.Keys.ToList()
            : projects.ContainsKey(requestedProject)
                ? new List<string> { requestedProject }
                : new List<string>();

        // Index projects in parallel
        var indexTasks = projectsToIndex.Select(async projectName =>
        {
            try
            {
                var symbols = await IndexProjectAsync(projectName, projects[projectName].Path, cancellationToken);
                return (projectName, symbols);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index project {ProjectName}", projectName);
                return (projectName, new List<CodeSymbol>());
            }
        });

        var results = await Task.WhenAll(indexTasks);

        foreach (var (projectName, symbols) in results)
        {
            newCache[projectName] = symbols;
        }

        _symbolCache = newCache;
        _cacheBuiltAt = DateTime.UtcNow;

        sw.Stop();
        _logger.LogInformation(
            "Symbol cache built: {ProjectCount} projects, {SymbolCount} symbols in {Duration}ms",
            newCache.Count,
            newCache.Values.Sum(s => s.Count),
            sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Indexes all symbols in a project
    /// </summary>
    private async Task<List<CodeSymbol>> IndexProjectAsync(
        string projectName,
        string projectPath,
        CancellationToken cancellationToken)
    {
        var symbols = new List<CodeSymbol>();

        // Get all C# files
        var csFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsExcludedPath(f))
            .ToList();

        foreach (var filePath in csFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var relativeFilePath = Path.GetRelativePath(projectPath, filePath);

                // Use existing analysis service
                var analysis = await _skeletonService.AnalyzeCSharpFileAsync(
                    projectName,
                    relativeFilePath,
                    includePrivateMembers: true,
                    cancellationToken);

                // Extract all searchable symbols
                symbols.AddRange(ExtractSymbols(analysis, projectName));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index file: {FilePath}", filePath);
            }
        }

        return symbols;
    }

    /// <summary>
    /// Extracts all searchable symbols from file analysis
    /// </summary>
    private List<CodeSymbol> ExtractSymbols(CSharpFileAnalysis analysis, string projectName)
    {
        var symbols = new List<CodeSymbol>();

        foreach (var classInfo in analysis.Classes)
        {
            // Class itself
            symbols.Add(new CodeSymbol
            {
                Name = classInfo.Name,
                Type = CodeMemberType.Class,
                FilePath = analysis.FilePath,
                LineNumber = classInfo.LineNumber,
                ProjectName = projectName,
                ParentClass = null,
                Signature = $"class {classInfo.Name}",
                Modifiers = classInfo.Modifiers
            });

            // Interfaces
            foreach (var interfaceName in classInfo.Interfaces)
            {
                symbols.Add(new CodeSymbol
                {
                    Name = interfaceName,
                    Type = CodeMemberType.Interface,
                    FilePath = analysis.FilePath,
                    LineNumber = classInfo.LineNumber,
                    ProjectName = projectName,
                    ParentClass = classInfo.Name,
                    Signature = interfaceName
                });
            }

            // Methods
            foreach (var method in classInfo.Methods)
            {
                symbols.Add(new CodeSymbol
                {
                    Name = method.Name,
                    Type = CodeMemberType.Method,
                    FilePath = analysis.FilePath,
                    LineNumber = method.LineNumber,
                    ProjectName = projectName,
                    ParentClass = classInfo.Name,
                    Signature = $"{method.ReturnType} {method.Name}(...)",
                    Modifiers = method.Modifiers
                });
            }

            // Properties
            foreach (var property in classInfo.Properties)
            {
                symbols.Add(new CodeSymbol
                {
                    Name = property.Name,
                    Type = CodeMemberType.Property,
                    FilePath = analysis.FilePath,
                    LineNumber = property.LineNumber,
                    ProjectName = projectName,
                    ParentClass = classInfo.Name,
                    Signature = $"{property.Type} {property.Name}",
                    Modifiers = property.Modifiers
                });
            }

            // Fields
            foreach (var field in classInfo.Fields)
            {
                symbols.Add(new CodeSymbol
                {
                    Name = field.Name,
                    Type = CodeMemberType.Field,
                    FilePath = analysis.FilePath,
                    LineNumber = field.LineNumber,
                    ProjectName = projectName,
                    ParentClass = classInfo.Name,
                    Signature = $"{field.Type} {field.Name}",
                    Modifiers = field.Modifiers
                });
            }
        }

        return symbols;
    }

    /// <summary>
    /// Fuzzy search with Visual Studio-style ranking:
    /// 1. Exact match (highest)
    /// 2. Prefix match
    /// 3. Camel case match (e.g., "PSS" matches "ProjectSkeletonService")
    /// 4. Substring match
    /// 5. Fuzzy match (characters in order)
    /// </summary>
    private List<(CodeSearchResult Result, double Score)> FuzzySearch(
        List<CodeSymbol> symbols,
        string query,
        bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<(CodeSearchResult, double)>();

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var results = new List<(CodeSearchResult Result, double Score)>();

        foreach (var symbol in symbols)
        {
            var score = CalculateFuzzyScore(symbol.Name, query, comparison);

            if (score > 0)
            {
                results.Add((new CodeSearchResult
                {
                    Name = symbol.Name,
                    MemberType = symbol.Type,
                    FilePath = symbol.FilePath,
                    LineNumber = symbol.LineNumber,
                    ProjectName = symbol.ProjectName,
                    ParentClass = symbol.ParentClass,
                    Signature = symbol.Signature,
                    Modifiers = symbol.Modifiers,
                    RelevanceScore = score
                }, score));
            }
        }

        return results.OrderByDescending(r => r.Score)
                     .ThenBy(r => r.Result.Name)
                     .ToList();
    }

    /// <summary>
    /// Visual Studio-style fuzzy scoring algorithm
    /// </summary>
    private double CalculateFuzzyScore(string name, string query, StringComparison comparison)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(query))
            return 0;

        // 1. Exact match → 1000 points
        if (name.Equals(query, comparison))
            return 1000;

        // 2. Prefix match → 500 points
        if (name.StartsWith(query, comparison))
            return 500;

        // 3. Camel case match → 300 points
        // "PSS" matches "ProjectSkeletonService"
        if (IsCamelCaseMatch(name, query, comparison))
            return 300;

        // 4. Word boundary match → 200 points
        // "Skeleton" matches "ProjectSkeletonService"
        if (IsWordBoundaryMatch(name, query, comparison))
            return 200;

        // 5. Substring match → 100 points
        if (name.Contains(query, comparison))
            return 100;

        // 6. Fuzzy match (all chars in order) → 1-50 points
        var fuzzyScore = CalculateFuzzyMatchScore(name, query, comparison);
        if (fuzzyScore > 0)
            return fuzzyScore;

        return 0;
    }

    /// <summary>
    /// Checks if query matches camel case initials
    /// "PSS" → "ProjectSkeletonService" ✓
    /// </summary>
    private bool IsCamelCaseMatch(string name, string query, StringComparison comparison)
    {
        var upperChars = string.Concat(name.Where(char.IsUpper));
        return upperChars.Contains(query, comparison) ||
               upperChars.StartsWith(query, comparison);
    }

    /// <summary>
    /// Checks if query matches at word boundaries
    /// "Skeleton" → "ProjectSkeletonService" ✓
    /// </summary>
    private bool IsWordBoundaryMatch(string name, string query, StringComparison comparison)
    {
        var words = Regex.Split(name, @"(?=[A-Z])|_");
        return words.Any(w => w.StartsWith(query, comparison));
    }

    /// <summary>
    /// Fuzzy match - all query chars appear in name in order
    /// "pss" → "ProjectSkeletonService" ✓
    /// Score based on character proximity
    /// </summary>
    private double CalculateFuzzyMatchScore(string name, string query, StringComparison comparison)
    {
        var nameChars = comparison == StringComparison.OrdinalIgnoreCase
            ? name.ToLowerInvariant().ToCharArray()
            : name.ToCharArray();

        var queryChars = comparison == StringComparison.OrdinalIgnoreCase
            ? query.ToLowerInvariant().ToCharArray()
            : query.ToCharArray();

        int nameIndex = 0;
        int matchCount = 0;
        int consecutiveMatches = 0;
        double score = 0;

        foreach (var queryChar in queryChars)
        {
            bool found = false;

            for (; nameIndex < nameChars.Length; nameIndex++)
            {
                if (nameChars[nameIndex] == queryChar)
                {
                    matchCount++;
                    consecutiveMatches++;
                    score += consecutiveMatches; // Bonus for consecutive matches
                    nameIndex++;
                    found = true;
                    break;
                }
                else
                {
                    consecutiveMatches = 0;
                }
            }

            if (!found)
                return 0; // All query chars must be present in order
        }

        // All chars found in order
        // Score: 1-50 based on match density
        var density = (double)matchCount / name.Length;
        return Math.Max(1, score * density);
    }

    private static bool IsExcludedPath(string path)
    {
        var excludedFolders = new[] { "bin", "obj", "node_modules", ".vs", ".git", "packages" };
        return excludedFolders.Any(folder =>
            path.Contains($"{Path.DirectorySeparatorChar}{folder}{Path.DirectorySeparatorChar}"));
    }
}

/// <summary>
/// Internal symbol representation for search
/// </summary>
internal class CodeSymbol
{
    public string Name { get; set; } = string.Empty;
    public CodeMemberType Type { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string? ParentClass { get; set; }
    public string Signature { get; set; } = string.Empty;
    public List<string> Modifiers { get; set; } = new();
}