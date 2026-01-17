using MCP.Core.Configuration;
using MCP.Core.Models;
using MCP.Core.Services;
using ModelContextProtocol.Server;
using NuGetExplorer.Services;
using System.ComponentModel;
using System.Text.Json;

namespace RisingTideAI.Trade.MCP.Host;

[McpServerToolType]
public class LocalTools
{
    private readonly INuGetSearchService _nugetService;
    private readonly IProjectSkeletonService _projectSkeletonService;
    private readonly IMarkdownFormatterService _markdownFormatter;
    private readonly ITomlSerializerService _tomlSerializer;
    private readonly INuGetPackageExplorer _packageExplorer;
    private readonly IMethodFormatterService _methodFormatter; // ✨ NEW
    private readonly IMethodCallGraphService _callGraphService;
    public LocalTools(
        INuGetSearchService nugetService,
        IProjectSkeletonService projectSkeletonService,
        IMarkdownFormatterService markdownFormatter,
        ITomlSerializerService tomlSerializerService,
        INuGetPackageExplorer packageExplorer,
        IMethodFormatterService methodFormatter,
        IMethodCallGraphService callGraphService) // ✨ NEW
    {
        _nugetService = nugetService;
        _projectSkeletonService = projectSkeletonService;
        _markdownFormatter = markdownFormatter;
        _tomlSerializer = tomlSerializerService;
        _packageExplorer = packageExplorer;
        _methodFormatter = methodFormatter; // ✨ NEW
        _callGraphService = callGraphService;
    }



    #region DateTime Tools

