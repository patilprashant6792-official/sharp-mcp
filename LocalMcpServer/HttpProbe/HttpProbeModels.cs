namespace MCP.Core.HttpProbe;

/// <summary>Full result returned to the MCP tool and Claude.</summary>
public sealed class HttpProbeResult
{
    public bool    Success     { get; init; }
    public int     Status      { get; init; }
    public string  StatusText  { get; init; } = string.Empty;
    public long    DurationMs  { get; init; }
    public string  Method      { get; init; } = string.Empty;
    public string  Environment { get; init; } = string.Empty;
    public string  Project     { get; init; } = string.Empty;

    // URL is intentionally omitted from result — base URL stays internal
    internal string Url { get; init; } = string.Empty;

    // Response body — always present, truncated when large
    public string  Body        { get; init; } = string.Empty;
    public bool    Truncated   { get; init; }
    public int     TotalBytes  { get; init; }

    // Structured stats — shape depends on content type
    public ResponseStats Stats { get; init; } = new();

    // Response headers (safe subset — no auth echoed back)
    public Dictionary<string, string> Headers { get; init; } = [];

    // Error message when Success = false
    public string? Error { get; init; }
}

/// <summary>
/// Content-aware stats so Claude can verify correctness
/// without reading the full payload.
/// </summary>
public sealed class ResponseStats
{
    public string ContentType  { get; init; } = string.Empty;

    // JSON array responses
    public int?   ArrayCount   { get; init; }
    public int?   TopKShown    { get; init; }

    // JSON object responses
    public List<string> TopLevelKeys { get; init; } = [];

    // Plain-text responses
    public int?   LineCount    { get; init; }

    // Universal
    public string Shape        { get; init; } = string.Empty;  // "array" | "object" | "text" | "empty" | "binary"
}

/// <summary>Request descriptor passed to the probe service.</summary>
public sealed class HttpProbeRequest
{
    public string  ProjectName  { get; init; } = string.Empty;
    public string? Environment  { get; init; }          // null → use default
    public string  Method       { get; init; } = "GET";
    public string  Path         { get; init; } = "/";   // relative to baseUrl
    public string? Body         { get; init; }          // request body (POST/PUT/PATCH)
    public Dictionary<string, string> Headers { get; init; } = [];
    public int     TopK         { get; init; } = 5;     // max array items / text lines shown
    public int     MaxBodyBytes { get; init; } = 2048;  // truncation threshold
}
