using MCP.Core.Configuration;
using MCP.Core.Services;
using System.Collections.Concurrent;
using System.Text;

namespace MCP.Core.FileModificationService;

public sealed class FileModificationService : IFileModificationService
{
    private readonly IProjectConfigService _config;
    private readonly IAnalysisCacheService _cache;
    private readonly ILogger<FileModificationService> _logger;

    // Per-absolute-path semaphore — prevents concurrent writes to the same file
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();

    private static readonly char[] PathSeparators = ['/', '\\'];

    public FileModificationService(
        IProjectConfigService config,
        IAnalysisCacheService cache,
        ILogger<FileModificationService> logger)
    {
        _config = config;
        _cache = cache;
        _logger = logger;
    }

    // ── Guard ─────────────────────────────────────────────────────────────────

    public string ResolveAndGuard(string projectName, string relativeFilePath)
    {
        var project = _config.LoadProjects().Projects
            .FirstOrDefault(p => p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Project '{projectName}' not found. Use get_project_skeleton('*') to list available projects.");

        var root = Path.GetFullPath(project.Path);
        var absolute = Path.GetFullPath(Path.Combine(root, relativeFilePath));

        // Traversal guard — must be inside project root
        if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !absolute.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new FileAccessDeniedException(relativeFilePath, "Path traversal detected.");

        // Blocked directory segments
        var segments = absolute[root.Length..].Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (FileAccessPolicy.BlockedDirectories.Contains(seg))
                throw new FileAccessDeniedException(relativeFilePath,
                    $"Access to '{seg}' directory is blocked.");
        }

        // Blocked filename patterns (wildcard: * only)
        var filename = Path.GetFileName(absolute);
        foreach (var pattern in FileAccessPolicy.BlockedPatterns)
        {
            if (WildcardMatch(filename, pattern))
                throw new FileAccessDeniedException(relativeFilePath,
                    $"Filename matches blocked pattern '{pattern}'.");
        }

        return absolute;
    }

    // ── WriteFiles ────────────────────────────────────────────────────────────

