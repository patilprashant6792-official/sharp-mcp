using MCP.Core.HttpProbe;
using MCP.Core.Models;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;

namespace MCP.Host.Controllers;

[ApiController]
[Route("api/environments")]
public sealed class EnvironmentProbeController : ControllerBase
{
    private readonly ILogger<EnvironmentProbeController> _logger;

    public EnvironmentProbeController(ILogger<EnvironmentProbeController> logger)
        => _logger = logger;

    /// <summary>
    /// Lightweight reachability probe used by the UI Test button.
    /// Hits GET {baseUrl}/ — does NOT use IHttpProbeService (no project
    /// lookup needed here; auth/url are supplied directly by the form).
    /// POST /api/environments/probe
    /// </summary>
    [HttpPost("probe")]
    public async Task<IActionResult> Probe([FromBody] UpsertEnvironmentRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BaseUrl))
            return BadRequest(new { error = "BaseUrl is required." });

        using var handler = BuildHandler(request.SkipTlsVerify);
        using var client  = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        ApplyAuth(client, request.Auth);

        var sw = Stopwatch.StartNew();
        try
        {
            var res = await client.GetAsync(request.BaseUrl.TrimEnd('/') + "/");
            sw.Stop();
            return Ok(new { reachable = true, status = (int)res.StatusCode, durationMs = sw.ElapsedMilliseconds });
        }
        catch (TaskCanceledException)    { return Ok(new { reachable = false, error = "Request timed out (8s)" }); }
        catch (HttpRequestException ex)  { return Ok(new { reachable = false, error = ex.Message }); }
    }

    private static HttpClientHandler BuildHandler(bool skipTls) => new()
    {
        ServerCertificateCustomValidationCallback =
            skipTls ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator : null
    };

    private static void ApplyAuth(HttpClient client, EnvironmentAuth? auth)
    {
        if (auth is null) return;
        switch (auth.Type)
        {
            case AuthType.Bearer when auth.Bearer?.Token is { Length: > 0 } t:
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", t);
                break;
            case AuthType.Basic when auth.Basic is { } b:
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{b.Username}:{b.Password}")));
                break;
        }
    }
}
