using MCP.Core.Configuration;
using MCP.Core.Models;
using System.Text;

namespace MCP.Core.Services;

public class MethodFormatterService : IMethodFormatterService
{
    public string FormatMethodImplementationsBatch(List<MethodImplementationInfo> methods)
    {
        if (methods == null || methods.Count == 0)
            return "No methods provided.";

        var sb = new StringBuilder();
        var firstMethod = methods[0];

        // Shared header (project, file, class, namespace)
        sb.AppendLine($"# Batch Method Implementations: {methods.Count} method{(methods.Count > 1 ? "s" : "")}");
        sb.AppendLine();
        sb.AppendLine("## Shared Context");
        sb.AppendLine();
        sb.AppendLine("| Property | Value |");
        sb.AppendLine("|----------|-------|");
        sb.AppendLine($"| **Project** | `{firstMethod.ProjectName}` |");
        sb.AppendLine($"| **File** | `{firstMethod.FilePath}` |");
        sb.AppendLine($"| **Class** | `{firstMethod.ClassName}` |");
        sb.AppendLine($"| **Namespace** | `{firstMethod.Namespace}` |");
        sb.AppendLine();

        // Quick index
        sb.AppendLine("## Methods Index");
        sb.AppendLine();
        for (int i = 0; i < methods.Count; i++)
        {
            var method = methods[i];
            sb.AppendLine($"{i + 1}. **{method.MethodName}** (Line {method.LineNumber}) → `{method.ReturnType}`");
        }
        sb.AppendLine();

        // Individual methods - simplified format matching single method output
        for (int idx = 0; idx < methods.Count; idx++)
        {
            var method = methods[idx];
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## Method {idx + 1}: `{method.MethodName}`");
            sb.AppendLine();

            // Compact metadata (just the essentials that differ per method)
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");
            sb.AppendLine($"| **Line Number** | {method.LineNumber} |");
            sb.AppendLine($"| **Return Type** | `{method.ReturnType}` |");
            sb.AppendLine($"| **Modifiers** | `{string.Join(" ", method.Modifiers)}` |");
            sb.AppendLine();

            // Implementation with line numbers
            sb.AppendLine("## Implementation");
            sb.AppendLine();
            sb.AppendLine("```csharp");

            if (!string.IsNullOrWhiteSpace(method.FullMethodCode))
            {
                // Split full method code into lines
                var codeLines = method.FullMethodCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                // Calculate starting line: subtract attribute count
                var attributeLineCount = method.Attributes.Count;
                var startingLine = method.LineNumber - attributeLineCount;

                var currentLine = startingLine;
                foreach (var line in codeLines)
                {
                    sb.AppendLine($"{currentLine,4} | {line}");
                    currentLine++;
                }
            }
            else
            {
                sb.AppendLine($"{method.LineNumber,4} | // Abstract method - no implementation");
            }

            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }
    public string FormatMethodImplementation(MethodImplementationInfo methodInfo)
    {
        var md = new StringBuilder();

        // Compact Metadata Section
        md.AppendLine($"# Method: `{methodInfo.MethodName}`");
        md.AppendLine();
        md.AppendLine("## Metadata");
        md.AppendLine();
        md.AppendLine("| Property | Value |");
        md.AppendLine("|----------|-------|");
        md.AppendLine($"| **Project** | `{methodInfo.ProjectName}` |");
        md.AppendLine($"| **File** | `{methodInfo.FilePath}` |");
        md.AppendLine($"| **Class** | `{methodInfo.ClassName}` |");
        md.AppendLine($"| **Namespace** | `{methodInfo.Namespace}` |");
        md.AppendLine($"| **Line Number** | {methodInfo.LineNumber} |");
        md.AppendLine($"| **Return Type** | `{methodInfo.ReturnType}` |");
        md.AppendLine($"| **Modifiers** | `{string.Join(" ", methodInfo.Modifiers)}` |");
        md.AppendLine();

        // Implementation with line numbers (includes attributes)
        md.AppendLine("## Implementation");
        md.AppendLine();
        md.AppendLine("```csharp");

        if (!string.IsNullOrWhiteSpace(methodInfo.FullMethodCode))
        {
            // Split full method code (which includes attributes) into lines
            var codeLines = methodInfo.FullMethodCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var currentLine = methodInfo.LineNumber;

            // Adjust starting line if attributes exist (they come before the method)
            var attributeCount = methodInfo.Attributes.Count;
            if (attributeCount > 0)
            {
                currentLine -= attributeCount;
            }

            foreach (var line in codeLines)
            {
                md.AppendLine($"{currentLine,4} | {line}");
                currentLine++;
            }
        }
        else
        {
            md.AppendLine("// Abstract method - no implementation");
        }

        md.AppendLine("```");

        return md.ToString();
    }


    public string FormatMethodCallGraph(MethodCallGraph graph)
    {
        var md = new StringBuilder();

        md.AppendLine($"# Method Call Graph: `{graph.MethodName}`");
        md.AppendLine();
        md.AppendLine($"**Location:** `{graph.FilePath}` | Class: `{graph.ClassName}` | Line: {graph.LineNumber}");
        md.AppendLine();

        // INCOMING CALLS (CalledBy)
        if (graph.CalledBy.Count == 0)
        {
            md.AppendLine("**Called By:** None (no C# callers detected - may be external entry point, test method, or unused code)");
        }
        else
        {
            md.AppendLine($"**Called By ({graph.CalledBy.Count}):**");
            md.AppendLine();

            foreach (var caller in graph.CalledBy)
            {
                var needsClassName = !caller.Resolution.IsSingleClassFile;
                var classHint = needsClassName ? $" (className: `{caller.Resolution.ExactClassName}`)" : "";

                md.AppendLine($"- **{caller.ClassName}.{caller.MethodName}**");
                md.AppendLine($"  - File: `{caller.FilePath}:{caller.LineNumber}`{classHint}");
            }
            md.AppendLine();
        }

        // OUTGOING CALLS (Calls)
        if (graph.Calls.Count == 0)
        {
            md.AppendLine("**Calls:** None (no outgoing method calls detected)");
        }
        else
        {
            md.AppendLine($"**Calls ({graph.Calls.Count}):**");
            md.AppendLine();

            // Group by type
            var systemCalls = graph.Calls.Where(c => c.ClassName.Contains("System") || c.ClassName == "Unknown").ToList();
            var sameCalls = graph.Calls.Where(c => c.ClassName == "SameClass").ToList();
            var externalCalls = graph.Calls.Except(systemCalls).Except(sameCalls).ToList();

            if (sameCalls.Any())
            {
                md.AppendLine("**Same Class:**");
                foreach (var call in sameCalls.Take(5))
                {
                    md.AppendLine($"- `{call.MethodName}()` - Line {call.LineNumber}");
                }
                if (sameCalls.Count > 5)
                    md.AppendLine($"  - ...and {sameCalls.Count - 5} more");
                md.AppendLine();
            }

            if (externalCalls.Any())
            {
                md.AppendLine("**External/Services:**");
                foreach (var call in externalCalls.Take(5))
                {
                    md.AppendLine($"- `{call.ClassName}.{call.MethodName}()` - Line {call.LineNumber}");
                }
                if (externalCalls.Count > 5)
                    md.AppendLine($"  - ...and {externalCalls.Count - 5} more");
                md.AppendLine();
            }

            if (systemCalls.Any())
            {
                md.AppendLine("**System/Framework:**");
                foreach (var call in systemCalls.Take(3))
                {
                    md.AppendLine($"- `{call.MethodName}()` - Line {call.LineNumber}");
                }
                if (systemCalls.Count > 3)
                    md.AppendLine($"  - ...and {systemCalls.Count - 3} more framework calls");
                md.AppendLine();
            }
        }

        md.AppendLine($"**Total References:** Incoming: {graph.CalledBy.Count}, Outgoing: {graph.Calls.Count} | **Files Affected:** {graph.CalledBy.Select(c => c.FilePath).Distinct().Count()}");

        return md.ToString();
    }
}