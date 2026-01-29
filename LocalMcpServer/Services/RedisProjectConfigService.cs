using MCP.Core.Models;
using StackExchange.Redis;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCP.Core.Services;

public partial class RedisProjectConfigService : IProjectConfigService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<RedisProjectConfigService> _logger;
    private readonly IDatabase _db;
    private const string ProjectsKey = "mcp:projects:config";
    private const string ProjectKeyPrefix = "mcp:project:";
    private const int MaxProjects = 10;

    public RedisProjectConfigService(
        IConnectionMultiplexer redis,
        ILogger<RedisProjectConfigService> logger)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _db = redis.GetDatabase();
    }

    public ProjectConfiguration LoadProjects()
    {
        try
        {
            var json = _db.StringGet(ProjectsKey);

            if (json.IsNullOrEmpty)
            {
                _logger.LogInformation("No projects found in Redis, returning empty configuration");
                return new ProjectConfiguration { MaxProjects = MaxProjects };
            }

            var config = JsonSerializer.Deserialize<ProjectConfiguration>(json.ToString())
                ?? new ProjectConfiguration { MaxProjects = MaxProjects };

            _logger.LogDebug("Loaded {Count} projects from Redis", config.Projects.Count);
            return config;
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Redis error while loading projects");
            throw new InvalidOperationException("Failed to load projects from Redis", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while loading projects");
            throw new InvalidOperationException("Failed to load projects configuration", ex);
        }
    }

    public async Task SaveProjectsAsync(ProjectConfiguration config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            var saved = await _db.StringSetAsync(ProjectsKey, json);

            if (!saved)
            {
                throw new InvalidOperationException("Failed to save projects to Redis");
            }

            // Also save individual project keys for quick lookup
            foreach (var project in config.Projects)
            {
                var projectKey = $"{ProjectKeyPrefix}{project.Id}";
                var projectJson = JsonSerializer.Serialize(project);
                await _db.StringSetAsync(projectKey, projectJson);
            }

            _logger.LogInformation("Saved {Count} projects to Redis", config.Projects.Count);
        }
        catch (RedisException ex)
        {
            _logger.LogError(ex, "Redis error while saving projects");
            throw new InvalidOperationException("Failed to save projects to Redis", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while saving projects");
            throw;
        }
    }

    public async Task<ProjectValidationResult> AddProjectAsync(ProjectConfigEntry project)
    {
        var config = LoadProjects();

        // Validate max projects limit
        if (config.Projects.Count >= MaxProjects)
        {
            return new ProjectValidationResult
            {
                IsValid = false,
                Error = $"Maximum of {MaxProjects} projects allowed"
            };
        }

        // Validate project name uniqueness
        if (config.Projects.Any(p =>
            p.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return new ProjectValidationResult
            {
                IsValid = false,
                Error = $"Project name '{project.Name}' already exists"
            };
        }

        // Validate project path
        var pathValidation = await ValidateProjectPathAsync(project.Path);
        if (!pathValidation.IsValid)
        {
            return pathValidation;
        }

        // Add project
        project.Id = Guid.NewGuid().ToString();
        project.AddedDate = DateTime.UtcNow;
        config.Projects.Add(project);

        await SaveProjectsAsync(config);

        return new ProjectValidationResult
        {
            IsValid = true,
            Metadata = pathValidation.Metadata
        };
    }

    public async Task UpdateProjectAsync(ProjectConfigEntry project)
    {
        var config = LoadProjects();
        var existing = config.Projects.FirstOrDefault(p => p.Id == project.Id);

        if (existing == null)
        {
            throw new KeyNotFoundException($"Project with ID '{project.Id}' not found");
        }

        // Validate name uniqueness (excluding current project)
        if (config.Projects.Any(p =>
            p.Id != project.Id &&
            p.Name.Equals(project.Name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Project name '{project.Name}' already exists");
        }

        // Validate path
        var pathValidation = await ValidateProjectPathAsync(project.Path);
        if (!pathValidation.IsValid)
        {
            throw new InvalidOperationException(pathValidation.Error ?? "Invalid path");
        }

        // Update
        existing.Name = project.Name;
        existing.Path = project.Path;
        existing.Description = project.Description;
        existing.Enabled = project.Enabled;

        await SaveProjectsAsync(config);
    }

    public async Task DeleteProjectAsync(string projectId)
    {
        var config = LoadProjects();
        var project = config.Projects.FirstOrDefault(p => p.Id == projectId);

        if (project == null)
        {
            throw new KeyNotFoundException($"Project with ID '{projectId}' not found");
        }

        config.Projects.Remove(project);
        await SaveProjectsAsync(config);

        // Also delete the individual project key
        var projectKey = $"{ProjectKeyPrefix}{projectId}";
        await _db.KeyDeleteAsync(projectKey);

        _logger.LogInformation("Deleted project {ProjectId}", projectId);
    }

    public ProjectConfigEntry? GetProject(string projectId)
    {
        try
        {
            // Try individual key first for performance
            var projectKey = $"{ProjectKeyPrefix}{projectId}";
            var json = _db.StringGet(projectKey);

            if (!json.IsNullOrEmpty)
            {
                return JsonSerializer.Deserialize<ProjectConfigEntry>(json.ToString());
            }

            // Fallback to loading all projects
            var config = LoadProjects();
            return config.Projects.FirstOrDefault(p => p.Id == projectId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving project {ProjectId}", projectId);
            return null;
        }
    }

    public async Task<ProjectValidationResult> ValidateProjectPathAsync(string path)
    {
        await Task.CompletedTask;

        // Basic validation
        if (string.IsNullOrWhiteSpace(path))
        {
            return new ProjectValidationResult
            {
                IsValid = false,
                Error = "Project path cannot be empty"
            };
        }

        // Sanitize path
        path = path.Trim();

        // Validate path characters (prevent injection)
        if (!IsValidPath(path))
        {
            return new ProjectValidationResult
            {
                IsValid = false,
                Error = "Project path contains invalid characters"
            };
        }

        // Check directory exists
        if (!Directory.Exists(path))
        {
            return new ProjectValidationResult
            {
                IsValid = false,
                Error = $"Directory does not exist: {path}"
            };
        }

        // Auto-detect project metadata
        var metadata = new ProjectMetadata();

        try
        {
            // Find solution files
            metadata.SolutionFiles = Directory.EnumerateFiles(path, "*.sln", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .ToList()!;

            var slnxFiles = Directory.EnumerateFiles(path, "*.slnx", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(f => f != null)
                .ToList()!;

            metadata.SolutionFiles.AddRange(slnxFiles);
            metadata.HasSolutionFile = metadata.SolutionFiles.Any();

            // Find csproj files (search subdirectories)
            metadata.CsprojFiles = Directory.EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories)
                .Where(f => !IsInExcludedDirectory(f))
                .Select(f => Path.GetRelativePath(path, f))
                .ToList();

            metadata.CsprojCount = metadata.CsprojFiles.Count;
            metadata.HasCsprojFiles = metadata.CsprojCount > 0;

            // Detect target framework from first .csproj
            if (metadata.CsprojFiles.Any())
            {
                var firstCsproj = Path.Combine(path, metadata.CsprojFiles.First());
                metadata.DetectedFramework = DetectTargetFramework(firstCsproj);
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new ProjectValidationResult
            {
                IsValid = false,
                Error = "Access denied to directory"
            };
        }
        catch (Exception ex)
        {
            return new ProjectValidationResult
            {
                IsValid = false,
                Error = $"Error scanning directory: {ex.Message}"
            };
        }

        // Validation: Must have at least .sln or .csproj
        if (!metadata.HasSolutionFile && !metadata.HasCsprojFiles)
        {
            return new ProjectValidationResult
            {
                IsValid = false,
                Error = "No solution (.sln/.slnx) or project (.csproj) files found"
            };
        }

        return new ProjectValidationResult
        {
            IsValid = true,
            Metadata = metadata
        };
    }

    private static bool IsValidPath(string path)
    {
        return PathValidationRegex().IsMatch(path);
    }

    private static bool IsInExcludedDirectory(string filePath)
    {
        var excludedDirs = new[] { "bin", "obj", ".vs", ".git", "node_modules", "packages" };
        var pathParts = filePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return pathParts.Any(part => excludedDirs.Contains(part, StringComparer.OrdinalIgnoreCase));
    }

    private static string? DetectTargetFramework(string csprojPath)
    {
        try
        {
            var content = File.ReadAllText(csprojPath);
            var match = TargetFrameworkRegex().Match(content);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[a-zA-Z0-9\\/:\\_\-\.\s]+$")]
    private static partial Regex PathValidationRegex();

    [GeneratedRegex(@"<TargetFramework>(.*?)</TargetFramework>", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkRegex();
}