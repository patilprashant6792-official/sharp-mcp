using MCP.Core.Models;

namespace MCP.Core.Services;

public interface IProjectConfigService
{
    /// <summary>
    /// Loads project configuration from projects.json
    /// </summary>
    ProjectConfiguration LoadProjects();

    /// <summary>
    /// Saves project configuration to projects.json
    /// </summary>
    Task SaveProjectsAsync(ProjectConfiguration config);

    /// <summary>
    /// Adds a new project (with validation)
    /// </summary>
    Task<ProjectValidationResult> AddProjectAsync(ProjectConfigEntry project);

    /// <summary>
    /// Updates existing project
    /// </summary>
    Task UpdateProjectAsync(ProjectConfigEntry project);

    /// <summary>
    /// Deletes project by ID
    /// </summary>
    Task DeleteProjectAsync(string projectId);

    /// <summary>
    /// Gets project by ID
    /// </summary>
    ProjectConfigEntry? GetProject(string projectId);

    /// <summary>
    /// Validates project path (checks for .sln, .csproj)
    /// </summary>
    Task<ProjectValidationResult> ValidateProjectPathAsync(string path);
}