using MCP.Core.Models;
using MCP.Core.Services;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace MCP.Core.HttpProbe;

public sealed class HttpProbeService : IHttpProbeService
{
    private readonly IProjectEnvironmentService             _envService;
    private readonly IProjectConfigService                  _projService;
    private readonly ILogger<HttpProbeService>              _logger;

    // Headers safe to forward to Claude — never echo auth
    private static readonly HashSet<string> _safeHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "content-type", "content-length", "x-request-id",
            "x-correlation-id", "x-trace-id", "date",
            "cache-control", "etag", "last-modified",
            "x-powered-by", "server",
        };

    public HttpProbeService(
        IProjectEnvironmentService envService,
        IProjectConfigService projService,
        ILogger<HttpProbeService> logger)
    {
        _envService  = envService;
        _projService = projService;
        _logger      = logger;
    }

    public async Task<HttpProbeResult> ExecuteAsync(HttpProbeRequest req)
    {
        // ── 1. Resolve project ────────────────────────────────
        var project = _projService.GetProject(req.ProjectName)
            ?? _projService.LoadProjects().Projects
                .FirstOrDefault(p => p.Name.Equals(req.ProjectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Project '{req.ProjectName}' not found. Register it first via the Projects UI.");

        // ── 2. Resolve environment ────────────────────────────
        var env = string.IsNullOrWhiteSpace(req.Environment)
            ? await _envService.GetDefaultEnvironmentAsync(project.Id)
            : (await _envService.GetEnvironmentsAsync(project.Id))
                .FirstOrDefault(e => e.Name.Equals(req.Environment, StringComparison.OrdinalIgnoreCase));

        if (env is null)
            throw new InvalidOperationException(
                $"No environment found for project '{req.ProjectName}'. " +
                $"Add one via the Environments UI first.");

        // ── 3. Build URL ──────────────────────────────────────
        var path       = req.Path.StartsWith('/') ? req.Path : '/' + req.Path;
        var requestUrl = env.BaseUrl.TrimEnd('/') + path;

        // ── 4. Build HTTP client ──────────────────────────────
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = env.SkipTlsVerify
                ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                : null,
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        ApplyAuth(client, env.Auth);

        // Caller-supplied headers (e.g. Accept, custom headers)
        foreach (var (k, v) in req.Headers)
            client.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

        // ── 5. Build request message ──────────────────────────
        var method  = new HttpMethod(req.Method.ToUpperInvariant());
        var message = new HttpRequestMessage(method, requestUrl);

        if (!string.IsNullOrWhiteSpace(req.Body))
            message.Content = new StringContent(req.Body, Encoding.UTF8, "application/json");

        // ── 6. Execute ────────────────────────────────────────
        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message);
        }
        catch (TaskCanceledException)
        {
            return Failure(req, env.Name, requestUrl, "Request timed out (30s)");
        }
        catch (HttpRequestException ex)
        {
            return Failure(req, env.Name, requestUrl, ex.Message);
        }
        finally { sw.Stop(); }

        // ── 7. Read + truncate response body ──────────────────
        var rawBytes   = await response.Content.ReadAsByteArrayAsync();
        var totalBytes = rawBytes.Length;
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        var (body, truncated, stats) = ShapeResponse(rawBytes, contentType, req.MaxBodyBytes, req.TopK);

        // ── 8. Safe headers subset ────────────────────────────
        var headers = new Dictionary<string, string>();
        foreach (var h in response.Headers.Concat(response.Content.Headers))
        {
            if (_safeHeaders.Contains(h.Key))
                headers[h.Key] = string.Join(", ", h.Value);
        }

        _logger.LogInformation(
            "[HttpProbe] {Method} {Url} → {Status} in {Ms}ms (env:{Env})",
            req.Method, requestUrl, (int)response.StatusCode, sw.ElapsedMilliseconds, env.Name);

        return new HttpProbeResult
        {
            Success     = response.IsSuccessStatusCode,
            Status      = (int)response.StatusCode,
            StatusText  = response.ReasonPhrase ?? string.Empty,
            DurationMs  = sw.ElapsedMilliseconds,
            Method      = req.Method.ToUpperInvariant(),
            Url         = requestUrl,
            Environment = env.Name,
            Project     = project.Name,
            Body        = body,
            Truncated   = truncated,
            TotalBytes  = totalBytes,
            Stats       = stats,
            Headers     = headers,
        };
    }

    // ── Response shaping ──────────────────────────────────────
    private static (string body, bool truncated, ResponseStats stats)
        ShapeResponse(byte[] rawBytes, string contentType, int maxBytes, int topK)
    {
        if (rawBytes.Length == 0)
            return (string.Empty, false, new ResponseStats { Shape = "empty", ContentType = contentType });

        var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        var isText = isJson
            || contentType.Contains("text", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml",  StringComparison.OrdinalIgnoreCase);

        if (!isText)
        {
            // Binary — return size info only, no body
            return (string.Empty, true, new ResponseStats
            {
                Shape       = "binary",
                ContentType = contentType,
            });
        }

        var raw      = Encoding.UTF8.GetString(rawBytes);
        var truncated = rawBytes.Length > maxBytes;
        var preview   = truncated ? raw[..maxBytes] : raw;

        if (!isJson)
        {
            // Plain text — line stats
            var lines     = raw.Split('\n');
            var shownLines = lines.Take(topK);
            return (string.Join('\n', shownLines), truncated, new ResponseStats
            {
                Shape       = "text",
                ContentType = contentType,
                LineCount   = lines.Length,
                TopKShown   = Math.Min(topK, lines.Length),
            });
        }

        // ── JSON shaping ──────────────────────────────────────
        try
        {
            using var doc = JsonDocument.Parse(raw);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var all   = doc.RootElement.EnumerateArray().ToList();
                var shown = all.Take(topK).ToList();
                var opts  = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                var body  = JsonSerializer.Serialize(
                    shown.Select(e => e.Clone()), opts);

                return (body, truncated || all.Count > topK, new ResponseStats
                {
                    Shape       = "array",
                    ContentType = contentType,
                    ArrayCount  = all.Count,
                    TopKShown   = shown.Count,
                });
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var keys = doc.RootElement
                    .EnumerateObject()
                    .Select(p => p.Name)
                    .ToList();

                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                var body = truncated
                    ? preview + "\n... [truncated]"
                    : JsonSerializer.Serialize(doc.RootElement.Clone(), opts);

                return (body, truncated, new ResponseStats
                {
                    Shape        = "object",
                    ContentType  = contentType,
                    TopLevelKeys = keys,
                });
            }
        }
        catch (JsonException)
        {
            // Not valid JSON despite content-type — fall through to plain text
        }

        return (preview, truncated, new ResponseStats
        {
            Shape       = "text",
            ContentType = contentType,
            LineCount   = raw.Split('\n').Length,
        });
    }

    // ── Auth injection ────────────────────────────────────────
    private static void ApplyAuth(HttpClient client, EnvironmentAuth? auth)
    {
        if (auth is null) return;
        switch (auth.Type)
        {
            case AuthType.Bearer when auth.Bearer?.Token is { Length: > 0 } t:
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", t);
                break;

            case AuthType.Basic when auth.Basic is { } b:
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{b.Username}:{b.Password}"));
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", encoded);
                break;
        }
    }

    private static HttpProbeResult Failure(
        HttpProbeRequest req, string envName, string url, string error) => new()
    {
        Success     = false,
        Method      = req.Method.ToUpperInvariant(),
        Url         = url,
        Environment = envName,
        Project     = req.ProjectName,
        Error       = error,
    };
}
