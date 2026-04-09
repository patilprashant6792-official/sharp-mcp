// ProjectExplorationServices/MethodCallGraphService.cs

using MCP.Core.Configuration;
using MCP.Core.Models;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace MCP.Core.Services;

public class MethodCallGraphService : IMethodCallGraphService
{
    private readonly IProjectSkeletonService _skeletonService;
    private readonly ILogger<MethodCallGraphService> _logger;

    public MethodCallGraphService(
        IProjectSkeletonService skeletonService,
        ILogger<MethodCallGraphService> logger)
    {
        _skeletonService = skeletonService;
        _logger = logger;
    }

    public async Task<MethodCallGraph> AnalyzeMethodDependenciesAsync(
        string projectName,
        string relativeFilePath,
        string methodName,
        string? className = null,
        bool includeTests = false,
        int depth = 1,
        int page = 1,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var projectMappings = _skeletonService.GetAvailableProjectsWithInfo();

        if (!projectMappings.TryGetValue(projectName, out var projectInfo))
            throw new ArgumentException($"Project '{projectName}' not found");

        var projectPath = projectInfo.Path;
        var fullFilePath = Path.Combine(projectPath, relativeFilePath);

        if (!File.Exists(fullFilePath))
            throw new FileNotFoundException($"File not found: {relativeFilePath}");

        // Find target method
        var fileAnalysis = await _skeletonService.AnalyzeCSharpFileAsync(
            projectName, relativeFilePath, includePrivateMembers: true, cancellationToken);

        var (targetClass, targetMethod) = FindTargetMethod(fileAnalysis, methodName, className);

        // Find all C# files
        var allCsFiles = Directory.GetFiles(projectPath, "*.cs", SearchOption.AllDirectories)
            .Where(f => includeTests || !f.Contains("\\Tests\\") && !f.Contains("\\Test\\"))
            .ToList();

        // Full caller scan — always complete, pagination applied after
        var allCallers = await FindCallersAsync(
            projectPath, allCsFiles, targetMethod.Name, fullFilePath, cancellationToken);

        var effectivePageSize = Math.Clamp(pageSize, 1, 200);
        var totalPages = (int)Math.Ceiling((double)allCallers.Count / effectivePageSize);
        var effectivePage = allCallers.Count == 0 ? 1 : Math.Clamp(page, 1, totalPages);

        return new MethodCallGraph
        {
            ProjectName = projectName,
            FilePath = relativeFilePath,
            ClassName = targetClass.Name,
            MethodName = targetMethod.Name,
            LineNumber = targetMethod.LineNumber,
            TotalCallers = allCallers.Count,
            Page = effectivePage,
            PageSize = effectivePageSize,
            CalledBy = allCallers
                .Skip((effectivePage - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToList(),
            Calls = await FindOutgoingCallsAsync(
                projectPath, fullFilePath, targetClass.Name, targetMethod.Name, cancellationToken)
        };
    }

    private (ClassInfo targetClass, MethodInfo targetMethod) FindTargetMethod(
        CSharpFileAnalysis fileAnalysis, string methodName, string? className)
    {
        var candidateClasses = fileAnalysis.Classes
            .Where(c => c.Methods.Any(m => m.Name == methodName))
            .ToList();

        if (candidateClasses.Count == 0)
            throw new ArgumentException($"Method '{methodName}' not found");

        ClassInfo targetClass;
        if (candidateClasses.Count == 1)
        {
            targetClass = candidateClasses[0];
        }
        else
        {
            if (string.IsNullOrEmpty(className))
            {
                var classNames = string.Join(", ", candidateClasses.Select(c => c.Name));
                throw new ArgumentException(
                    $"Multiple classes have '{methodName}': {classNames}. Specify className.");
            }
            targetClass = candidateClasses.FirstOrDefault(c => c.Name == className)
                ?? throw new ArgumentException($"Class '{className}' not found");
        }

        var targetMethod = targetClass.Methods.First(m => m.Name == methodName);
        return (targetClass, targetMethod);
    }

    private async Task<List<CallSite>> FindCallersAsync(
        string projectPath,
        List<string> allCsFiles,
        string targetMethodName,
        string targetFilePath,
        CancellationToken cancellationToken)
    {
        var callers = new List<CallSite>();

        foreach (var filePath in allCsFiles)
        {
            if (filePath == targetFilePath) continue;

            try
            {
                var content = await File.ReadAllTextAsync(filePath, cancellationToken);
                var tree = CSharpSyntaxTree.ParseText(content);
                var root = await tree.GetRootAsync(cancellationToken);

                var invocations = root.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(inv => IsCallToMethod(inv, targetMethodName));

                foreach (var invocation in invocations)
                {
                    var containingMethod = invocation.Ancestors()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault();

                    var containingClass = invocation.Ancestors()
                        .OfType<ClassDeclarationSyntax>()
                        .FirstOrDefault();

                    if (containingMethod == null || containingClass == null) continue;

                    var lineNumber = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
                    var relativeFilePath = Path.GetRelativePath(projectPath, filePath);

                    // Get class count for resolution hint
                    var classCount = root.DescendantNodes()
                        .OfType<ClassDeclarationSyntax>()
                        .Count();

                    callers.Add(new CallSite
                    {
                        ClassName = containingClass.Identifier.Text,
                        MethodName = containingMethod.Identifier.Text,
                        FilePath = relativeFilePath,
                        LineNumber = lineNumber,
                        Resolution = new MethodResolutionInfo
                        {
                            ExactClassName = containingClass.Identifier.Text,
                            ClassesInFile = classCount,
                            IsSingleClassFile = classCount == 1
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to analyze {File}", filePath);
            }
        }

        return callers;
    }

    // 🔥 NEW METHOD: Find what THIS method calls (outgoing calls)
    private async Task<List<CallSite>> FindOutgoingCallsAsync(
        string projectPath,
        string filePath,
        string className,
        string methodName,
        CancellationToken cancellationToken)
    {
        var calls = new List<CallSite>();

        try
        {
            var content = await File.ReadAllTextAsync(filePath, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(content);
            var root = await tree.GetRootAsync(cancellationToken);

            // Find the target method in this file
            var targetMethod = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m =>
                    m.Identifier.Text == methodName &&
                    m.Ancestors().OfType<ClassDeclarationSyntax>()
                        .FirstOrDefault()?.Identifier.Text == className);

            if (targetMethod == null)
            {
                _logger.LogWarning("Method {Method} not found in class {Class}", methodName, className);
                return calls;
            }

            // Find all invocations inside this method
            var invocations = targetMethod.DescendantNodes()
                .OfType<InvocationExpressionSyntax>();

            foreach (var invocation in invocations)
            {
                var (calledMethodName, calledClassName) = ExtractMethodInfo(invocation);

                if (string.IsNullOrEmpty(calledMethodName)) continue;

                var lineNumber = tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1;
                var relativeFilePath = Path.GetRelativePath(projectPath, filePath);

                calls.Add(new CallSite
                {
                    MethodName = calledMethodName,
                    ClassName = calledClassName ?? "Unknown",
                    FilePath = relativeFilePath,
                    LineNumber = lineNumber,
                    Resolution = new MethodResolutionInfo
                    {
                        ExactClassName = calledClassName ?? "Unknown",
                        IsSingleClassFile = false
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze outgoing calls from {File}", filePath);
        }

        return calls;
    }

    private (string? methodName, string? className) ExtractMethodInfo(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            // instance.Method() or _field.Method()
            MemberAccessExpressionSyntax memberAccess => (
                memberAccess.Name.Identifier.Text,
                ExtractClassNameFromExpression(memberAccess.Expression)
            ),

            // Method() - same class call
            IdentifierNameSyntax identifier => (
                identifier.Identifier.Text,
                "SameClass"
            ),

            _ => (null, null)
        };
    }

    private string? ExtractClassNameFromExpression(ExpressionSyntax expression)
    {
        return expression switch
        {
            // ClassName.StaticMethod()
            IdentifierNameSyntax identifier => identifier.Identifier.Text,

            // this.Method() or base.Method()
            ThisExpressionSyntax => "SameClass",
            BaseExpressionSyntax => "BaseClass",

            // _service.Method() - field/property
            MemberAccessExpressionSyntax memberAccess =>
                ExtractClassNameFromExpression(memberAccess.Expression),

            _ => null
        };
    }

    private bool IsCallToMethod(InvocationExpressionSyntax invocation, string methodName)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess =>
                memberAccess.Name.Identifier.Text == methodName,
            IdentifierNameSyntax identifier =>
                identifier.Identifier.Text == methodName,
            _ => false
        };
    }
}
