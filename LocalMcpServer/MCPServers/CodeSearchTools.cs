using MCP.Core.Configuration;
using MCP.Core.Models;
using MCP.Core.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace RisingTideAI.Trade.MCP.Host.MCPServers;

[McpServerToolType]
public class CodeSearchTools
{
    private readonly ICodeSearchService _codeSearchService;
    private readonly ICodeSearchFormatterService _codeSearchFormatter;

    public CodeSearchTools(
        ICodeSearchService codeSearchService,
        ICodeSearchFormatterService codeSearchFormatter)
    {
        _codeSearchService = codeSearchService;
        _codeSearchFormatter = codeSearchFormatter;
    }

    [McpServerTool]
    [Description("Global code search across project(s) - finds classes, methods, properties, fields, interfaces by name or keyword. " +
        "WILDCARD SUPPORT: Pass projectName='*' to search across ALL configured projects simultaneously. " +
        "Returns ranked results with file locations and member types. " +
        "Examples: " +
        "• Single project: search_code_globally('RisingTideAPI', 'Redis') " +
        "• ALL projects: search_code_globally('*', 'Redis') ← Search everything! " +
        "Use cases: " +
        "• Security audits: search_code_globally('*', 'Authorize') " +
        "• Dependency analysis: search_code_globally('*', 'IUserService') " +
        "• Refactoring impact: Find all usages before renaming")]
    public async Task<string> SearchCodeGlobally(
        [Description("Required: Search query (class name, method name, keyword, e.g., 'Redis', 'UserService', 'Authorize')")]
        string query,

        [Description("Required: Project name OR '*' for ALL projects. Use '*' for cross-project analysis.")]
        string projectName,

        [Description("Optional: Filter by member type (Class/Interface/Method/Property/Field/All). Default: 'All'")]
        string memberType = "All",

        [Description("Optional: Case-sensitive search (default: false for broader matches)")]
        bool caseSensitive = false,

        [Description("Optional: Results per page (default: 20, max: 200). Use with 'page' to paginate large result sets.")]
        int pageSize = 20,

        [Description("Optional: Page number, 1-based (default: 1). Increment to retrieve subsequent pages.")]
        int page = 1,

        [Description("Optional: Maximum results to return — legacy alias for pageSize. Ignored when pageSize is set explicitly.")]
        int topK = 20)
    {
        try
        {
            if (!Enum.TryParse<CodeMemberType>(memberType, ignoreCase: true, out var parsedMemberType))
            {
                return $"❌ Invalid member type: '{memberType}'. Valid values: {string.Join(", ", Enum.GetNames<CodeMemberType>())}";
            }

            if (page < 1) page = 1;
            if (pageSize < 1 || pageSize > 200) pageSize = Math.Clamp(pageSize, 1, 200);

            var request = new CodeSearchRequest
            {
                ProjectName = projectName,
                Query = query,
                MemberType = parsedMemberType,
                CaseSensitive = caseSensitive,
                TopK = topK,
                Page = page,
                PageSize = pageSize
            };

            var response = await _codeSearchService.SearchGloballyAsync(request);
            return _codeSearchFormatter.FormatSearchResults(response);
        }
        catch (Exception ex)
        {
            return $"❌ Search failed: {ex.Message}";
        }
    }
}
