using MCP.Core.Configuration;
using MCP.Core.Models;
using MCP.Core.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace RisingTideAI.Trade.MCP.Host.MCPServers;

/// <summary>
/// C# file analysis and method extraction toolkit - PREFERRED over raw file reading.
/// 
/// WHY THESE TOOLS EXIST:
/// - Extract structured metadata WITHOUT loading entire file (token-efficient)
/// - Get method implementations with line numbers (precise code changes)
/// - Understand class structure, dependencies, attributes, properties
/// - Batch operations save ~300-500 tokens per additional file/method
/// 
/// TOKEN OPTIMIZATION CRITICAL:
/// File Size Decision Tree:
///   ≤ 15 KB (≈500 lines) → read_file_content is FASTER and SIMPLER
///   > 15 KB              → analyze_c_sharp_file + fetch_method_implementation (TOKEN-EFFICIENT)
/// 
/// BATCH MODE (HIGH PRIORITY):
/// - analyze_c_sharp_file: 'File1.cs,File2.cs,File3.cs' → Saves ~300 tokens per extra file
/// - fetch_method_implementation: 'Method1,Method2,Method3' → Saves ~500 tokens per extra method
/// - NO SPACES in comma-separated lists
/// - Use batch mode WHENEVER analyzing multiple related files/methods
/// 
/// WHEN TO USE:
/// 1. **analyze_c_sharp_file** - FIRST STEP:
///    - Get class structure, method signatures, properties, fields
///    - Identify constructor dependencies (DI analysis)
///    - Classify file type (controller, service, repository, model)
///    - See ALL members before drilling into specifics
///    - BATCH: Analyze multiple related files (Service + Controller + Repository)
/// 
/// 2. **fetch_method_implementation** - SECOND STEP:
///    - Get complete method body with line numbers
///    - Extract specific logic for modification
///    - Understand implementation details
///    - BATCH: Fetch multiple related methods (CRUD operations)
/// 
/// 3. **read_file_content** - LAST RESORT:
///    - ONLY for small files (≤15 KB)
///    - ONLY for non-C# files (Dockerfile, launchSettings.json)
///    - BLOCKED: appsettings.json, secrets, .env, credentials
/// 
/// CRITICAL WORKFLOW:
/// ❌ WRONG: read_file_content for 2000-line service → Wastes 10,000 tokens
/// ✅ RIGHT: analyze_c_sharp_file → See methods → fetch_method_implementation for 2 methods → Saves 8,000 tokens
/// 
/// WHEN NOT TO USE:
/// - Files without classes/methods (configs, JSON, XML) → Use read_file_content
/// - Understanding WHO calls a method → Use MethodCallGraphTools
/// - Global search across projects → Use CodeSearchTools
/// </summary>
/// 
[McpServerToolType]
public class CodeAnalysisTools
{
    private readonly IProjectSkeletonService _projectSkeletonService;
    private readonly IMarkdownFormatterService _markdownFormatter;
    private readonly IMethodFormatterService _methodFormatter;
    private readonly ITomlSerializerService _tomlSerializer;

    public CodeAnalysisTools(
        IProjectSkeletonService projectSkeletonService,
        IMarkdownFormatterService markdownFormatter,
        IMethodFormatterService methodFormatter,
        ITomlSerializerService tomlSerializer)
    {
        _projectSkeletonService = projectSkeletonService;
        _markdownFormatter = markdownFormatter;
        _methodFormatter = methodFormatter;
        _tomlSerializer = tomlSerializer;
    }

