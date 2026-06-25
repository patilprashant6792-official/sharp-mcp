using MCP.Core.Models;

namespace MCP.Core.Services;

public interface IProjectEnvironmentService
{
    /// <summary>Returns all environments for a project, empty list if none exist.</summary>
    Task<List<ProjectEnvironment>> GetEnvironmentsAsync(string projectId);

    /// <summary>Returns a single environment by id, null if not found.</summary>
    Task<ProjectEnvironment?> GetEnvironmentAsync(string projectId, string envId);

    /// <summary>Returns the default environment for a project, null if none set.</summary>
    Task<ProjectEnvironment?> GetDefaultEnvironmentAsync(string projectId);

    /// <summary>Creates a new environment. Enforces unique name per project.</summary>
    Task<ProjectEnvironment> AddEnvironmentAsync(string projectId, UpsertEnvironmentRequest request);

    /// <summary>Updates an existing environment. Throws if not found.</summary>
    Task<ProjectEnvironment> UpdateEnvironmentAsync(string projectId, string envId, UpsertEnvironmentRequest request);

    /// <summary>Deletes an environment. Reassigns default if needed.</summary>
    Task DeleteEnvironmentAsync(string projectId, string envId);

    /// <summary>Sets the specified environment as default, clears all others.</summary>
    Task SetDefaultAsync(string projectId, string envId);
}
