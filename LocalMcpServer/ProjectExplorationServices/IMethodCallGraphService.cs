// ProjectExplorationServices/IMethodCallGraphService.cs

using MCP.Core.Models;

namespace MCP.Core.Services;

public interface IMethodCallGraphService
{
    /// <summary>
    /// Analyzes method dependencies - shows who calls this method
    /// </summary>
    Task<MethodCallGraph> AnalyzeMethodDependenciesAsync(
        string projectName,
        string relativeFilePath,
        string methodName,
        string? className = null,
        bool includeTests = false,
        int depth = 1,
        CancellationToken cancellationToken = default);
}