    [McpServerTool]
    [Description("Gets the current date and time in UTC, local timezone, or a specific timezone")]
    public DateTimeResponse GetDateTime(
        [Description("Optional timezone ID (e.g., 'America/New_York', 'Europe/London', 'Asia/Tokyo'). If not provided, returns UTC and local time")]
        string? timeZoneId = null)
    {
        var utcNow = DateTime.UtcNow;

        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            var localNow = DateTime.Now;
            return new DateTimeResponse
            {
                LocalDateTime = localNow.ToString("yyyy-MM-dd HH:mm:ss"),
                UtcDateTime = utcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                TimeZone = TimeZoneInfo.Local.DisplayName,
                UnixTimestamp = new DateTimeOffset(utcNow).ToUnixTimeSeconds()
            };
        }

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            var zonedTime = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);

            return new DateTimeResponse
            {
                LocalDateTime = zonedTime.ToString("yyyy-MM-dd HH:mm:ss"),
                UtcDateTime = utcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                TimeZone = timeZone.DisplayName,
                UnixTimestamp = new DateTimeOffset(utcNow).ToUnixTimeSeconds()
            };
        }
        catch (TimeZoneNotFoundException)
        {
            throw new ArgumentException($"Timezone '{timeZoneId}' not found. Use standard timezone IDs like 'America/New_York' or 'UTC'.");
        }
    }

    #endregion

    #region NuGet Tools

    [McpServerTool]
    [Description("STEP 1: Lists all available namespaces in a NuGet package. Use this FIRST to discover what namespaces exist before exploring types.\n\nExample workflow:\n1. Call this → Get ['Newtonsoft.Json', 'Newtonsoft.Json.Linq', 'Newtonsoft.Json.Schema']\n2. Then call GetNamespaceSummary for each namespace to see what types exist")]
    public async Task<string> GetNuGetPackageNamespaces(
     [Description("Required: NuGet package ID (e.g., 'Newtonsoft.Json', 'Microsoft.EntityFrameworkCore')")]
    string packageId,
     [Description("Optional: specific version (e.g., '13.0.3'). Omit for latest stable version")]
    string? version = null,
     [Description("Optional: target framework (e.g., 'net8.0', 'net6.0'). Defaults to net10.0")]
    string? targetFramework = null,
     [Description("Optional: include prerelease versions. Defaults to false (stable only)")]
    bool includePrerelease = false)
    {
        try
        {
            var namespaces = await _packageExplorer.GetNamespaces(
                packageId, version, targetFramework, includePrerelease);
            return _tomlSerializer.Serialize(new { Namespaces = namespaces });
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to retrieve namespaces: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("STEP 2: Gets entire summary of including classes methods types and signatures.")]
    public async Task<string> GetNamespaceSummary(
        [Description("Required: NuGet package ID")]
    string packageId,
        [Description("Required: namespace to explore (from GetNuGetPackageNamespaces)")]
    string @namespace,
        [Description("Optional: specific version")]
    string? version = null,
        [Description("Optional: target framework")]
    string? targetFramework = null,
        [Description("Optional: include prerelease")]
    bool includePrerelease = false)
    {
        try
        {
            var summary = await _packageExplorer.FilterMetadataByNamespace(
                packageId, @namespace, version, targetFramework, includePrerelease);
            return summary;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to retrieve namespace summary: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description("Searches NuGet.org repository...")]
    public async Task<string> SearchNuGetPackages(
        [Description("Required: search terms...")]
    string query,
        [Description("Optional: maximum number of results...")]
    int take = 20,
        [Description("Optional: include prerelease packages in search results. Defaults to false (stable only)")]
    bool includePrerelease = false)
    {
        var results = await _nugetService.SearchPackagesAsync(query, take, includePrerelease);
        return _tomlSerializer.Serialize(results);
    }

    #endregion

    #region Project Skeleton Tools

    // Dynamic description property
    private string ProjectSkeletonDescription => _projectSkeletonService.GetToolDescription();

    [McpServerTool]
    [Description("Retrieves the complete file tree structure and content of a pre-configured project. Shows folder hierarchy, file paths, and full file contents to understand existing architecture and patterns. Use this before modifying or extending projects to understand current structure.\n\nConfigured projects:" +
        "LocalMcpServer - Main MCP server host project with tool implementations" +
        "RisingTideTradeCapture - Rising Tide AI Trade Capture project" +
        "RisingTideAPI - Rising Tide AI API project")]
    public async Task<string> GetProjectSkeleton(
     [Description("Required: name of the project to analyze (e.g., 'MCP.Host', 'MCP.Core')")]
    string projectName)
    {
        try
        {
            return await _projectSkeletonService.GetProjectSkeletonAsync(projectName);
        }
        catch (KeyNotFoundException)
        {
            var availableProjects = _projectSkeletonService.GetAvailableProjectsWithInfo();
            var projectList = string.Join("\n", availableProjects.Select(p =>
                $"• {p.Key} - {p.Value.Description}"));

            throw new ArgumentException(
                $"Project '{projectName}' not found.\n\n" +
                $"Available projects:\n{projectList}");
        }
    }

    [McpServerTool]
    [Description("Analyzes method call graph - shows WHO calls this method and WHERE. " +
      "Critical for understanding impact before modifying methods. " +
      "Returns caller locations with exact file paths, line numbers, and class resolution hints for fetching caller implementations.")]
    public async Task<string> AnalyzeMethodCallGraph(
      [Description("Required: project name (e.g., 'LocalMcpServer', 'RisingTideAPI')")]
        string projectName,

      [Description("Required: relative file path (e.g., 'Services/UserService.cs')")]
        string relativeFilePath,

      [Description("Required: method name to analyze")]
        string methodName,

      [Description("Optional: class name if file has multiple classes")]
        string? className = null,

      [Description("Optional: include test files (default: false)")]
        bool includeTests = false)
    {
        try
        {
            var graph = await _callGraphService.AnalyzeMethodDependenciesAsync(
                projectName,
                relativeFilePath,
                methodName,
                className,
                includeTests,
                depth: 1);

            return _methodFormatter.FormatMethodCallGraph(graph);
        }
        catch (Exception ex)
        {
            return $"❌ Error analyzing method call graph: {ex.Message}";
        }
    }

    // ADD THIS NEW TOOL to MCPTools.cs after ReadFileContent method (around line 280)

    [McpServerTool]
    [Description("Search and list files within a specific folder. Returns paginated results sorted by filename. " +
        "Use this to explore folders that were collapsed in get_project_skeleton (folders with >50 files). " +
        "Supports optional search filtering by filename.")]
    public async Task<string> SearchFolderFiles(
        [Description("Project name (e.g., 'LocalMcpServer', 'RisingTideAPI')")]
    string projectName,

        [Description("Relative folder path from project root (e.g., 'RisingTide.Common.DataAccess/Entities/Exchange')")]
    string folderPath,

        [Description("Optional search term to filter filenames (case-insensitive, e.g., 'Company', 'Transaction'). Leave empty to list all files.")]
    string? searchPattern = null,

        [Description("Page number (default: 1)")]
    int page = 1,

        [Description("Files per page (default: 50, max: 200)")]
    int pageSize = 50)
    {
        try
        {
            var result = await _projectSkeletonService.SearchFolderFilesAsync(
                projectName,
                folderPath,
                searchPattern,
                page,
                pageSize,
                CancellationToken.None);

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return json;
        }
        catch (ArgumentException ex)
        {
            return $"❌ Invalid argument: {ex.Message}";
        }
        catch (KeyNotFoundException ex)
        {
            return $"❌ Project not found: {ex.Message}";
        }
        catch (DirectoryNotFoundException ex)
        {
            return $"❌ Directory not found: {ex.Message}";
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"❌ Access denied: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"❌ Error searching folder: {ex.Message}\n{ex.StackTrace}";
        }
    }

    [McpServerTool]
    [Description("Analyzes a C# file and returns structured metadata including methods, properties, fields, attributes, constructor dependencies, and file classification. Use this to understand implementation patterns before adding new code.")]
    public async Task<string> AnalyzeCSharpFile(
    [Description("Required: project name (e.g., 'LocalMcpServer', 'RisingTideAPI')")]
    string projectName,
    [Description("Required: relative path to C# file from project root (e.g., 'MCPServers/MCPTools.cs', 'Controllers/ToolsController.cs')")]
    string relativeFilePath,
    [Description("Optional: include private members (methods, properties, fields). Defaults to false (public members only)")]
    bool includePrivateMembers = false)
    {
        try
        {
            var analysis = await _projectSkeletonService.AnalyzeCSharpFileAsync(
                projectName,
                relativeFilePath,
                includePrivateMembers);
            return _markdownFormatter.FormatCSharpAnalysis(analysis);
        }
        catch (KeyNotFoundException)
        {
            var availableProjects = _projectSkeletonService.GetAvailableProjectsWithInfo();
            var projectList = string.Join("\n", availableProjects.Select(p =>
                $"• {p.Key} - {p.Value.Description}"));
            throw new ArgumentException(
                $"Project '{projectName}' not found.\n\n" +
                $"Available projects:\n{projectList}");
        }
        catch (FileNotFoundException ex)
        {
            throw new ArgumentException($"File not found: {ex.Message}");
        }
    }

    [McpServerTool]
    [Description("Fetches method implementation(s) from a C# file. Supports both single and batch operations. " +
    "BATCH MODE: Pass multiple method names (comma-separated) to fetch them efficiently in one call (saves ~500 tokens per additional method). " +
    "Returns complete implementation including signature, body with line numbers, attributes, and XML documentation.")]
    public async Task<string> FetchMethodImplementation(
    [Description("Required: project name (e.g., 'LocalMcpServer', 'RisingTideAPI')")]
    string projectName,
    [Description("Required: relative path to C# file from project root (e.g., 'Services/NugetService.cs')")]
    string relativeFilePath,
    [Description("Required: method name(s) to fetch. Single method: 'GetUsers' OR Multiple methods (batch): 'GetUsers,UpdateUser,DeleteUser' (comma-separated, no spaces)")]
    string methodName,
    [Description("Optional: class name if multiple classes contain methods with the same name (e.g., 'NugetService')")]
    string? className = null)
    {
        try
        {
            // Check if batch mode (comma-separated method names)
            var methodNames = methodName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (methodNames.Length == 1)
            {
                // SINGLE MODE: Use existing logic
                var implementation = await _projectSkeletonService.FetchMethodImplementationAsync(
                    projectName, relativeFilePath, methodNames[0], className);

                return _methodFormatter.FormatMethodImplementation(implementation);
            }
            else
            {
                // BATCH MODE: Use new batch method
                var implementations = await _projectSkeletonService.FetchMethodImplementationsBatchAsync(
                    projectName, relativeFilePath, methodNames, className);

                return _methodFormatter.FormatMethodImplementationsBatch(implementations);
            }
        }
        catch (KeyNotFoundException)
        {
            var availableProjects = _projectSkeletonService.GetAvailableProjectsWithInfo();
            var projectList = string.Join("\n", availableProjects.Select(p =>
                $"• {p.Key} - {p.Value.Description}"));

            throw new ArgumentException(
                $"Project '{projectName}' not found.\n\n" +
                $"Available projects:\n{projectList}");
        }
        catch (FileNotFoundException ex)
        {
            throw new ArgumentException($"File not found: {ex.Message}");
        }
        catch (ArgumentException)
        {
            throw;
        }
    }
    #endregion

    [McpServerTool]
    [Description("Reads raw content of source code files and safe configuration files. " +
    "SECURITY: Blocks access to sensitive files (appsettings.json, secrets, credentials, .env files, etc.). " +
    "Use this for Program.cs, controllers, services, Dockerfile, and other non-sensitive files.")]
    public async Task<string> ReadFileContent(
    [Description("Required: project name (e.g., 'LocalMcpServer', 'RisingTideAPI')")]
    string projectName,
    [Description("Required: relative file path from project root (e.g., 'Program.cs', 'Controllers/MyController.cs'). " +
        "⚠️ Cannot access: appsettings.json, secrets.json, .env, credentials, or files in bin/obj/node_modules directories.")]
    string relativeFilePath)
    {
        try
        {
            var result = await _projectSkeletonService.ReadFileContentAsync(projectName, relativeFilePath);
            return _tomlSerializer.Serialize(result);
        }
        catch (FileAccessDeniedException ex)
        {
            // Return structured error response in TOML format
            var errorResult = new
            {
                Success = false,
                ErrorType = "FileAccessDenied",
                ProjectName = projectName,
                FilePath = relativeFilePath,
                Reason = ex.Reason,
                Message = ex.Message,
                Suggestions = new[]
                {
                "This file contains sensitive data and cannot be accessed for security reasons.",
                "If you need configuration structure (not values), use 'get_project_skeleton' instead.",
                "For code analysis, use 'analyze_c_sharp_file' for semantic understanding.",
                "Allowed file types: .cs, .csproj, .md, .txt, Program.cs, Dockerfile, etc.",
                "Blocked files: appsettings.json, secrets.json, .env, credential files, database files"
            }
            };

            return _tomlSerializer.Serialize(errorResult);
        }
        catch (FileNotFoundException ex)
        {
            var errorResult = new
            {
                Success = false,
                ErrorType = "FileNotFound",
                ProjectName = projectName,
                FilePath = relativeFilePath,
                Message = ex.Message,
                Suggestions = new[]
                {
                "Use 'get_project_skeleton' to see all available files in the project.",
                "Verify the file path is correct and uses forward slashes (/) or backslashes (\\).",
                "Check if the file exists in the project directory."
            }
            };

            return _tomlSerializer.Serialize(errorResult);
        }
    }
}

public class DateTimeResponse
{
    public string LocalDateTime { get; set; } = string.Empty;
    public string UtcDateTime { get; set; } = string.Empty;
    public string TimeZone { get; set; } = string.Empty;
    public long UnixTimestamp { get; set; }
}