    public async Task<BatchOperationResult> WriteFilesAsync(string projectName, List<WriteFileEntry> files)
    {
        var results = new List<FileOperationResult>();

        foreach (var entry in files)
        {
            try
            {
                var absolute = ResolveAndGuard(projectName, entry.RelativeFilePath);
                var exists = File.Exists(absolute);

                if (entry.Mode == WriteMode.Create && exists)
                {
                    results.Add(Fail(entry.RelativeFilePath,
                        "File already exists. Use mode Overwrite or Upsert."));
                    continue;
                }

                if (entry.Mode == WriteMode.Overwrite && !exists)
                {
                    results.Add(Fail(entry.RelativeFilePath,
                        "File does not exist. Use mode Create or Upsert."));
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

                var sem = GetLock(absolute);
                await sem.WaitAsync();
                try
                {
                    await File.WriteAllTextAsync(absolute, entry.Content, Encoding.UTF8);
                }
                finally { sem.Release(); }

                await InvalidateCacheAsync(projectName, entry.RelativeFilePath);

                var lineCount = CountLines(entry.Content);
                _logger.LogInformation("WriteFile: {Project}/{Path} ({Mode}, {Lines} lines)",
                    projectName, entry.RelativeFilePath, entry.Mode, lineCount);

                results.Add(new FileOperationResult
                {
                    RelativeFilePath = entry.RelativeFilePath,
                    Success = true,
                    LineCount = lineCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WriteFile failed: {Project}/{Path}", projectName, entry.RelativeFilePath);
                results.Add(Fail(entry.RelativeFilePath, ex.Message));
            }
        }

        return new BatchOperationResult { Results = results };
    }

    // ── EditLines ─────────────────────────────────────────────────────────────

    public async Task<BatchOperationResult> EditLinesAsync(
        string projectName, string relativeFilePath, List<PatchOperation> patches)
    {
        var absolute = ResolveAndGuard(projectName, relativeFilePath);

        if (!File.Exists(absolute))
            throw new FileNotFoundException($"File not found: {relativeFilePath}");

        if (patches.Count == 0)
            throw new ArgumentException("At least one patch operation is required.");

        var sem = GetLock(absolute);
        await sem.WaitAsync();
        try
        {
            var lines = (await File.ReadAllLinesAsync(absolute, Encoding.UTF8)).ToList();
            var total = lines.Count;

            // ── Validate all patches before touching anything ─────────────
            var ranged = patches.Where(p => p.Action != PatchAction.Append).ToList();

            foreach (var p in ranged)
            {
                switch (p.Action)
                {
                    case PatchAction.Patch:
                    case PatchAction.Delete:
                        if (p.StartLine < 1 || p.StartLine > total)
                            throw new ArgumentException(
                                $"{p.Action}: startLine {p.StartLine} out of range (file has {total} lines).");
                        if (p.EndLine < p.StartLine)
                            throw new ArgumentException(
                                $"{p.Action}: endLine {p.EndLine} must be >= startLine {p.StartLine}.");
                        if (p.EndLine > total)
                            throw new ArgumentException(
                                $"{p.Action}: endLine {p.EndLine} out of range (file has {total} lines).");
                        if (p.Action == PatchAction.Patch && p.Content is null)
                            throw new ArgumentException("Patch action requires Content.");
                        break;

                    case PatchAction.Insert:
                        if (p.StartLine < 0 || p.StartLine > total)
                            throw new ArgumentException(
                                $"Insert: startLine {p.StartLine} out of range. Use 0 to prepend.");
                        if (p.Content is null)
                            throw new ArgumentException("Insert action requires Content.");
                        break;
                }
            }

            // ── Overlap detection ─────────────────────────────────────────
            // Insert at startLine N inserts *after* line N, so model it as the
            // half-open point N+1. A Patch/Delete range [s,e] conflicts when s <= N+1 <= e,
            // i.e. the existing EndLine >= NextStartLine check covers it naturally once
            // Insert is included in the sorted list with StartLine = p.StartLine + 1.
            var sorted = ranged
                .Select(p => p.Action == PatchAction.Insert
                    ? new PatchOperation(p.Action, p.StartLine + 1, p.StartLine + 1, p.Content)
                    : p)
                .OrderBy(p => p.StartLine)
                .ToList();

            for (var i = 0; i < sorted.Count - 1; i++)
            {
                if (sorted[i].EndLine >= sorted[i + 1].StartLine)
                {
                    // Reconstruct readable labels from the originals
                    var a = patches.Where(p => p.Action != PatchAction.Append).OrderBy(p => p.StartLine).ToList()[i];
                    var b = patches.Where(p => p.Action != PatchAction.Append).OrderBy(p => p.StartLine).ToList()[i + 1];
                    var labelA = a.Action == PatchAction.Insert
                        ? $"Insert after line {a.StartLine}"
                        : $"[{a.StartLine}-{a.EndLine}]";
                    var labelB = b.Action == PatchAction.Insert
                        ? $"Insert after line {b.StartLine}"
                        : $"[{b.StartLine}-{b.EndLine}]";
                    throw new ArgumentException(
                        $"Patches overlap: {a.Action} {labelA} conflicts with {b.Action} {labelB}.");
                }
            }

            // ── Apply: non-append bottom-up, appends last ─────────────────
            var patchResults = new List<FileOperationResult>();

            var ordered = patches
                .Where(p => p.Action != PatchAction.Append)
                .OrderByDescending(p => p.StartLine)
                .ToList();

            foreach (var p in ordered)
            {
                var newLines = SplitLines(p.Content ?? "");
                int affected;

                switch (p.Action)
                {
                    case PatchAction.Patch:
                        var removeCount = p.EndLine - p.StartLine + 1;
                        lines.RemoveRange(p.StartLine - 1, removeCount);
                        lines.InsertRange(p.StartLine - 1, newLines);
                        affected = removeCount;
                        break;

                    case PatchAction.Insert:
                        // startLine=0 → insert at index 0 (prepend)
                        lines.InsertRange(p.StartLine, newLines);
                        affected = newLines.Count;
                        break;

                    case PatchAction.Delete:
                        affected = p.EndLine - p.StartLine + 1;
                        lines.RemoveRange(p.StartLine - 1, affected);
                        break;

                    default:
                        affected = 0;
                        break;
                }

                patchResults.Add(new FileOperationResult
                {
                    RelativeFilePath = relativeFilePath,
                    Success = true,
                    LinesAffected = affected
                });
            }

            foreach (var p in patches.Where(p => p.Action == PatchAction.Append))
            {
                var newLines = SplitLines(p.Content ?? "");
                lines.AddRange(newLines);
                patchResults.Add(new FileOperationResult
                {
                    RelativeFilePath = relativeFilePath,
                    Success = true,
                    LinesAffected = newLines.Count
                });
            }

            // ── Single write ──────────────────────────────────────────────
            await File.WriteAllLinesAsync(absolute, lines, Encoding.UTF8);
            await InvalidateCacheAsync(projectName, relativeFilePath);

            _logger.LogInformation("EditLines: {Project}/{Path} — {Count} patch(es), {Lines} lines total",
                projectName, relativeFilePath, patches.Count, lines.Count);

            // Summarise into one result per call — callers see final state
            return new BatchOperationResult
            {
                Results =
                [
                    new FileOperationResult
                    {
                        RelativeFilePath = relativeFilePath,
                        Success = true,
                        LineCount = lines.Count,
                        LinesAffected = patchResults.Sum(r => r.LinesAffected ?? 0)
                    }
                ]
            };
        }
        finally { sem.Release(); }
    }

    // ── MoveFiles ─────────────────────────────────────────────────────────────

    public async Task<BatchOperationResult> MoveFilesAsync(string projectName, List<MoveFileEntry> moves)
    {
        var results = new List<FileOperationResult>();

        // ── Validate ALL before executing ANY ────────────────────────────
        var validated = new List<(MoveFileEntry entry, string srcAbs, string dstAbs)>();

        foreach (var move in moves)
        {
            try
            {
                var src = ResolveAndGuard(projectName, move.From);
                var dst = ResolveAndGuard(projectName, move.To);

                if (!File.Exists(src))
                { results.Add(Fail(move.From, "Source file not found.")); continue; }

                if (File.Exists(dst))
                { results.Add(Fail(move.From, $"Destination already exists: {move.To}")); continue; }

                validated.Add((move, src, dst));
            }
            catch (Exception ex)
            {
                results.Add(Fail(move.From, ex.Message));
            }
        }

        // If any validation errors, bail — return errors, execute nothing
        if (results.Count > 0)
            return new BatchOperationResult { Results = results };

        // ── Execute ───────────────────────────────────────────────────────
        foreach (var (move, src, dst) in validated)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(src, dst);
                await InvalidateCacheAsync(projectName, move.From);
                // Watcher picks up new file at dst automatically

                _logger.LogInformation("MoveFile: {Project} {From} → {To}", projectName, move.From, move.To);
                results.Add(new FileOperationResult { RelativeFilePath = move.From, Success = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MoveFile failed: {Project}/{From}", projectName, move.From);
                results.Add(Fail(move.From, ex.Message));
            }
        }

        return new BatchOperationResult { Results = results };
    }

    // ── DeleteFiles ───────────────────────────────────────────────────────────

    public async Task<BatchOperationResult> DeleteFilesAsync(string projectName, List<string> relativeFilePaths)
    {
        var results = new List<FileOperationResult>();

        foreach (var rel in relativeFilePaths)
        {
            try
            {
                var absolute = ResolveAndGuard(projectName, rel);

                if (!File.Exists(absolute))
                { results.Add(Fail(rel, "File not found.")); continue; }

                File.Delete(absolute);
                await InvalidateCacheAsync(projectName, rel);

                _logger.LogInformation("DeleteFile: {Project}/{Path}", projectName, rel);
                results.Add(new FileOperationResult { RelativeFilePath = rel, Success = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeleteFile failed: {Project}/{Path}", projectName, rel);
                results.Add(Fail(rel, ex.Message));
            }
        }

        return new BatchOperationResult { Results = results };
    }

    // ── CreateFolders ─────────────────────────────────────────────────────────

    public async Task<BatchOperationResult> CreateFoldersAsync(string projectName, List<string> relativeFolderPaths)
    {
        var results = new List<FileOperationResult>();

        foreach (var rel in relativeFolderPaths)
        {
            try
            {
                var absolute = ResolveAndGuard(projectName, rel);
                Directory.CreateDirectory(absolute); // idempotent, handles nested

                _logger.LogInformation("CreateFolder: {Project}/{Path}", projectName, rel);
                results.Add(new FileOperationResult { RelativeFilePath = rel, Success = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CreateFolder failed: {Project}/{Path}", projectName, rel);
                results.Add(Fail(rel, ex.Message));
            }
        }

        return await Task.FromResult(new BatchOperationResult { Results = results });
    }

    // ── MoveFolder (single) ───────────────────────────────────────────────────

    public async Task<FileOperationResult> MoveFolderAsync(string projectName, string from, string to)
    {
        var srcAbs = ResolveAndGuard(projectName, from);
        var dstAbs = ResolveAndGuard(projectName, to);

        if (!Directory.Exists(srcAbs))
            throw new ArgumentException($"Source folder not found: {from}");

        if (Directory.Exists(dstAbs))
            throw new InvalidOperationException($"Destination folder already exists: {to}");

        // Evict all cache entries under the old path before moving
        var index = await _cache.GetIndexAsync(projectName);
        var normalizedFrom = NormalizePath(from);

        var stale = index
            .Where(p => NormalizePath(p).StartsWith(normalizedFrom, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var p in stale)
            await _cache.DeleteAsync(projectName, p);

        Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);
        Directory.Move(srcAbs, dstAbs);
        // Watcher re-indexes all files under dst automatically

        _logger.LogInformation("MoveFolder: {Project} {From} → {To}", projectName, from, to);
        return new FileOperationResult { RelativeFilePath = from, Success = true };
    }

    // ── DeleteFolders ─────────────────────────────────────────────────────────

    public async Task<BatchOperationResult> DeleteFoldersAsync(string projectName, List<string> relativeFolderPaths)
    {
        var results = new List<FileOperationResult>();

        // Deepest first — avoids operating on already-deleted children
        var sorted = relativeFolderPaths
            .OrderByDescending(p => p.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).Length)
            .ToList();

        foreach (var rel in sorted)
        {
            try
            {
                var absolute = ResolveAndGuard(projectName, rel);

                // Extra guard: block top-level blocked dirs explicitly
                var rootSegment = rel.Split(PathSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (FileAccessPolicy.BlockedDirectories.Contains(rootSegment))
                {
                    results.Add(Fail(rel, $"Deleting '{rootSegment}' is blocked."));
                    continue;
                }

                if (!Directory.Exists(absolute))
                {
                    // Already gone (e.g. parent was deleted earlier in this batch) — not a failure
                    results.Add(new FileOperationResult { RelativeFilePath = rel, Success = true });
                    continue;
                }

                // Evict cache for all files under this folder
                var index = await _cache.GetIndexAsync(projectName);
                var normalized = NormalizePath(rel);
                foreach (var p in index.Where(p => NormalizePath(p).StartsWith(normalized, StringComparison.OrdinalIgnoreCase)))
                    await _cache.DeleteAsync(projectName, p);

                Directory.Delete(absolute, recursive: true);

                _logger.LogInformation("DeleteFolder: {Project}/{Path}", projectName, rel);
                results.Add(new FileOperationResult { RelativeFilePath = rel, Success = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DeleteFolder failed: {Project}/{Path}", projectName, rel);
                results.Add(Fail(rel, ex.Message));
            }
        }

        return new BatchOperationResult { Results = results };
    }

    // ── GetFileInfo ───────────────────────────────────────────────────────────

    public async Task<BatchOperationResult> GetFileInfoAsync(string projectName, List<string> relativeFilePaths)
    {
        var results = new List<FileOperationResult>();

        foreach (var rel in relativeFilePaths)
        {
            try
            {
                var absolute = ResolveAndGuard(projectName, rel);

                if (!File.Exists(absolute))
                {
                    results.Add(new FileOperationResult
                    {
                        RelativeFilePath = rel,
                        Success = true,
                        Exists = false
                    });
                    continue;
                }

                var info = new FileInfo(absolute);
                // Stream line count — never loads full content into memory
                var lineCount = File.ReadLines(absolute).Count();

                results.Add(new FileOperationResult
                {
                    RelativeFilePath = rel,
                    Success = true,
                    Exists = true,
                    LineCount = lineCount,
                    SizeBytes = info.Length,
                    LastModifiedUtc = info.LastWriteTimeUtc.ToString("O"),
                    Extension = info.Extension
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "GetFileInfo failed: {Project}/{Path}", projectName, rel);
                results.Add(Fail(rel, ex.Message));
            }
        }

        return await Task.FromResult(new BatchOperationResult
        {
            Results = results,
            CacheStatus = "n/a"
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task InvalidateCacheAsync(string projectName, string relativeFilePath)
    {
        try { await _cache.DeleteAsync(projectName, relativeFilePath); }
        catch (Exception ex)
        {
            // Non-fatal — watcher will correct it within 300ms
            _logger.LogWarning(ex, "Cache invalidation failed for {Project}/{Path}", projectName, relativeFilePath);
        }
    }

    private SemaphoreSlim GetLock(string absolutePath)
        => _fileLocks.GetOrAdd(absolutePath, _ => new SemaphoreSlim(1, 1));

    private static FileOperationResult Fail(string path, string error) => new()
    {
        RelativeFilePath = path,
        Success = false,
        Error = error
    };

    private static List<string> SplitLines(string content)
        => content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    private static int CountLines(string content)
        => content.Split('\n').Length;

    private static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimEnd('/') + "/";

    /// <summary>Evaluates '*'-only wildcard patterns against a filename.</summary>
    private static bool WildcardMatch(string filename, string pattern)
    {
        var parts = pattern.Split('*', StringSplitOptions.None);
        if (parts.Length == 1)
            return filename.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        var s = filename.AsSpan();
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            var idx = s.IndexOf(part, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            s = s[(idx + part.Length)..];
        }
        return true;
    }
}
