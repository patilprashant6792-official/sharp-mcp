namespace MCP.Core.Models;

public class ProjectConfiguration
{
    public List<ProjectConfigEntry> Projects { get; set; } = new();
    public int MaxProjects { get; set; } = 10;
}

public class ProjectConfigEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public DateTime AddedDate { get; set; } = DateTime.UtcNow;
}

public class ProjectValidationResult
{
    public bool IsValid { get; set; }
    public string? Error { get; set; }
    public ProjectMetadata? Metadata { get; set; }
}

public class ProjectMetadata
{
    public bool HasSolutionFile { get; set; }
    public bool HasCsprojFiles { get; set; }
    public string? DetectedFramework { get; set; }
    public int CsprojCount { get; set; }
    public List<string> SolutionFiles { get; set; } = new();
    public List<string> CsprojFiles { get; set; } = new();
}