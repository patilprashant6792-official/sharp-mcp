using MCP.Core.Models;
using MCP.Core.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace RisingTideAI.Trade.MCP.Host.MCPServers;

[McpServerToolType]
public class ProjectSkeletonTools
{
    private readonly IProjectSkeletonService _projectSkeletonService;

    public ProjectSkeletonTools(IProjectSkeletonService projectSkeletonService)
    {
        _projectSkeletonService = projectSkeletonService;
    }

    [McpServerTool]
    [Description("Retrieves complete project structure: ASCII folder tree, .sln/.csproj files, and file metadata. " +
        "Shows architecture patterns, NuGet packages, and file sizes. " +
        "WILDCARD: Pass projectName='*' to list all available projects. " +
        "INCREMENTAL: Use sinceTimestamp for files modified after specific date. " +
        "Use this BEFORE any project modifications to understand existing structure.")]
    public async Task<string> GetProjectSkeleton(
        [Description("Required: Project name (e.g., 'RisingTideAPI', 'LocalMcpServer'). Use '*' to list all projects.")]
        string projectName,
        [Description("Optional: Unix timestamp or ISO 8601 date (e.g., '2026-01-17T00:00:00Z'). Returns only files modified after this date.")]
        string? sinceTimestamp = null)
    {
        try
        {
            return await _projectSkeletonService.GetProjectSkeletonAsync(projectName, sinceTimestamp);
        }
        catch (KeyNotFoundException)
        {
            var availableProjects = _projectSkeletonService.GetAvailableProjectsWithInfo();
            var projectList = string.Join("\n", availableProjects.Select(p =>
                $"• {p.Key} - {p.Value.Description}"));

            return
                $"Available projects:\n{projectList}";
        }
    }

    [McpServerTool]
    [Description("Search and paginate files within a specific folder. " +
        "CRITICAL: Use this when get_project_skeleton shows '[...50+ files - use search_folder_files]'. " +
        "Supports filtering by filename pattern (case-insensitive). " +
        "Returns sorted, paginated results with file metadata. " +
        "Example: Find all 'Transaction' files in Entities/Exchange folder.")]
    public async Task<string> SearchFolderFiles(
        [Description("Required: Project name")]
        string projectName,

        [Description("Required: Relative folder path from project root (e.g., 'Entities/Exchange', 'Controllers')")]
        string folderPath,

        [Description("Optional: Filter by filename (case-insensitive, e.g., 'Company', 'Transaction'). Leave empty to list all.")]
        string? searchPattern = null,

        [Description("Optional: Page number (default: 1)")]
        int page = 1,

        [Description("Optional: Files per page (default: 50, max: 200)")]
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

            return FormatFolderSearchAsMarkdown(result);
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

    private static string FormatFolderSearchAsMarkdown(FolderSearchResponse result)
    {
        var sb = new System.Text.StringBuilder();

        var filterLabel = string.IsNullOrWhiteSpace(result.SearchPattern)
            ? string.Empty
            : $" (filter: `{result.SearchPattern}`)";

        sb.AppendLine($"## Folder: `{result.FolderPath}`{filterLabel}");
        sb.AppendLine($"**Project:** {result.ProjectName}");
        sb.AppendLine();
        sb.AppendLine($"Showing {result.Files.Count} of {result.TotalFiles} file(s) — page {result.Page}/{result.TotalPages}");
        sb.AppendLine();

        if (result.Files.Count == 0)
        {
            sb.AppendLine("_No files found._");
            return sb.ToString();
        }

        sb.AppendLine("## Legend");
        sb.AppendLine("- ✓ **Small file** (≤15KB) - Use `read_file_content`");
        sb.AppendLine("- ⚠️ **Large file** (>15KB) - Use `analyze_c_sharp_file` + `fetch_method_implementation`");
        sb.AppendLine();

        foreach (var f in result.Files)
        {
            var sizeTag = f.IsLargeFile ? "⚠️" : "✓";
            sb.AppendLine($"- {sizeTag} `{f.RelativePath}` ({f.SizeDisplay}, {f.LineCount} lines)");
        }

        if (result.HasNextPage)
            sb.AppendLine($"\n_Next page: call `search_folder_files` with `page={result.Page + 1}`_");
        if (result.HasPreviousPage)
            sb.AppendLine($"_Previous page: `page={result.Page - 1}`_");

        return sb.ToString();
    }
}
