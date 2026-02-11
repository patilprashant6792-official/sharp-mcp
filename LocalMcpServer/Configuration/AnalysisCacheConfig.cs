namespace MCP.Core.Configuration;

public class AnalysisCacheConfig
{
    public const string SectionName = "AnalysisCache";

    /// <summary>How long each file analysis lives in Redis before expiring (hours). Default: 24.</summary>
    public int TtlHours { get; set; } = 24;

    /// <summary>How often the background indexer re-scans all projects (minutes). Default: 60.</summary>
    public int RefreshIntervalMinutes { get; set; } = 60;

    /// <summary>Max concurrent Roslyn parses during bulk indexing. Default: 4.</summary>
    public int IndexingConcurrency { get; set; } = 4;

    /// <summary>Milliseconds to wait after a file-change event before re-analysing (debounce). Default: 300.</summary>
    public int FileWatcherDebounceMs { get; set; } = 300;
}