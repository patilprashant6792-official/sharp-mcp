using MCP.Core.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCP.Core.Services;

public partial class ProjectConfigService : IProjectConfigService
{
    private readonly string _configFilePath;
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private const int MaxProjects = 10;

    public ProjectConfigService(IWebHostEnvironment env)
    {
        _configFilePath = Path.Combine(env.ContentRootPath, "projects.json");
        EnsureConfigFileExists();
    }

    private void EnsureConfigFileExists()
    {
        if (!File.Exists(_configFilePath))
        {
            var defaultConfig = new ProjectConfiguration
            {
                Projects = new List<ProjectConfigEntry>(),
                MaxProjects = MaxProjects
            };

            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_configFilePath, json);
        }
    }

    public ProjectConfiguration LoadProjects()
    {
        try
        {
            if (!File.Exists(_configFilePath))
            {
                return new ProjectConfiguration { MaxProjects = MaxProjects };
            }

            var json = File.ReadAllText(_configFilePath);
            return JsonSerializer.Deserialize<ProjectConfiguration>(json)
                   ?? new ProjectConfiguration { MaxProjects = MaxProjects };
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load projects configuration from {_configFilePath}", ex);
        }
    }

    public async Task SaveProjectsAsync(ProjectConfiguration config)
    {
        await _fileLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(_configFilePath, json);
        }
        finally
        {
            _fileLock.Release();
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
    }

    public ProjectConfigEntry? GetProject(string projectId)
    {
        var config = LoadProjects();
        return config.Projects.FirstOrDefault(p => p.Id == projectId);
    }

    public async Task<ProjectValidationResult> ValidateProjectPathAsync(string path)
    {
        await Task.CompletedTask; // Async for future enhancements

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
        // Allow: letters, numbers, backslash, forward slash, colon, underscore, hyphen, space, dot
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