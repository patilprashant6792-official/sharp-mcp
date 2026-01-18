using MCP.Core.Configuration;
using MCP.Core.Models;
using Microsoft.AspNetCore.Mvc;
using System.Reflection.Metadata;

namespace MCP.Core.Services;

public interface IProjectSkeletonService
{
    string GetToolDescription();

    Task<CSharpFileAnalysis> AnalyzeCSharpFileAsync(
        string projectName,
        string relativeFilePath,
        bool includePrivateMembers = false,
        CancellationToken cancellationToken = default);

    Task<MethodImplementationInfo> FetchMethodImplementationAsync(
        string projectName,
        string relativeFilePath,
        string methodName,
        string? className = null,
        CancellationToken cancellationToken = default);

    Task<string> GetProjectSkeletonAsync(
        string projectName,
        string? sinceTimestamp = null,
        CancellationToken cancellationToken = default);

    IReadOnlyDictionary<string, string> GetAvailableProjects();

    IReadOnlyDictionary<string, ProjectInfo> GetAvailableProjectsWithInfo();

    Task<FileContentResponse> ReadFileContentAsync(
        string projectName,
        string relativeFilePath,
        CancellationToken cancellationToken = default);

    Task<List<MethodImplementationInfo>> FetchMethodImplementationsBatchAsync(
        string projectName,
        string relativeFilePath,
        string[] methodNames,
        string? className = null,
        CancellationToken cancellationToken = default);

    Task<FolderSearchResponse> SearchFolderFilesAsync(
        string projectName,
        string folderPath,
        string? searchPattern = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default);
}