using MCP.Core.Models;
using MCP.Core.Services;
using Microsoft.AspNetCore.Mvc;

namespace MCP.Host.Controllers;

[ApiController]
[Route("api/projects/{projectId}/environments")]
public sealed class ProjectEnvironmentController : ControllerBase
{
    private readonly IProjectEnvironmentService _envService;
    private readonly IProjectConfigService      _projectService;
    private readonly ILogger<ProjectEnvironmentController> _logger;

    public ProjectEnvironmentController(
        IProjectEnvironmentService envService,
        IProjectConfigService projectService,
        ILogger<ProjectEnvironmentController> logger)
    {
        _envService     = envService;
        _projectService = projectService;
        _logger         = logger;
    }

    // GET /api/projects/{projectId}/environments
    [HttpGet]
    public async Task<IActionResult> GetEnvironments(string projectId)
    {
        var project = _projectService.GetProject(projectId);
        if (project is null) return NotFound(new { error = $"Project '{projectId}' not found." });

        var envs = await _envService.GetEnvironmentsAsync(projectId);
        return Ok(new EnvironmentListResponse
        {
            ProjectId    = projectId,
            ProjectName  = project.Name,
            Environments = envs,
        });
    }

    // GET /api/projects/{projectId}/environments/{envId}
    [HttpGet("{envId}")]
    public async Task<IActionResult> GetEnvironment(string projectId, string envId)
    {
        var env = await _envService.GetEnvironmentAsync(projectId, envId);
        if (env is null) return NotFound(new { error = $"Environment '{envId}' not found." });
        return Ok(env);
    }

    // POST /api/projects/{projectId}/environments
    [HttpPost]
    public async Task<IActionResult> AddEnvironment(string projectId, [FromBody] UpsertEnvironmentRequest request)
    {
        if (_projectService.GetProject(projectId) is null)
            return NotFound(new { error = $"Project '{projectId}' not found." });

        try
        {
            var env = await _envService.AddEnvironmentAsync(projectId, request);
            return CreatedAtAction(nameof(GetEnvironment), new { projectId, envId = env.Id }, env);
        }
        catch (ArgumentException ex)    { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // PUT /api/projects/{projectId}/environments/{envId}
    [HttpPut("{envId}")]
    public async Task<IActionResult> UpdateEnvironment(string projectId, string envId, [FromBody] UpsertEnvironmentRequest request)
    {
        try
        {
            var env = await _envService.UpdateEnvironmentAsync(projectId, envId, request);
            return Ok(env);
        }
        catch (KeyNotFoundException ex)  { return NotFound(new { error = ex.Message }); }
        catch (ArgumentException ex)     { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }

    // DELETE /api/projects/{projectId}/environments/{envId}
    [HttpDelete("{envId}")]
    public async Task<IActionResult> DeleteEnvironment(string projectId, string envId)
    {
        try
        {
            await _envService.DeleteEnvironmentAsync(projectId, envId);
            return Ok(new { message = "Environment deleted." });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    // POST /api/projects/{projectId}/environments/{envId}/set-default
    [HttpPost("{envId}/set-default")]
    public async Task<IActionResult> SetDefault(string projectId, string envId)
    {
        try
        {
            await _envService.SetDefaultAsync(projectId, envId);
            return Ok(new { message = "Default environment updated." });
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }
}
