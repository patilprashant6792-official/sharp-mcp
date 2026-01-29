using MCP.Core.Configuration;
using MCP.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.RegularExpressions;
using Tomlyn.Syntax;
using static OllamaSharp.OllamaApiClient;
using SyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace MCP.Core.Services;

public class ProjectMappingsConfiguration
{
    public const string SectionName = "ProjectMappings";

    public Dictionary<string, ProjectInfo> Projects { get; set; } = new();
}
public class ProjectSkeletonService : IProjectSkeletonService
{

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", ".git", "node_modules", "packages", ".idea", ".vscode", "TestResults"
    };

    private readonly Dictionary<string, ProjectInfo> _projectMappings;

    public ProjectSkeletonService(
    IConfiguration configuration,
    IProjectConfigService projectConfigService)
    {
       
            // Fallback to projects.json from UI config
            var projectConfig = projectConfigService.LoadProjects();
            _projectMappings = projectConfig.Projects
                .Where(p => p.Enabled) // Only load enabled projects
                .ToDictionary(
                    p => p.Name,
                    p => new ProjectInfo
                    {
                        Path = p.Path,
                        Description = p.Description
                    }
                );

            if (_projectMappings.Count == 0)
            {
                throw new InvalidOperationException(
                    "No projects configured. Please configure projects via:\n" +
                    "1. Config UI at http://localhost:5000/config.html\n" +
                    "2. Or add 'ProjectMappings' section in appsettings.json");
            }
        
    }

    public ProjectSkeletonService(Dictionary<string, ProjectInfo> projectMappings)
    {
        _projectMappings = projectMappings ?? throw new ArgumentNullException(nameof(projectMappings));
    }

    public string GetToolDescription()
    {
        var projectList = string.Join("\n", _projectMappings.Select(p =>
            $"• {p.Key} - {p.Value.Description}"));

        return $@"Generates a comprehensive markdown-formatted skeleton of a .NET project including complete folder structure (ASCII tree), all .sln/.slnx solution files with full content, and all .csproj project files with full content. Automatically excludes build artifacts (bin/, obj/, .vs/, etc.).

Available Projects:
{projectList}

Use this tool to understand project architecture, analyze dependencies, review project structure, or provide complete project context to AI assistants. The output includes the entire project hierarchy and all critical configuration files in a single markdown document.";
    }



    public async Task<FileContentResponse> ReadFileContentAsync(
     string projectName,
     string relativeFilePath,
     CancellationToken cancellationToken = default)
    {
        // Input validation
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name cannot be null or empty.", nameof(projectName));

        if (string.IsNullOrWhiteSpace(relativeFilePath))
            throw new ArgumentException("File path cannot be null or empty.", nameof(relativeFilePath));

        // Normalize path separators
        relativeFilePath = relativeFilePath.Replace('/', Path.DirectorySeparatorChar)
                                           .Replace('\\', Path.DirectorySeparatorChar);

        // 🔒 SECURITY CHECK - VALIDATE FILE ACCESS
        ValidateFileAccess(relativeFilePath);

        // Get project info
        if (!_projectMappings.TryGetValue(projectName, out var projectInfo))
            throw new KeyNotFoundException(
                $"Project '{projectName}' not found. Available projects: {string.Join(", ", _projectMappings.Keys)}");

        // Build full file path
        var fullPath = Path.Combine(projectInfo.Path, relativeFilePath);

        // Validate file exists
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                $"File '{relativeFilePath}' not found in project '{projectName}'");

        // Additional security: Ensure resolved path is still within project directory (prevent path traversal)
        var normalizedProjectPath = Path.GetFullPath(projectInfo.Path);
        var normalizedFilePath = Path.GetFullPath(fullPath);

        if (!normalizedFilePath.StartsWith(normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileAccessDeniedException(
                relativeFilePath,
                "Path traversal detected. File must be within the project directory.");
        }

        try
        {
            // Read file content
            var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var fileInfo = new FileInfo(fullPath);
            var lines = content.Split('\n');

            // Determine file type
            var extension = Path.GetExtension(relativeFilePath).ToLowerInvariant();
            var fileType = DetermineFileType(extension);

            // Analyze content for metadata
            var metadata = AnalyzeFileContent(content, extension);

            return new FileContentResponse
            {
                ProjectName = projectName,
                FilePath = relativeFilePath,
                FileName = Path.GetFileName(relativeFilePath),
                FileType = fileType,
                LineCount = lines.Length,
                FileSizeBytes = fileInfo.Length,
                Encoding = "UTF-8",
                RawContent = content,
                Metadata = metadata
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new FileAccessDeniedException(
                relativeFilePath,
                $"Operating system denied access: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates whether a file can be accessed based on security policies
    /// </summary>
    /// <exception cref="FileAccessDeniedException">Thrown when file access is denied</exception>
    private static void ValidateFileAccess(string relativeFilePath)
    {
        var fileName = Path.GetFileName(relativeFilePath);
        var extension = Path.GetExtension(relativeFilePath).ToLowerInvariant();
        var pathParts = relativeFilePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Check if file is in blocked directory
        foreach (var part in pathParts)
        {
            if (FileAccessPolicy.BlockedDirectories.Contains(part))
            {
                throw new FileAccessDeniedException(
                    relativeFilePath,
                    $"File is located in restricted directory: '{part}'. This directory may contain sensitive data or build artifacts.");
            }
        }

        // Check exact filename match (case-insensitive)
        if (FileAccessPolicy.BlockedFiles.Contains(fileName))
        {
            throw new FileAccessDeniedException(
                relativeFilePath,
                "This file contains sensitive configuration data (credentials, connection strings, API keys). Access is restricted for security reasons.");
        }

        // Check blocked patterns (wildcards)
        foreach (var pattern in FileAccessPolicy.BlockedPatterns)
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            if (Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase))
            {
                throw new FileAccessDeniedException(
                    relativeFilePath,
                    $"File name matches restricted pattern '{pattern}'. This may indicate sensitive content.");
            }
        }

        // Extension whitelist check
        if (!string.IsNullOrEmpty(extension) &&
            !FileAccessPolicy.AllowedExtensions.Contains(extension) &&
            extension != string.Empty) // Allow files without extensions (like Dockerfile)
        {
            throw new FileAccessDeniedException(
                relativeFilePath,
                $"File extension '{extension}' is not in the allowed list. Only safe file types can be accessed.");
        }

        // Additional paranoid checks for common sensitive patterns in path
        var lowerPath = relativeFilePath.ToLowerInvariant();
        if (lowerPath.Contains("secret") ||
            lowerPath.Contains("password") ||
            lowerPath.Contains("credential") ||
            lowerPath.Contains("apikey") ||
            lowerPath.Contains("token") ||
            lowerPath.Contains("private"))
        {
            throw new FileAccessDeniedException(
                relativeFilePath,
                "File path contains keywords associated with sensitive data (secret, password, credential, etc.).");
        }
    }

    /// <summary>
    /// Determines file type based on extension
    /// </summary>
    private static string DetermineFileType(string extension)
    {
        return extension switch
        {
            ".cs" => "C# Source File",
            ".csproj" => "C# Project File",
            ".sln" => "Visual Studio Solution",
            ".slnx" => "Visual Studio Solution (XML)",
            ".json" => "JSON Configuration",
            ".xml" => "XML Document",
            ".toml" => "TOML Configuration",
            ".md" => "Markdown Document",
            ".txt" => "Text File",
            ".ps1" => "PowerShell Script",
            ".yml" or ".yaml" => "YAML Configuration",
            ".dockerfile" => "Docker Configuration",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Analyzes file content to extract metadata patterns
    /// </summary>
    private static FileMetadata AnalyzeFileContent(string content, string extension)
    {
        var metadata = new FileMetadata();

        if (extension == ".cs")
        {
            // Detect C# patterns
            metadata.HasClasses = content.Contains("class ") || content.Contains("interface ") || content.Contains("record ");
            metadata.HasTopLevelStatements = !metadata.HasClasses && (
                content.Contains("var ") ||
                content.Contains("await ") ||
                content.Contains("using ") ||
                content.Contains("builder."));

            // Detect DI registration patterns
            metadata.ContainsDIRegistration =
                content.Contains("builder.Services.Add") ||
                content.Contains("services.Add") ||
                content.Contains("AddSingleton") ||
                content.Contains("AddScoped") ||
                content.Contains("AddTransient");

            // Detect MCP configuration
            metadata.ContainsMCPServerConfiguration =
                content.Contains("AddMcpServer") ||
                content.Contains("WithTools") ||
                content.Contains("MapMcpServer") ||
                content.Contains("McpServerTool");

            // Add detected patterns
            if (metadata.HasTopLevelStatements)
                metadata.DetectedPatterns.Add("Top-level statements (modern .NET)");
            if (metadata.ContainsDIRegistration)
                metadata.DetectedPatterns.Add("Dependency injection registration");
            if (metadata.ContainsMCPServerConfiguration)
                metadata.DetectedPatterns.Add("MCP server configuration");
            if (content.Contains("app.Use"))
                metadata.DetectedPatterns.Add("Middleware pipeline");
            if (content.Contains("[Route"))
                metadata.DetectedPatterns.Add("API routes/controllers");
        }
        else if (extension == ".json")
        {
            metadata.IsConfigurationFile = true;
            if (content.Contains("\"ConnectionStrings\""))
                metadata.DetectedPatterns.Add("Database connection strings");
            if (content.Contains("\"Logging\""))
                metadata.DetectedPatterns.Add("Logging configuration");
        }

        return metadata;
    }

    public async Task<MethodImplementationInfo> FetchMethodImplementationAsync(
        string projectName,
        string relativeFilePath,
        string methodName,
        string? className = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name cannot be null or empty.", nameof(projectName));

        if (string.IsNullOrWhiteSpace(relativeFilePath))
            throw new ArgumentException("Relative file path cannot be null or empty.", nameof(relativeFilePath));

        if (string.IsNullOrWhiteSpace(methodName))
            throw new ArgumentException("Method name cannot be null or empty.", nameof(methodName));

        if (!_projectMappings.TryGetValue(projectName, out var projectInfo))
            throw new KeyNotFoundException($"Project '{projectName}' not found. Available projects: {string.Join(", ", _projectMappings.Keys)}");

        var fullPath = Path.Combine(projectInfo.Path, relativeFilePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File '{relativeFilePath}' not found in project '{projectName}'.", fullPath);

        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only C# files (.cs) can be analyzed.", nameof(relativeFilePath));

        var sourceCode = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);

        var methods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.Text == methodName)
            .ToList();

        if (!methods.Any())
            throw new ArgumentException($"Method '{methodName}' not found in file '{relativeFilePath}'");

        MethodDeclarationSyntax? targetMethod = null;

        if (!string.IsNullOrWhiteSpace(className))
        {
            targetMethod = methods.FirstOrDefault(m =>
            {
                var classDecl = m.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                return classDecl?.Identifier.Text == className;
            });

            if (targetMethod == null)
                throw new ArgumentException($"Method '{methodName}' not found in class '{className}'");
        }
        else
        {
            if (methods.Count > 1)
            {
                var classNames = methods
                    .Select(m => m.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Unknown")
                    .Distinct()
                    .ToList();

                throw new ArgumentException(
                    $"Multiple methods named '{methodName}' found in classes: {string.Join(", ", classNames)}. " +
                    $"Please specify the className parameter.");
            }

            targetMethod = methods.First();
        }

        var containingClass = targetMethod.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        var containingNamespace = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault()
                                ?? root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault() as BaseNamespaceDeclarationSyntax;

        var xmlDocumentation = targetMethod.GetLeadingTrivia()
            .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                        t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
            .Select(t => t.ToString())
            .FirstOrDefault();

        return new MethodImplementationInfo
        {
            ProjectName = projectName,
            FilePath = relativeFilePath,
            ClassName = containingClass?.Identifier.Text ?? string.Empty,
            Namespace = containingNamespace?.Name.ToString() ?? string.Empty,
            MethodName = methodName,
            FullSignature = GetMethodSignature(targetMethod),
            ReturnType = targetMethod.ReturnType.ToString(),
            Modifiers = targetMethod.Modifiers.Select(m => m.Text).ToList(),
            Parameters = targetMethod.ParameterList.Parameters.Select(p => new ParameterInfo
            {
                Type = p.Type?.ToString() ?? "unknown",
                Name = p.Identifier.Text,
                DefaultValue = p.Default?.Value.ToString()
            }).ToList(),
            Attributes = targetMethod.AttributeLists
                .SelectMany(al => al.Attributes)
                .Select(a => new AttributeInfo
                {
                    Name = a.Name.ToString(),
                    Properties = a.ArgumentList?.Arguments
                        .Select((arg, idx) => new { Key = arg.NameEquals?.Name.ToString() ?? $"arg{idx}", Value = arg.Expression.ToString() })
                        .ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, string>()
                }).ToList(),
            XmlDocumentation = xmlDocumentation,
            MethodBody = targetMethod.Body?.ToString() ?? string.Empty,
            FullMethodCode = targetMethod.ToString(),
            LineNumber = targetMethod.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            IsAsync = targetMethod.Modifiers.Any(m => m.Text == "async"),
            IsStatic = targetMethod.Modifiers.Any(m => m.Text == "static"),
            IsVirtual = targetMethod.Modifiers.Any(m => m.Text == "virtual"),
            IsOverride = targetMethod.Modifiers.Any(m => m.Text == "override"),
            IsAbstract = targetMethod.Modifiers.Any(m => m.Text == "abstract")
        };
    }

    /// <summary>
    /// Fetches multiple method implementations in a single operation.
    /// More efficient than calling FetchMethodImplementationAsync multiple times.
    /// Saves ~500 tokens per method by parsing the file once and reusing context.
    /// </summary>
    public async Task<List<MethodImplementationInfo>> FetchMethodImplementationsBatchAsync(
        string projectName,
        string relativeFilePath,
        string[] methodNames,
        string? className = null,
        CancellationToken cancellationToken = default)
    {
        if (methodNames == null || methodNames.Length == 0)
            throw new ArgumentException("At least one method name must be provided", nameof(methodNames));

        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name cannot be null or empty.", nameof(projectName));

        if (string.IsNullOrWhiteSpace(relativeFilePath))
            throw new ArgumentException("Relative file path cannot be null or empty.", nameof(relativeFilePath));

        if (!_projectMappings.TryGetValue(projectName, out var projectInfo))
            throw new KeyNotFoundException($"Project '{projectName}' not found. Available projects: {string.Join(", ", _projectMappings.Keys)}");

        var fullPath = Path.Combine(projectInfo.Path, relativeFilePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File '{relativeFilePath}' not found in project '{projectName}'.", fullPath);

        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only C# files (.cs) can be analyzed.", nameof(relativeFilePath));

        // Parse file once for all methods
        var sourceCode = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);

        // Get all methods in the file
        var allMethods = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .ToList();

        // Filter by className if specified
        if (!string.IsNullOrWhiteSpace(className))
        {
            allMethods = allMethods.Where(m =>
            {
                var classDecl = m.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
                return classDecl?.Identifier.Text == className;
            }).ToList();
        }

        // Get shared namespace (same for all methods)
        var containingNamespace = root.DescendantNodes().OfType<NamespaceDeclarationSyntax>().FirstOrDefault()
                                ?? root.DescendantNodes().OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault() as BaseNamespaceDeclarationSyntax;

        var results = new List<MethodImplementationInfo>();
        var notFound = new List<string>();

        // Process each requested method
        foreach (var methodName in methodNames)
        {
            var targetMethod = allMethods.FirstOrDefault(m => m.Identifier.Text == methodName);

            if (targetMethod == null)
            {
                notFound.Add(methodName);
                continue;
            }

            // Check for multiple methods with same name
            var duplicates = allMethods.Where(m => m.Identifier.Text == methodName).ToList();
            if (duplicates.Count > 1 && string.IsNullOrWhiteSpace(className))
            {
                var classNames = duplicates
                    .Select(m => m.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Unknown")
                    .Distinct()
                    .ToList();

                throw new ArgumentException(
                    $"Multiple methods named '{methodName}' found in classes: {string.Join(", ", classNames)}. " +
                    $"Please specify the className parameter.");
            }

            var containingClass = targetMethod.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();

            var xmlDocumentation = targetMethod.GetLeadingTrivia()
                .Where(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) ||
                            t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                .Select(t => t.ToString())
                .FirstOrDefault();

            var methodInfo = new MethodImplementationInfo
            {
                ProjectName = projectName,
                FilePath = relativeFilePath,
                ClassName = containingClass?.Identifier.Text ?? string.Empty,
                Namespace = containingNamespace?.Name.ToString() ?? string.Empty,
                MethodName = methodName,
                FullSignature = GetMethodSignature(targetMethod),
                ReturnType = targetMethod.ReturnType.ToString(),
                Modifiers = targetMethod.Modifiers.Select(m => m.Text).ToList(),
                Parameters = targetMethod.ParameterList.Parameters.Select(p => new ParameterInfo
                {
                    Type = p.Type?.ToString() ?? "unknown",
                    Name = p.Identifier.Text,
                    DefaultValue = p.Default?.Value.ToString()
                }).ToList(),
                Attributes = targetMethod.AttributeLists
                    .SelectMany(al => al.Attributes)
                    .Select(a => new AttributeInfo
                    {
                        Name = a.Name.ToString(),
                        Properties = a.ArgumentList?.Arguments
                            .Select((arg, idx) => new { Key = arg.NameEquals?.Name.ToString() ?? $"arg{idx}", Value = arg.Expression.ToString() })
                            .ToDictionary(x => x.Key, x => x.Value) ?? new Dictionary<string, string>()
                    }).ToList(),
                XmlDocumentation = xmlDocumentation,
                MethodBody = targetMethod.Body?.ToString() ?? string.Empty,
                FullMethodCode = targetMethod.ToString(),
                LineNumber = targetMethod.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                IsAsync = targetMethod.Modifiers.Any(m => m.Text == "async"),
                IsStatic = targetMethod.Modifiers.Any(m => m.Text == "static"),
                IsVirtual = targetMethod.Modifiers.Any(m => m.Text == "virtual"),
                IsOverride = targetMethod.Modifiers.Any(m => m.Text == "override"),
                IsAbstract = targetMethod.Modifiers.Any(m => m.Text == "abstract")
            };

            results.Add(methodInfo);
        }

        // Throw exception if any methods were not found
        if (notFound.Any())
        {
            var availableMethods = allMethods.Select(m => m.Identifier.Text).Distinct().OrderBy(n => n).ToList();
            throw new ArgumentException(
                $"Methods not found: {string.Join(", ", notFound)}.\n" +
                $"Available methods in {(string.IsNullOrWhiteSpace(className) ? "file" : $"class '{className}'")}: " +
                $"{string.Join(", ", availableMethods)}");
        }

        return results;
    }

    private static string GetMethodSignature(MethodDeclarationSyntax method)
    {
        var modifiers = string.Join(" ", method.Modifiers.Select(m => m.Text));
        var returnType = method.ReturnType.ToString();
        var methodName = method.Identifier.Text;
        var parameters = string.Join(", ", method.ParameterList.Parameters.Select(p =>
        {
            var type = p.Type?.ToString() ?? "unknown";
            var name = p.Identifier.Text;
            var defaultVal = p.Default != null ? $" = {p.Default.Value}" : string.Empty;
            return $"{type} {name}{defaultVal}";
        }));

        return $"{modifiers} {returnType} {methodName}({parameters})".Trim();
    }
    public async Task<CSharpFileAnalysis> AnalyzeCSharpFileAsync(
      string projectName,
      string relativeFilePath,
      bool includePrivateMembers = false,
      CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name cannot be null or empty.", nameof(projectName));

        if (string.IsNullOrWhiteSpace(relativeFilePath))
            throw new ArgumentException("Relative file path cannot be null or empty.", nameof(relativeFilePath));

        if (!_projectMappings.TryGetValue(projectName, out var projectInfo))
            throw new KeyNotFoundException($"Project '{projectName}' not found. Available projects: {string.Join(", ", _projectMappings.Keys)}");

        var fullPath = Path.Combine(projectInfo.Path, relativeFilePath);

        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File '{relativeFilePath}' not found in project '{projectName}'.", fullPath);

        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only C# files (.cs) can be analyzed.", nameof(relativeFilePath));

        var fileInfo = new FileInfo(fullPath);
        var sourceCode = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);

        var result = new CSharpFileAnalysis
        {
            ProjectName = projectName,
            FilePath = relativeFilePath,
            FileName = Path.GetFileName(relativeFilePath),
            SizeInBytes = fileInfo.Length
        };

        // Extract namespace
        var namespaceDecl = root.DescendantNodes()
            .OfType<BaseNamespaceDeclarationSyntax>()
            .FirstOrDefault();
        result.Namespace = namespaceDecl?.Name.ToString() ?? string.Empty;

        // Extract using directives
        result.UsingDirectives = root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(u => u.Name?.ToString() ?? u.NamespaceOrType?.ToString() ?? string.Empty)
            .Where(u => !string.IsNullOrEmpty(u))
            .Distinct()
            .ToList();

        // Extract classes
        // ════════════════════════════════════════════════════════════════════════════════════════
        // 🔧 INTERFACE EXTRACTION PATCH for ProjectSkeletonService.cs
        // ════════════════════════════════════════════════════════════════════════════════════════
        // 
        // PROBLEM: Line 626 in AnalyzeCSharpFileAsync() only extracts ClassDeclarationSyntax.
        //          This means interface declarations like INuGetSearchService are NEVER indexed.
        //
        // SOLUTION: Add InterfaceDeclarationSyntax extraction immediately after class extraction.
        //
        // FILE: ProjectExplorationServices/ProjectSkeletonService.cs
        // REPLACE: Lines 625-878 (entire class extraction block)
        // ════════════════════════════════════════════════════════════════════════════════════════

        // ✂️ REMOVE OLD CODE (Lines 625-878)
        // Replace the entire section starting with:
        //     // Extract classes
        //     var classDeclarations = root.DescendantNodes()
        //         .OfType<ClassDeclarationSyntax>();
        //
        // WITH THE NEW CODE BELOW:

        // ════════════════════════════════════════════════════════════════════════════════════════
        // 🆕 EXTRACT BOTH CLASSES AND INTERFACES
        // ════════════════════════════════════════════════════════════════════════════════════════

        // Extract classes
        var classDeclarations = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>();

        foreach (var classDecl in classDeclarations)
        {
            var classInfo = new ClassInfo
            {
                Name = classDecl.Identifier.Text,
                Modifiers = classDecl.Modifiers.Select(m => m.Text).ToList(),
                BaseClass = classDecl.BaseList?.Types
                    .FirstOrDefault(t => !(t.Type is GenericNameSyntax gns && gns.Identifier.Text.StartsWith("I")))
                    ?.Type.ToString(),
                Interfaces = classDecl.BaseList?.Types
                    .Where(t => t.Type is GenericNameSyntax gns && gns.Identifier.Text.StartsWith("I") ||
                                t.Type is IdentifierNameSyntax ins && ins.Identifier.Text.StartsWith("I"))
                    .Select(t => t.Type.ToString())
                    .ToList() ?? new List<string>(),
                LineNumber = tree.GetLineSpan(classDecl.Span).StartLinePosition.Line + 1
            };

            // Extract class-level attributes
            foreach (var attrList in classDecl.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var attrInfo = new AttributeInfo
                    {
                        Name = attr.Name.ToString()
                    };

                    if (attr.ArgumentList != null)
                    {
                        foreach (var arg in attr.ArgumentList.Arguments)
                        {
                            if (arg.NameEquals != null)
                            {
                                var key = arg.NameEquals.Name.ToString();
                                var value = arg.Expression.ToString().Trim('"');
                                attrInfo.Properties[key] = value;
                            }
                            else
                            {
                                attrInfo.Properties[$"arg{attrInfo.Properties.Count}"] =
                                    arg.Expression.ToString().Trim('"');
                            }
                        }
                    }

                    classInfo.Attributes.Add(attrInfo);
                }
            }

            // Extract constructor for DI analysis
            var constructor = classDecl.Members
                .OfType<ConstructorDeclarationSyntax>()
                .Where(c => c.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                .FirstOrDefault();

            if (constructor != null)
            {
                classInfo.ConstructorParameters = constructor.ParameterList.Parameters
                    .Select(p => new ParameterInfo
                    {
                        Type = p.Type?.ToString() ?? "unknown",
                        Name = p.Identifier.Text,
                        DefaultValue = p.Default?.Value.ToString()
                    })
                    .ToList();
            }

            // Extract methods (public only OR all if includePrivateMembers=true)
            foreach (var method in classDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                if (!includePrivateMembers && !method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    continue;

                var methodSpan = tree.GetLineSpan(method.Span);
                var methodInfo = new MethodInfo
                {
                    Name = method.Identifier.Text,
                    ReturnType = method.ReturnType.ToString(),
                    Modifiers = method.Modifiers.Select(m => m.Text).ToList(),
                    Parameters = method.ParameterList.Parameters
                        .Select(p => new ParameterInfo
                        {
                            Type = p.Type?.ToString() ?? "unknown",
                            Name = p.Identifier.Text,
                            DefaultValue = p.Default?.Value.ToString()
                        })
                        .ToList(),
                    LineNumber = methodSpan.StartLinePosition.Line + 1,
                    LineNumberStart = methodSpan.StartLinePosition.Line + 1,
                    LineNumberEnd = methodSpan.EndLinePosition.Line + 1
                };

                // Extract method-level attributes
                foreach (var attrList in method.AttributeLists)
                {
                    foreach (var attr in attrList.Attributes)
                    {
                        var attrInfo = new AttributeInfo
                        {
                            Name = attr.Name.ToString()
                        };

                        if (attr.ArgumentList != null)
                        {
                            foreach (var arg in attr.ArgumentList.Arguments)
                            {
                                if (arg.NameEquals != null)
                                {
                                    var key = arg.NameEquals.Name.ToString();
                                    var value = arg.Expression.ToString().Trim('"');
                                    attrInfo.Properties[key] = value;
                                }
                                else
                                {
                                    attrInfo.Properties[$"arg{attrInfo.Properties.Count}"] =
                                        arg.Expression.ToString().Trim('"');
                                }
                            }
                        }

                        methodInfo.Attributes.Add(attrInfo);
                    }
                }

                // Extract XML documentation
                var trivia = method.GetLeadingTrivia()
                    .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));

                if (trivia != default)
                {
                    methodInfo.XmlDocumentation = trivia.ToString();
                }

                classInfo.Methods.Add(methodInfo);
            }

            // Extract properties (public only OR all if includePrivateMembers=true)
            foreach (var property in classDecl.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!includePrivateMembers && !property.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    continue;

                var propertySpan = tree.GetLineSpan(property.Span);
                var propInfo = new PropertyInfo
                {
                    Name = property.Identifier.Text,
                    Type = property.Type.ToString(),
                    Modifiers = property.Modifiers.Select(m => m.Text).ToList(),
                    HasGetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false,
                    HasSetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false,
                    LineNumber = propertySpan.StartLinePosition.Line + 1,
                    LineNumberStart = propertySpan.StartLinePosition.Line + 1,
                    LineNumberEnd = propertySpan.EndLinePosition.Line + 1
                };

                // Extract property-level attributes
                foreach (var attrList in property.AttributeLists)
                {
                    foreach (var attr in attrList.Attributes)
                    {
                        var attrInfo = new AttributeInfo
                        {
                            Name = attr.Name.ToString()
                        };

                        if (attr.ArgumentList != null)
                        {
                            foreach (var arg in attr.ArgumentList.Arguments)
                            {
                                if (arg.NameEquals != null)
                                {
                                    var key = arg.NameEquals.Name.ToString();
                                    var value = arg.Expression.ToString().Trim('"');
                                    attrInfo.Properties[key] = value;
                                }
                                else
                                {
                                    attrInfo.Properties[$"arg{attrInfo.Properties.Count}"] =
                                        arg.Expression.ToString().Trim('"');
                                }
                            }
                        }

                        propInfo.Attributes.Add(attrInfo);
                    }
                }

                classInfo.Properties.Add(propInfo);
            }

            // Extract fields (public only OR all if includePrivateMembers=true)
            foreach (var field in classDecl.Members.OfType<FieldDeclarationSyntax>())
            {
                if (!includePrivateMembers && !field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
                    continue;

                var fieldSpan = tree.GetLineSpan(field.Span);
                foreach (var variable in field.Declaration.Variables)
                {
                    var fieldInfo = new FieldInfo
                    {
                        Name = variable.Identifier.Text,
                        Type = field.Declaration.Type.ToString(),
                        Modifiers = field.Modifiers.Select(m => m.Text).ToList(),
                        IsReadOnly = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword)),
                        IsStatic = field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)),
                        IsConst = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)),
                        LineNumber = fieldSpan.StartLinePosition.Line + 1,
                        LineNumberStart = fieldSpan.StartLinePosition.Line + 1,
                        LineNumberEnd = fieldSpan.EndLinePosition.Line + 1
                    };

                    // Extract field-level attributes
                    foreach (var attrList in field.AttributeLists)
                    {
                        foreach (var attr in attrList.Attributes)
                        {
                            var attrInfo = new AttributeInfo
                            {
                                Name = attr.Name.ToString()
                            };

                            if (attr.ArgumentList != null)
                            {
                                foreach (var arg in attr.ArgumentList.Arguments)
                                {
                                    if (arg.NameEquals != null)
                                    {
                                        var key = arg.NameEquals.Name.ToString();
                                        var value = arg.Expression.ToString().Trim('"');
                                        attrInfo.Properties[key] = value;
                                    }
                                    else
                                    {
                                        attrInfo.Properties[$"arg{attrInfo.Properties.Count}"] =
                                            arg.Expression.ToString().Trim('"');
                                    }
                                }
                            }

                            fieldInfo.Attributes.Add(attrInfo);
                        }
                    }

                    classInfo.Fields.Add(fieldInfo);
                }
            }

            result.Classes.Add(classInfo);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // 🆕 EXTRACT INTERFACES (NEW CODE - ADD THIS AFTER CLASS EXTRACTION)
        // ════════════════════════════════════════════════════════════════════════════════════════

        var interfaceDeclarations = root.DescendantNodes()
            .OfType<InterfaceDeclarationSyntax>();

        foreach (var interfaceDecl in interfaceDeclarations)
        {
            // Create a ClassInfo for the interface (we're reusing ClassInfo to minimize changes)
            var interfaceInfo = new ClassInfo
            {
                Name = interfaceDecl.Identifier.Text,
                Modifiers = interfaceDecl.Modifiers.Select(m => m.Text).ToList(),
                BaseClass = null, // Interfaces don't have base classes
                Interfaces = interfaceDecl.BaseList?.Types
                    .Select(t => t.Type.ToString())
                    .ToList() ?? new List<string>(), // Inherited interfaces
                LineNumber = tree.GetLineSpan(interfaceDecl.Span).StartLinePosition.Line + 1
            };

            // Extract interface-level attributes
            foreach (var attrList in interfaceDecl.AttributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var attrInfo = new AttributeInfo
                    {
                        Name = attr.Name.ToString()
                    };

                    if (attr.ArgumentList != null)
                    {
                        foreach (var arg in attr.ArgumentList.Arguments)
                        {
                            if (arg.NameEquals != null)
                            {
                                var key = arg.NameEquals.Name.ToString();
                                var value = arg.Expression.ToString().Trim('"');
                                attrInfo.Properties[key] = value;
                            }
                            else
                            {
                                attrInfo.Properties[$"arg{attrInfo.Properties.Count}"] =
                                    arg.Expression.ToString().Trim('"');
                            }
                        }
                    }

                    interfaceInfo.Attributes.Add(attrInfo);
                }
            }

            // Extract interface methods (all are implicitly public)
            foreach (var method in interfaceDecl.Members.OfType<MethodDeclarationSyntax>())
            {
                var methodSpan = tree.GetLineSpan(method.Span);
                var methodInfo = new MethodInfo
                {
                    Name = method.Identifier.Text,
                    ReturnType = method.ReturnType.ToString(),
                    Modifiers = new List<string> { "public" }, // Interfaces are implicitly public
                    Parameters = method.ParameterList.Parameters
                        .Select(p => new ParameterInfo
                        {
                            Type = p.Type?.ToString() ?? "unknown",
                            Name = p.Identifier.Text,
                            DefaultValue = p.Default?.Value.ToString()
                        })
                        .ToList(),
                    LineNumber = methodSpan.StartLinePosition.Line + 1,
                    LineNumberStart = methodSpan.StartLinePosition.Line + 1,
                    LineNumberEnd = methodSpan.EndLinePosition.Line + 1
                };

                // Extract XML documentation
                var trivia = method.GetLeadingTrivia()
                    .FirstOrDefault(t => t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia));

                if (trivia != default)
                {
                    methodInfo.XmlDocumentation = trivia.ToString();
                }

                interfaceInfo.Methods.Add(methodInfo);
            }

            // Extract interface properties (all are implicitly public)
            foreach (var property in interfaceDecl.Members.OfType<PropertyDeclarationSyntax>())
            {
                var propertySpan = tree.GetLineSpan(property.Span);
                var propInfo = new PropertyInfo
                {
                    Name = property.Identifier.Text,
                    Type = property.Type.ToString(),
                    Modifiers = new List<string> { "public" }, // Interfaces are implicitly public
                    HasGetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? false,
                    HasSetter = property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration)) ?? false,
                    LineNumber = propertySpan.StartLinePosition.Line + 1,
                    LineNumberStart = propertySpan.StartLinePosition.Line + 1,
                    LineNumberEnd = propertySpan.EndLinePosition.Line + 1
                };

                interfaceInfo.Properties.Add(propInfo);
            }

            // Add to results - interfaces are stored in the same Classes list
            // (Consider renaming Classes to TypeDeclarations in the future for clarity)
            result.Classes.Add(interfaceInfo);
        }

        // ════════════════════════════════════════════════════════════════════════════════════════
        // Continue with the return statement (no changes needed after this)
        // ════════════════════════════════════════════════════════════════════════════════════════

        return result;
    }

    public IReadOnlyDictionary<string, string> GetAvailableProjects()
    {
        return _projectMappings.ToDictionary(p => p.Key, p => p.Value.Path);
    }

    public IReadOnlyDictionary<string, ProjectInfo> GetAvailableProjectsWithInfo()
    {
        return _projectMappings;
    }

    public async Task<string> GetProjectSkeletonAsync(
        string projectName,
        string? sinceTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name cannot be null or empty.", nameof(projectName));

        if (!_projectMappings.TryGetValue(projectName, out var projectInfo))
            throw new KeyNotFoundException($"Project '{projectName}' not found. Available projects: {string.Join(", ", _projectMappings.Keys)}");

        var projectPath = projectInfo.Path;
        if (!Directory.Exists(projectPath))
            throw new DirectoryNotFoundException($"Project path '{projectPath}' does not exist.");

        DateTime? cutoffDate = null;
        if (!string.IsNullOrWhiteSpace(sinceTimestamp))
        {
            cutoffDate = ParseTimestamp(sinceTimestamp);
        }

        var markdown = new StringBuilder();

        if (cutoffDate.HasValue)
        {
            // Changed files mode
            markdown.AppendLine($"# Changed Files Since {sinceTimestamp}");
            markdown.AppendLine($"**Project:** {projectName}");
            markdown.AppendLine($"**Description:** {projectInfo.Description}");
            markdown.AppendLine($"**Path:** `{projectPath}`");
            markdown.AppendLine($"**Cutoff Date:** {cutoffDate:yyyy-MM-dd HH:mm:ss} UTC");
            markdown.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            markdown.AppendLine();

            var changedFiles = GetChangedFiles(projectPath, cutoffDate.Value);

            if (changedFiles.Count == 0)
            {
                markdown.AppendLine("**No files changed since the specified timestamp.**");
                return markdown.ToString();
            }

            markdown.AppendLine($"**Total Changed Files:** {changedFiles.Count}");
            markdown.AppendLine();
            markdown.AppendLine("## Changed Files");
            markdown.AppendLine("```");

            foreach (var file in changedFiles.OrderBy(f => f.RelativePath))
            {
                var indicator = file.SizeKB > 15 ? "⚠️" : "✓";
                markdown.AppendLine($"├── {file.RelativePath}");
                markdown.AppendLine($"│   Modified: {file.LastModified:yyyy-MM-dd HH:mm:ss} UTC ({file.SizeDisplay}, {file.LineCount} lines) {indicator}");
            }

            markdown.AppendLine("```");
            return markdown.ToString();
        }

        // Full skeleton mode (existing logic)
        markdown.AppendLine($"# Project Skeleton: {projectName}");
        markdown.AppendLine($"**Description:** {projectInfo.Description}");
        markdown.AppendLine($"**Path:** `{projectPath}`");
        markdown.AppendLine($"**Generated:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        markdown.AppendLine();

        markdown.AppendLine("## Legend");
        markdown.AppendLine("- ✓ **Small file** (≤15KB) - Use `read_file_content`");
        markdown.AppendLine("- ⚠️ **Large file** (>15KB) - Use `analyze_c_sharp_file` + `fetch_method_implementation`");
        markdown.AppendLine();

        markdown.AppendLine("## Folder Structure");
        markdown.AppendLine("```");
        await GenerateFolderTreeAsync(projectPath, projectPath, markdown, "", true, cancellationToken);
        markdown.AppendLine("```");
        markdown.AppendLine();

        var slnFiles = Directory.EnumerateFiles(projectPath, "*.sln", SearchOption.TopDirectoryOnly).ToList();
        var slnxFiles = Directory.EnumerateFiles(projectPath, "*.slnx", SearchOption.TopDirectoryOnly).ToList();

        foreach (var slnFile in slnFiles)
        {
            await AppendFileContentAsync(markdown, slnFile, "Solution File (.sln)", cancellationToken);
        }

        foreach (var slnxFile in slnxFiles)
        {
            await AppendFileContentAsync(markdown, slnxFile, "Solution File (.slnx)", cancellationToken);
        }

        var csprojFiles = Directory.EnumerateFiles(projectPath, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !IsInExcludedDirectory(f, projectPath))
            .ToList();

        foreach (var csprojFile in csprojFiles)
        {
            var relativePath = Path.GetRelativePath(projectPath, csprojFile);
            await AppendFileContentAsync(markdown, csprojFile, $"Project File: {relativePath}", cancellationToken);
        }

        return markdown.ToString();
    }

    private DateTime ParseTimestamp(string timestamp)
    {
        // Try Unix timestamp first
        if (long.TryParse(timestamp, out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
        }

        // Try ISO 8601 format
        if (DateTime.TryParse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedDate))
        {
            return parsedDate.ToUniversalTime();
        }

        throw new ArgumentException(
            $"Invalid timestamp format: '{timestamp}'. " +
            "Expected Unix timestamp (e.g., '1705449600') or ISO 8601 date (e.g., '2026-01-17T00:00:00Z')");
    }

    private List<ChangedFileInfo> GetChangedFiles(string projectPath, DateTime cutoffDate)
    {
        var changedFiles = new List<ChangedFileInfo>();

        var allFiles = Directory.EnumerateFiles(projectPath, "*.*", SearchOption.AllDirectories)
            .Where(f => !IsInExcludedDirectory(f, projectPath))
            .ToList();

        foreach (var filePath in allFiles)
        {
            var fileInfo = new FileInfo(filePath);

            if (fileInfo.LastWriteTimeUtc > cutoffDate)
            {
                var relativePath = Path.GetRelativePath(projectPath, filePath);
                var lineCount = GetLineCountAsync(filePath, CancellationToken.None).GetAwaiter().GetResult();
                var sizeKB = fileInfo.Length / 1024.0;

                changedFiles.Add(new ChangedFileInfo
                {
                    RelativePath = relativePath,
                    LastModified = fileInfo.LastWriteTimeUtc,
                    SizeKB = sizeKB,
                    SizeDisplay = FormatFileSize(fileInfo.Length),
                    LineCount = lineCount
                });
            }
        }

        return changedFiles;
    }

    private class ChangedFileInfo
    {
        public string RelativePath { get; set; } = string.Empty;
        public DateTime LastModified { get; set; }
        public double SizeKB { get; set; }
        public string SizeDisplay { get; set; } = string.Empty;
        public int LineCount { get; set; }
    }

    private async Task GenerateFolderTreeAsync(
     string rootPath,
     string currentPath,
     StringBuilder output,
     string indent,
     bool isLast,
     CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dirInfo = new DirectoryInfo(currentPath);
        var displayName = currentPath == rootPath ? Path.GetFileName(rootPath) + "/" : dirInfo.Name + "/";
        var connector = isLast ? "└── " : "├── ";
        var extension = isLast ? "    " : "│   ";

        output.AppendLine($"{indent}{connector}{displayName}");

        var newIndent = indent + extension;

        try
        {
            var directories = Directory.EnumerateDirectories(currentPath)
                .Where(d => !ExcludedDirectories.Contains(Path.GetFileName(d)))
                .OrderBy(d => d)
                .ToList();

            var files = Directory.EnumerateFiles(currentPath)
                .OrderBy(f => f)
                .ToList();

            // AUTO-COLLAPSE LARGE FOLDERS
            const int COLLAPSE_THRESHOLD = 50;
            if (files.Count > COLLAPSE_THRESHOLD)
            {
                var relativePath = Path.GetRelativePath(rootPath, currentPath);
                output.AppendLine($"{newIndent}└── ({files.Count} files collapsed - use search_folder_files to explore)");
                output.AppendLine($"{newIndent}    Example: search_folder_files(\"{Path.GetFileName(rootPath)}\", \"{relativePath}\")");

                // Still recurse into subdirectories
                for (int i = 0; i < directories.Count; i++)
                {
                    var directory = directories[i];
                    var isLastItem = i == directories.Count - 1 && files.Count == 0;
                    await GenerateFolderTreeAsync(rootPath, directory, output, newIndent, isLastItem, cancellationToken);
                }
                return;
            }

            var totalItems = directories.Count + files.Count;
            var currentIndex = 0;

            foreach (var directory in directories)
            {
                currentIndex++;
                var isLastItem = currentIndex == totalItems;
                await GenerateFolderTreeAsync(rootPath, directory, output, newIndent, isLastItem, cancellationToken);
            }

            foreach (var file in files)
            {
                currentIndex++;
                var isLastItem = currentIndex == totalItems;
                var fileConnector = isLastItem ? "└── " : "├── ";
                var fileName = Path.GetFileName(file);
                var fileInfo = new FileInfo(file);
                var fileSizeKB = fileInfo.Length / 1024.0;
                var lineCount = await GetLineCountAsync(file, cancellationToken);
                var indicator = fileSizeKB > 15 ? "⚠️" : "✓";
                var sizeDisplay = FormatFileSize(fileInfo.Length);

                output.AppendLine($"{newIndent}{fileConnector}{fileName} ({sizeDisplay}, {lineCount} lines) {indicator}");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            output.AppendLine($"{newIndent}└── [Access Denied: {ex.Message}]");
        }
        catch (Exception ex)
        {
            output.AppendLine($"{newIndent}└── [Error: {ex.Message}]");
        }
    }

    // ADD THIS NEW METHOD after GenerateFolderTreeAsync (around line 723)

    public async Task<FolderSearchResponse> SearchFolderFilesAsync(
        string projectName,
        string folderPath,
        string? searchPattern = null,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        // Validation
        if (string.IsNullOrWhiteSpace(projectName))
            throw new ArgumentException("Project name cannot be null or empty.", nameof(projectName));

        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("Folder path cannot be null or empty.", nameof(folderPath));

        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 200) pageSize = 50;

        // Get project info
        if (!_projectMappings.TryGetValue(projectName, out var projectInfo))
            throw new KeyNotFoundException($"Project '{projectName}' not found. Available projects: {string.Join(", ", _projectMappings.Keys)}");

        // Normalize folder path
        folderPath = folderPath.Replace('/', Path.DirectorySeparatorChar)
                               .Replace('\\', Path.DirectorySeparatorChar);

        // Build full folder path
        var fullFolderPath = Path.Combine(projectInfo.Path, folderPath);

        // Validate folder exists
        if (!Directory.Exists(fullFolderPath))
            throw new DirectoryNotFoundException($"Folder '{folderPath}' not found in project '{projectName}'");

        // Security: Ensure folder is within project directory
        var normalizedProjectPath = Path.GetFullPath(projectInfo.Path);
        var normalizedFolderPath = Path.GetFullPath(fullFolderPath);

        if (!normalizedFolderPath.StartsWith(normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Access denied: Folder must be within the project directory.");

        // Get all files
        var allFiles = Directory.EnumerateFiles(fullFolderPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(f => !IsInExcludedDirectory(f, projectInfo.Path))
            .Select(f => new FileInfo(f))
            .ToList();

        // Apply search filter if provided
        if (!string.IsNullOrWhiteSpace(searchPattern))
        {
            var pattern = searchPattern.Trim();
            allFiles = allFiles.Where(f =>
                f.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Sort by filename
        var sortedFiles = allFiles.OrderBy(f => f.Name).ToList();

        // Pagination
        var totalFiles = sortedFiles.Count;
        var totalPages = (int)Math.Ceiling(totalFiles / (double)pageSize);
        var skip = (page - 1) * pageSize;

        var pagedFiles = new List<FileEntry>();
        foreach (var f in sortedFiles.Skip(skip).Take(pageSize))
        {
            var lineCount = await GetLineCountAsync(f.FullName, cancellationToken);
            pagedFiles.Add(new FileEntry
            {
                FileName = f.Name,
                RelativePath = Path.GetRelativePath(projectInfo.Path, f.FullName),
                SizeBytes = f.Length,
                SizeDisplay = FormatFileSize(f.Length),
                LineCount = lineCount,
                IsLargeFile = f.Length > 15 * 1024
            });
        }

        return new FolderSearchResponse
        {
            ProjectName = projectName,
            FolderPath = folderPath,
            SearchPattern = searchPattern,
            TotalFiles = totalFiles,
            Page = page,
            PageSize = pageSize,
            TotalPages = totalPages,
            Files = pagedFiles,
            HasNextPage = page < totalPages,
            HasPreviousPage = page > 1
        };
    }


    private async Task AppendFileContentAsync(
        StringBuilder markdown,
        string filePath,
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            markdown.AppendLine($"## {title}");
            markdown.AppendLine($"**File:** `{Path.GetFileName(filePath)}`");
            markdown.AppendLine();
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var language = extension switch
            {
                ".sln" => "text",
                ".slnx" => "xml",
                ".csproj" => "xml",
                _ => "text"
            };
            markdown.AppendLine($"```{language}");
            markdown.AppendLine(content);
            markdown.AppendLine("```");
            markdown.AppendLine();
        }
        catch (UnauthorizedAccessException ex)
        {
            markdown.AppendLine($"*[Access Denied: {ex.Message}]*");
            markdown.AppendLine();
        }
        catch (Exception ex)
        {
            markdown.AppendLine($"*[Error reading file: {ex.Message}]*");
            markdown.AppendLine();
        }
    }

    private static async Task<int> GetLineCountAsync(string filePath, CancellationToken cancellationToken)
    {
        try
        {
            return (await File.ReadAllLinesAsync(filePath, cancellationToken)).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.#}{sizes[order]}";
    }

    private bool IsInExcludedDirectory(string filePath, string rootPath)
    {
        var relativePath = Path.GetRelativePath(rootPath, filePath);
        var pathParts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return pathParts.Any(part => ExcludedDirectories.Contains(part));
    }



}

// Data models
public class ProjectInfo
{
    public string Path { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}


public class CSharpFileAnalysis
{
    public string ProjectName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long SizeInBytes { get; set; }
    public string Namespace { get; set; } = string.Empty;
    public List<string> UsingDirectives { get; set; } = new();
    public List<ClassInfo> Classes { get; set; } = new();
}

public class ClassInfo
{
    public string Name { get; set; } = string.Empty;
    public List<string> Modifiers { get; set; } = new();
    public string? BaseClass { get; set; }
    public List<string> Interfaces { get; set; } = new();
    public List<ParameterInfo> ConstructorParameters { get; set; } = new();
    public List<MethodInfo> Methods { get; set; } = new();
    public List<PropertyInfo> Properties { get; set; } = new();
    public List<FieldInfo> Fields { get; set; } = new();
    public List<AttributeInfo> Attributes { get; set; } = new();  // ADD THIS LINE
    public int LineNumber { get; set; }
}

public class MethodInfo
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public List<string> Modifiers { get; set; } = new();
    public List<ParameterInfo> Parameters { get; set; } = new();
    public List<AttributeInfo> Attributes { get; set; } = new();
    public string? XmlDocumentation { get; set; }

    /// <summary>
    /// Line number where the method declaration starts (1-based)
    /// Includes attributes if present
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Starting line number of the method (including attributes and documentation)
    /// </summary>
    public int LineNumberStart { get; set; }

    /// <summary>
    /// Ending line number of the method (last line of the closing brace)
    /// </summary>
    public int LineNumberEnd { get; set; }
}

public class FieldInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public List<string> Modifiers { get; set; } = new();
    public bool IsReadOnly { get; set; }
    public bool IsStatic { get; set; }
    public bool IsConst { get; set; }
    public List<AttributeInfo> Attributes { get; set; } = new();  // ADD THIS LINE
    public int LineNumber { get; set; }

    /// <summary>
    /// Starting line number of the field declaration
    /// </summary>
    public int LineNumberStart { get; set; }

    /// <summary>
    /// Ending line number of the field declaration
    /// </summary>
    public int LineNumberEnd { get; set; }
}

public class PropertyInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public List<string> Modifiers { get; set; } = new();
    public bool HasGetter { get; set; }
    public bool HasSetter { get; set; }
    public List<AttributeInfo> Attributes { get; set; } = new();  // ADD THIS LINE

    /// <summary>
    /// Line number where the property declaration starts (1-based)
    /// </summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// Starting line number of the property (including attributes)
    /// </summary>
    public int LineNumberStart { get; set; }

    /// <summary>
    /// Ending line number of the property
    /// </summary>
    public int LineNumberEnd { get; set; }
}
public class ParameterInfo
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DefaultValue { get; set; }
}

public class AttributeInfo
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Properties { get; set; } = new();
}

public class FolderSearchResponse
{
    public string ProjectName { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string? SearchPattern { get; set; }
    public int TotalFiles { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public List<FileEntry> Files { get; set; } = new();
    public bool HasNextPage { get; set; }
    public bool HasPreviousPage { get; set; }
}

public class FileEntry
{
    public string FileName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string SizeDisplay { get; set; } = string.Empty;
    public int LineCount { get; set; }
    public bool IsLargeFile { get; set; }
}