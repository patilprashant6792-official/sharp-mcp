using MCP.Core.Models;
using StackExchange.Redis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCP.Core.Services;

public sealed class RedisProjectEnvironmentService : IProjectEnvironmentService
{
    private readonly IDatabase                              _db;
    private readonly ILogger<RedisProjectEnvironmentService> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // Key pattern:  mcp:project:{projectId}:environments
    private static string EnvKey(string projectId) => $"mcp:project:{projectId}:environments";

    public RedisProjectEnvironmentService(
        IConnectionMultiplexer redis,
        ILogger<RedisProjectEnvironmentService> logger)
    {
        _db     = redis.GetDatabase();
        _logger = logger;
    }

    // ── Read ──────────────────────────────────────────────────
    public async Task<List<ProjectEnvironment>> GetEnvironmentsAsync(string projectId)
    {
        var raw = await _db.StringGetAsync(EnvKey(projectId));
        if (raw.IsNullOrEmpty) return [];

        return JsonSerializer.Deserialize<List<ProjectEnvironment>>(raw.ToString(), _json) ?? [];
    }

    public async Task<ProjectEnvironment?> GetEnvironmentAsync(string projectId, string envId)
    {
        var envs = await GetEnvironmentsAsync(projectId);
        return envs.FirstOrDefault(e => e.Id == envId);
    }

    public async Task<ProjectEnvironment?> GetDefaultEnvironmentAsync(string projectId)
    {
        var envs = await GetEnvironmentsAsync(projectId);
        return envs.FirstOrDefault(e => e.IsDefault) ?? envs.FirstOrDefault();
    }

    // ── Write ─────────────────────────────────────────────────
    public async Task<ProjectEnvironment> AddEnvironmentAsync(string projectId, UpsertEnvironmentRequest request)
    {
        ValidateRequest(request);

        var envs = await GetEnvironmentsAsync(projectId);

        if (envs.Any(e => e.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Environment '{request.Name}' already exists for this project.");

        var env = new ProjectEnvironment
        {
            Id            = Guid.NewGuid().ToString(),
            Name          = request.Name.Trim(),
            BaseUrl       = request.BaseUrl.Trim().TrimEnd('/'),
            IsDefault     = request.IsDefault || envs.Count == 0,   // first env is always default
            SkipTlsVerify = request.SkipTlsVerify,
            Auth          = request.Auth,
            CreatedAt     = DateTime.UtcNow,
            UpdatedAt     = DateTime.UtcNow,
        };

        if (env.IsDefault) ClearDefault(envs);
        envs.Add(env);

        await PersistAsync(projectId, envs);
        _logger.LogInformation("Added environment '{Name}' to project {ProjectId}", env.Name, projectId);
        return env;
    }

    public async Task<ProjectEnvironment> UpdateEnvironmentAsync(string projectId, string envId, UpsertEnvironmentRequest request)
    {
        ValidateRequest(request);

        var envs = await GetEnvironmentsAsync(projectId);
        var env  = envs.FirstOrDefault(e => e.Id == envId)
                   ?? throw new KeyNotFoundException($"Environment '{envId}' not found.");

        // Duplicate name check (excluding self)
        if (envs.Any(e => e.Id != envId && e.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Another environment named '{request.Name}' already exists.");

        if (request.IsDefault) ClearDefault(envs);

        env.Name          = request.Name.Trim();
        env.BaseUrl       = request.BaseUrl.Trim().TrimEnd('/');
        env.IsDefault     = request.IsDefault;
        env.SkipTlsVerify = request.SkipTlsVerify;
        env.Auth          = request.Auth;
        env.UpdatedAt     = DateTime.UtcNow;

        await PersistAsync(projectId, envs);
        _logger.LogInformation("Updated environment '{Name}' on project {ProjectId}", env.Name, projectId);
        return env;
    }

    public async Task DeleteEnvironmentAsync(string projectId, string envId)
    {
        var envs = await GetEnvironmentsAsync(projectId);
        var env  = envs.FirstOrDefault(e => e.Id == envId)
                   ?? throw new KeyNotFoundException($"Environment '{envId}' not found.");

        envs.Remove(env);

        // If deleted env was default, promote first remaining
        if (env.IsDefault && envs.Count > 0)
            envs[0].IsDefault = true;

        await PersistAsync(projectId, envs);
        _logger.LogInformation("Deleted environment '{Name}' from project {ProjectId}", env.Name, projectId);
    }

    public async Task SetDefaultAsync(string projectId, string envId)
    {
        var envs = await GetEnvironmentsAsync(projectId);
        var env  = envs.FirstOrDefault(e => e.Id == envId)
                   ?? throw new KeyNotFoundException($"Environment '{envId}' not found.");

        ClearDefault(envs);
        env.IsDefault = true;

        await PersistAsync(projectId, envs);
    }

    // ── Helpers ───────────────────────────────────────────────
    private async Task PersistAsync(string projectId, List<ProjectEnvironment> envs)
    {
        var json = JsonSerializer.Serialize(envs, _json);
        await _db.StringSetAsync(EnvKey(projectId), json);
    }

    private static void ClearDefault(List<ProjectEnvironment> envs)
    {
        foreach (var e in envs) e.IsDefault = false;
    }

    private static void ValidateRequest(UpsertEnvironmentRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
            throw new ArgumentException("Environment name is required.");

        if (string.IsNullOrWhiteSpace(r.BaseUrl))
            throw new ArgumentException("Base URL is required.");

        if (!Uri.TryCreate(r.BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("Base URL must be a valid http/https URL.");

        if (r.Auth.Type == AuthType.Bearer &&
            string.IsNullOrWhiteSpace(r.Auth.Bearer?.Token))
            throw new ArgumentException("Bearer token is required when auth type is Bearer.");

        if (r.Auth.Type == AuthType.Basic &&
            string.IsNullOrWhiteSpace(r.Auth.Basic?.Username))
            throw new ArgumentException("Username is required when auth type is Basic.");
    }
}
