using MCP.Core.Services;
using ModelContextProtocol.Server;
using StackExchange.Redis;
using System.ComponentModel;
using System.Reflection.Metadata;
using System.Threading.Channels;

namespace RisingTideAI.Trade.MCP.Host.MCPServers;

[McpServerToolType]
public class MethodCallGraphTools
{
    private readonly IMethodCallGraphService _callGraphService;
    private readonly IMethodFormatterService _methodFormatter;

    public MethodCallGraphTools(
        IMethodCallGraphService callGraphService,
        IMethodFormatterService methodFormatter)
    {
        _callGraphService = callGraphService;
        _methodFormatter = methodFormatter;
    }

    [McpServerTool]
    [Description("Analyzes method call graph - shows WHO calls this method and WHERE exactly. CRITICAL for understanding impact before modifying methods. Returns caller locations with exact file paths, line numbers, and class resolution hints. Use this BEFORE:"
 +" - Changing method signatures(prevents breaking changes)"
 +" - Modifying method behavior(impact analysis)"
 +" - Deleting methods(dependency verification)"
 +" - Renaming methods(find all references)"
+"Example: Before changing GetUser() signature, verify no other services depend on specific parameter order.")]
    public async Task<string> AnalyzeMethodCallGraph(
        [Description("Required: Project name")]
        string projectName,

        [Description("Required: Relative file path where method is defined (e.g., 'Services/UserService.cs')")]
        string relativeFilePath,

        [Description("Required: Method name to analyze (e.g., 'GetUser', 'ProcessOrder')")]
        string methodName,

        [Description("Optional: Class name if file has multiple classes")]
        string? className = null,

        [Description("Optional: Include test files in analysis (default: false, production code only)")]
        bool includeTests = false,

        [Description("Optional: Results per page for CalledBy list (default: 20, max: 200). Use with 'page' when a method has many callers.")]
        int pageSize = 20,

        [Description("Optional: Page number, 1-based (default: 1). Increment to see more callers.")]
        int page = 1)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 200) pageSize = Math.Clamp(pageSize, 1, 200);

            var graph = await _callGraphService.AnalyzeMethodDependenciesAsync(
                projectName,
                relativeFilePath,
                methodName,
                className,
                includeTests,
                depth: 1,
                page: page,
                pageSize: pageSize);

            return _methodFormatter.FormatMethodCallGraph(graph);
        }
        catch (Exception ex)
        {
            return $"❌ Error analyzing method call graph: {ex.Message}";
        }
    }
}
