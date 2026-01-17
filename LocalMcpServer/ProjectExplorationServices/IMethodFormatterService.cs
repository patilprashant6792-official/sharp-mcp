using MCP.Core.Configuration;
using MCP.Core.Models;

namespace MCP.Core.Services;

/// <summary>
/// Formats method implementation into human-readable Markdown
/// </summary>
public interface IMethodFormatterService
{
    /// <summary>
    /// Formats method implementation with line numbers and metadata
    /// </summary>
    string FormatMethodImplementation(MethodImplementationInfo methodInfo);

    string FormatMethodImplementationsBatch(List<MethodImplementationInfo> methods);

    string FormatMethodCallGraph(MethodCallGraph graph);
}