    [McpServerTool]
    [Description("PREFERRED METHOD: Analyzes C# file(s) and returns structured metadata WITHOUT loading full content. " +
    "Returns: namespace, using directives, classes, methods, properties, fields, attributes, constructor dependencies, file classification. " +
    "BATCH MODE: Pass 'File1.cs,File2.cs,File3.cs' (comma-separated, NO SPACES) to analyze multiple files efficiently (saves ~300 tokens per additional file). " +
    "Use this BEFORE fetch_method_implementation to understand structure. " +
    "TOKEN-EFFICIENT: Always prioritise this if its a class based c# file, else use read File Content,Set includePrivateMembers=true when:" +
    " - Debugging internal implementation details\r\n  - Analyzing dependency injection patterns\r\n  - Refactoring private methods\r\n  - Understanding full class architecture\r\nDefault: false (public API surface only)")]
    public async Task<string> AnalyzeCSharpFile(
    [Description("Required: Project name")]
    string projectName,
    [Description("Required: Relative path(s) to C# file from project root. " +
        "Single: 'Services/UserService.cs' | " +
        "Batch: 'Services/UserService.cs,Controllers/UserController.cs,Repositories/UserRepository.cs' (NO SPACES)")]
    string relativeFilePath,
    [Description("Optional: Include private members (default: false, public only)")]
    bool includePrivateMembers = false)
    {
        try
        {
            var filePaths = relativeFilePath
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToArray();

            // Single file — no overhead of Task.WhenAll
            if (filePaths.Length == 1)
            {
                var analysis = await _projectSkeletonService.AnalyzeCSharpFileAsync(
                    projectName,
                    filePaths[0],
                    includePrivateMembers);
                return _markdownFormatter.FormatCSharpAnalysis(analysis);
            }

            // Batch mode — all cache/Redis reads fire concurrently
            var fetchTasks = filePaths.Select(async path =>
            {
                try
                {
                    var analysis = await _projectSkeletonService.AnalyzeCSharpFileAsync(
                        projectName,
                        path,
                        includePrivateMembers);
                    return (Analysis: analysis, Error: (string?)null);
                }
                catch (FileNotFoundException)
                {
                    return (Analysis: (CSharpFileAnalysis?)null, Error: $"File not found: {path}");
                }
            });

            var results = await Task.WhenAll(fetchTasks);

            var analyses = results
                .Where(r => r.Analysis != null)
                .Select(r => r.Analysis!)
                .ToList();

            var errors = results
                .Where(r => r.Error != null)
                .Select(r => r.Error!)
                .ToList();

            if (analyses.Count == 0)
                throw new ArgumentException($"No valid files found.\n\nErrors:\n{string.Join("\n", errors)}");

            // Format batch results
            var sb = new StringBuilder();
            sb.AppendLine($"# Batch C# File Analysis: {analyses.Count} file(s)");
            sb.AppendLine();
            sb.AppendLine($"**Project:** {analyses[0].ProjectName}");
            sb.AppendLine();

            if (errors.Count > 0)
            {
                sb.AppendLine("⚠️ **Warnings:**");
                foreach (var error in errors)
                    sb.AppendLine($"- {error}");
                sb.AppendLine();
            }

            sb.AppendLine("## Files Index");
            sb.AppendLine();
            for (int i = 0; i < analyses.Count; i++)
            {
                var a = analyses[i];
                var classCount = a.Classes.Count;
                var methodCount = a.Classes.Sum(c => c.Methods.Count);
                sb.AppendLine($"{i + 1}. **{a.FileName}** → {classCount} class{(classCount != 1 ? "es" : "")}, {methodCount} method{(methodCount != 1 ? "s" : "")}");
            }
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            for (int i = 0; i < analyses.Count; i++)
            {
                sb.AppendLine($"## File {i + 1}: `{analyses[i].FileName}`");
                sb.AppendLine();

                var formatted = _markdownFormatter.FormatCSharpAnalysis(analyses[i]);

                var lines = formatted.Split('\n');
                var contentStart = Array.FindIndex(lines, l => l.StartsWith("**Project:**"));
                sb.AppendLine(contentStart > 0
                    ? string.Join("\n", lines.Skip(contentStart))
                    : formatted);

                if (i < analyses.Count - 1)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }
        catch (KeyNotFoundException)
        {
            var availableProjects = _projectSkeletonService.GetAvailableProjectsWithInfo();
            var projectList = string.Join("\n", availableProjects.Select(p =>
                $"• {p.Key} - {p.Value.Description}"));
            throw new ArgumentException(
                $"Project '{projectName}' not found.\n\nAvailable projects:\n{projectList}");
        }
        catch (FileNotFoundException ex)
        {
            throw new ArgumentException($"File not found: {ex.Message}");
        }
    }

    [McpServerTool]
    [Description("PREFERRED METHOD: Fetches complete method implementation(s) with line numbers. " +
        "Returns: signature, full body with line numbers, attributes, XML documentation. " +
        "BATCH MODE: Pass 'Method1,Method2,Method3' (comma-separated, NO SPACES) to fetch multiple methods efficiently (saves ~500 tokens per additional method). " +
        "Use AFTER analyze_c_sharp_file to drill into specific methods. " +
        "CRITICAL: Returns line numbers for precise code change suggestions (e.g., 'replace lines 45-60 with...')")]
    public async Task<string> FetchMethodImplementation(
        [Description("Required: Project name")]
        string projectName,
        [Description("Required: Relative path to C# file from project root (e.g., 'Services/UserService.cs')")]
        string relativeFilePath,
        [Description("Required: Method name(s) to fetch. " +
            "Single: 'GetUsers' | " +
            "Batch: 'GetUsers,UpdateUser,DeleteUser' (NO SPACES)")]
        string methodName,
        [Description("Optional: Class name if file has multiple classes with same method name")]
        string? className = null)
    {
        try
        {
            // Check if batch mode (comma-separated method names)
            var methodNames = methodName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (methodNames.Length == 1)
            {
                // SINGLE MODE
                var implementation = await _projectSkeletonService.FetchMethodImplementationAsync(
                    projectName, relativeFilePath, methodNames[0], className);

                return _methodFormatter.FormatMethodImplementation(implementation);
            }
            else
            {
                // BATCH MODE
                var implementations = await _projectSkeletonService.FetchMethodImplementationsBatchAsync(
                    projectName, relativeFilePath, methodNames, className);

                return _methodFormatter.FormatMethodImplementationsBatch(implementations);
            }
        }
        catch (KeyNotFoundException)
        {
            var availableProjects = _projectSkeletonService.GetAvailableProjectsWithInfo();
            var projectList = string.Join("\n", availableProjects.Select(p =>
                $"• {p.Key} - {p.Value.Description}"));

            throw new ArgumentException(
                $"Project '{projectName}' not found.\n\n" +
                $"Available projects:\n{projectList}");
        }
        catch (FileNotFoundException ex)
        {
            throw new ArgumentException($"File not found: {ex.Message}");
        }
        catch (ArgumentException)
        {
            throw;
        }
    }

    [McpServerTool]
    [Description("LAST RESORT: Reads raw file content for source code and safe configuration files. " +
        "SECURITY: Blocks sensitive files (appsettings.json, secrets, credentials, .env). " +
        "DECISION RULE: Use ONLY for files ≤15 KB or non-C# files (Dockerfile, launchSettings.json). " +
        "For C# files >15 KB, use analyze_c_sharp_file + fetch_method_implementation instead (massive token savings). " +
        "Use for: Program.cs, small controllers, Dockerfile, non-sensitive configs.")]
    public async Task<string> ReadFileContent(
        [Description("Required: Project name")]
        string projectName,
        [Description("Required: Relative file path from project root (e.g., 'Program.cs', 'Dockerfile'). " +
            "⚠️ BLOCKED: appsettings.json, secrets.json, .env, credentials, bin/, obj/, node_modules/")]
        string relativeFilePath)
    {
        try
        {
            var result = await _projectSkeletonService.ReadFileContentAsync(projectName, relativeFilePath);
            return result.RawContent; // ✅ Just return the content directly
        }
        catch (FileAccessDeniedException ex)
        {
            // Return structured error response in TOML format
            var errorResult = new
            {
                Success = false,
                ErrorType = "FileAccessDenied",
                ProjectName = projectName,
                FilePath = relativeFilePath,
                Reason = ex.Reason,
                Message = ex.Message,
                Suggestions = new[]
                {
                    "This file contains sensitive data and cannot be accessed for security reasons.",
                    "If you need configuration structure (not values), use 'get_project_skeleton' instead.",
                    "For code analysis, use 'analyze_c_sharp_file' for semantic understanding.",
                    "Allowed file types: .cs, .csproj, .md, .txt, Program.cs, Dockerfile, etc.",
                    "Blocked files: appsettings.json, secrets.json, .env, credential files, database files"
                }
            };

            return _tomlSerializer.Serialize(errorResult);
        }
        catch (FileNotFoundException ex)
        {
            var errorResult = new
            {
                Success = false,
                ErrorType = "FileNotFound",
                ProjectName = projectName,
                FilePath = relativeFilePath,
                Message = ex.Message,
                Suggestions = new[]
                {
                    "Use 'get_project_skeleton' to see all available files in the project.",
                    "Verify the file path is correct and uses forward slashes (/) or backslashes (\\).",
                    "Check if the file exists in the project directory."
                }
            };

            return _tomlSerializer.Serialize(errorResult);
        }
    }
}