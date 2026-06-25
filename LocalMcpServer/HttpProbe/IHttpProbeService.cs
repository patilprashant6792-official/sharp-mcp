namespace MCP.Core.HttpProbe;

public interface IHttpProbeService
{
    Task<HttpProbeResult> ExecuteAsync(HttpProbeRequest request);
}
