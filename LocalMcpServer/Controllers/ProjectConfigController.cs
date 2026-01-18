using MCP.Core.Models;
using MCP.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace MCP.Host.Controllers;

[ApiController]
[Route("api/projects")]
public class ProjectConfigController : ControllerBase
{
    private readonly IProjectConfigService _configService;
    private readonly ILogger<ProjectConfigController> _logger;

    public ProjectConfigController(
        IProjectConfigService configService,
        ILogger<ProjectConfigController> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    /// <summary>
    /// Get all configured projects
    /// </summary>
    [HttpGet]
    public ActionResult<ProjectConfiguration> GetProjects()
    {
        try
        {
            var config = _configService.LoadProjects();
            return Ok(config);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load projects");
            return StatusCode(500, new { error = "Failed to load projects" });
        }
    }

    /// <summary>
    /// Get project by ID
    /// </summary>
    [HttpGet("{id}")]
    public ActionResult<ProjectConfigEntry> GetProject(string id)
    {
        try
        {
            var project = _configService.GetProject(id);
            if (project == null)
            {
                return NotFound(new { error = $"Project with ID '{id}' not found" });
            }

            return Ok(project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get project {ProjectId}", id);
            return StatusCode(500, new { error = "Failed to get project" });
        }
    }

    /// <summary>
    /// Add new project
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProjectValidationResult>> AddProject(
        [FromBody] ProjectConfigEntry project)
    {
        try
        {
            // Validate input
            if (string.IsNullOrWhiteSpace(project.Name))
            {
                return BadRequest(new { error = "Project name is required" });
            }

            if (string.IsNullOrWhiteSpace(project.Path))
            {
                return BadRequest(new { error = "Project path is required" });
            }

            var result = await _configService.AddProjectAsync(project);

            if (!result.IsValid)
            {
                return BadRequest(new { error = result.Error });
            }

            _logger.LogInformation(
                "Project added: {ProjectName} at {ProjectPath}",
                project.Name,
                project.Path);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add project");
            return StatusCode(500, new { error = "Failed to add project" });
        }
    }

    /// <summary>
    /// Update existing project
    /// </summary>
    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateProject(
        string id,
        [FromBody] ProjectConfigEntry project)
    {
        try
        {
            if (id != project.Id)
            {
                return BadRequest(new { error = "ID mismatch" });
            }

            if (string.IsNullOrWhiteSpace(project.Name))
            {
                return BadRequest(new { error = "Project name is required" });
            }

            if (string.IsNullOrWhiteSpace(project.Path))
            {
                return BadRequest(new { error = "Project path is required" });
            }

            await _configService.UpdateProjectAsync(project);

            _logger.LogInformation(
                "Project updated: {ProjectName} ({ProjectId})",
                project.Name,
                id);

            return Ok(new { message = "Project updated successfully" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update project {ProjectId}", id);
            return StatusCode(500, new { error = "Failed to update project" });
        }
    }

    /// <summary>
    /// Delete project
    /// </summary>
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteProject(string id)
    {
        try
        {
            await _configService.DeleteProjectAsync(id);

            _logger.LogInformation("Project deleted: {ProjectId}", id);

            return Ok(new { message = "Project deleted successfully" });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project {ProjectId}", id);
            return StatusCode(500, new { error = "Failed to delete project" });
        }
    }

    /// <summary>
    /// Validate project path
    /// </summary>
    [HttpPost("validate")]
    public async Task<ActionResult<ProjectValidationResult>> ValidatePath(
        [FromBody] PathValidationRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.Path))
            {
                return BadRequest(new { error = "Path is required" });
            }

            var result = await _configService.ValidateProjectPathAsync(request.Path);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate path");
            return StatusCode(500, new { error = "Failed to validate path" });
        }
    }
}

public class PathValidationRequest
{
    public string Path { get; set; } = string.Empty;
}