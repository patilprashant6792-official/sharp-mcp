using MCP.Core.HttpProbe;
using MCP.Core.Models;
using MCP.Core.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace RisingTideAI.Trade.MCP.Host.MCPServers;

[McpServerToolType]
public sealed class HttpProbeTools(
    IHttpProbeService probe,
    IProjectConfigService projects,
    IProjectEnvironmentService environments)
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [McpServerTool]
    [Description(
        "List environment names configured for a project.\n" +
        "Use this when you need to know which environments exist (e.g. 'local', 'staging')\n" +
        "or to confirm a default is set before calling http_request.\n" +
        "Project names are already known from get_project_skeleton — pass one directly.\n\n" +
        "Base URLs, auth tokens, and credentials are NEVER returned.\n\n" +
        "RETURNS: array of projects each with:\n" +
        "  projectName    — exact string to use in http_request\n" +
        "  environments[]\n" +
        "    name         — pass this as the environment param in http_request\n" +
        "    isDefault    — true = used automatically when environment param is omitted")]
    public async Task<string> ListEnvironments(
        [Description("Project name from get_project_skeleton. Omit to check all projects.")]
        string? projectName = null)
    {
        var config   = projects.LoadProjects();
        var filtered = string.IsNullOrWhiteSpace(projectName)
            ? config.Projects
            : config.Projects
                .Where(p => p.Name.Equals(projectName.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (filtered.Count == 0)
        {
            var hint = string.IsNullOrWhiteSpace(projectName)
                ? "No projects registered. Add one via the Projects UI."
                : $"Project '{projectName}' not found. Verify the name with get_project_skeleton.";
            return JsonSerializer.Serialize(new { error = hint }, _json);
        }

        var result = new List<object>();
        foreach (var project in filtered)
        {
            var envs = await environments.GetEnvironmentsAsync(project.Id);
            result.Add(new
            {
                projectName  = project.Name,
                environments = envs.Select(e => new
                {
                    name      = e.Name,
                    isDefault = e.IsDefault,
                }).ToList(),
            });
        }

        return JsonSerializer.Serialize(result, _json);
    }


    // ── Tool 2: Execute request ───────────────────────────────
    [McpServerTool]
    [Description(
        "Execute an HTTP request against a configured project environment.\n" +
        "Auth (Bearer/Basic) and base URL are resolved automatically from saved config — never visible to Claude.\n\n" +
        "If environment is omitted, the project's default environment is used automatically.\n" +
        "Only call list_environments first if you need to choose a specific non-default environment.\n\n" +
        "WORKFLOW:\n" +
        "  1. edit_lines                 — make code changes\n" +
        "  2. execute_dotnet_command     — build\n" +
        "  3. http_request               — verify (default env used automatically)\n" +
        "  4. Check stats + body, iterate if needed\n\n" +
        "PARAMETERS:\n" +
        "  projectName  — exact project name from get_project_skeleton (case-insensitive)\n" +
        "  path         — relative path + optional query string, e.g. '/api/orders?page=1'\n" +
        "  method       — GET | POST | PUT | PATCH | DELETE (default: GET)\n" +
        "  body         — JSON string for POST/PUT/PATCH (optional)\n" +
        "  environment  — env name from list_environments. Omit to use the default.\n" +
        "  topK         — max array items / text lines in preview (default: 5)\n" +
        "  maxBodyBytes — truncation threshold in bytes (default: 2048)\n\n" +
        "RETURNS:\n" +
        "  success, status, statusText, durationMs, method, environment, project\n" +
        "  body         — response preview (truncated to maxBodyBytes)\n" +
        "  truncated    — true when full response exceeded maxBodyBytes\n" +
        "  totalBytes   — full response size in bytes\n" +
        "  stats:\n" +
        "    shape        — 'array' | 'object' | 'text' | 'empty' | 'binary'\n" +
        "    arrayCount   — total items in a JSON array response\n" +
        "    topKShown    — items shown in body preview\n" +
        "    topLevelKeys — keys present in a JSON object response\n" +
        "    lineCount    — line count for plain-text responses\n" +
        "  headers      — safe response headers (content-type, etag, cache-control etc.)\n" +
        "  error        — set when success=false (timeout, connection refused, 4xx/5xx)")]
    public async Task<string> HttpRequest(
        [Description("Project name from list_environments (case-insensitive)")]
        string projectName,

        [Description("Relative path with optional query string. E.g. '/api/orders', '/api/users?page=1'")]
        string path,

        [Description("HTTP method: GET, POST, PUT, PATCH, DELETE (default: GET)")]
        string method = "GET",

        [Description("JSON body string for POST/PUT/PATCH. Omit for GET/DELETE.")]
        string? body = null,

        [Description("Environment name from list_environments. Omit to use the default environment.")]
        string? environment = null,

        [Description("Max array items or text lines in body preview (default: 5, max: 50)")]
        int topK = 5,

        [Description("Byte limit before truncation (default: 2048, max: 32768)")]
        int maxBodyBytes = 2048)
    {
        var request = new HttpProbeRequest
        {
            ProjectName  = projectName,
            Environment  = environment,
            Method       = method,
            Path         = path,
            Body         = body,
            TopK         = Math.Clamp(topK, 1, 50),
            MaxBodyBytes = Math.Clamp(maxBodyBytes, 256, 32768),
        };

        try
        {
            var result = await probe.ExecuteAsync(request);
            return JsonSerializer.Serialize(result, _json);
        }
        catch (KeyNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _json);
        }
        catch (InvalidOperationException ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message }, _json);
        }
    }
}
