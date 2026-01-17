using MCP.Core.Models;

namespace MCP.Core.Services;

public interface ICodeSearchService
{
    Task<CodeSearchResponse> SearchGloballyAsync(
        CodeSearchRequest request,
        CancellationToken cancellationToken = default);
}