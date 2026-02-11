namespace MCP.Core.FileUpdateService;

/// <summary>
/// Allows runtime registration/deregistration of project paths to watch.
/// Call this when a project is added or removed via the config UI.
/// </summary>
public interface IFileWatcherRegistry
{
    void RegisterProject(string projectName, string projectPath);
    void UnregisterProject(string projectName);
}