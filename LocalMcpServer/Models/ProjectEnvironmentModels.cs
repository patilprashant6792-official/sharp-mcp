using System.Text.Json.Serialization;

namespace MCP.Core.Models;

// ── Auth types ────────────────────────────────────────────────
public enum AuthType { None, Bearer, Basic }

public sealed class BearerAuth
{
    public string Token { get; set; } = string.Empty;
}

public sealed class BasicAuth
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class EnvironmentAuth
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AuthType Type { get; set; } = AuthType.None;

    public BearerAuth? Bearer { get; set; }
    public BasicAuth?  Basic  { get; set; }
}

// ── Core environment model ────────────────────────────────────
public sealed class ProjectEnvironment
{
    public string          Id             { get; set; } = Guid.NewGuid().ToString();
    public string          Name           { get; set; } = string.Empty;
    public string          BaseUrl        { get; set; } = string.Empty;
    public bool            IsDefault      { get; set; }
    public bool            SkipTlsVerify  { get; set; }
    public EnvironmentAuth Auth           { get; set; } = new();
    public DateTime        CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime        UpdatedAt      { get; set; } = DateTime.UtcNow;
}

// ── Request / response DTOs ───────────────────────────────────
public sealed class UpsertEnvironmentRequest
{
    public string          Name          { get; set; } = string.Empty;
    public string          BaseUrl       { get; set; } = string.Empty;
    public bool            IsDefault     { get; set; }
    public bool            SkipTlsVerify { get; set; }
    public EnvironmentAuth Auth          { get; set; } = new();
}

public sealed class EnvironmentListResponse
{
    public string                    ProjectId   { get; set; } = string.Empty;
    public string                    ProjectName { get; set; } = string.Empty;
    public List<ProjectEnvironment>  Environments { get; set; } = [];
}
