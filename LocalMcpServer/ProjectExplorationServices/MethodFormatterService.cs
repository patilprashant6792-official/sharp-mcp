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

        // Individual methods
        for (int idx = 0; idx < methods.Count; idx++)
        {
            var method = methods[idx];
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"## Method {idx + 1}: `{method.MethodName}`");
            sb.AppendLine();

            // Metadata table
            sb.AppendLine("| Property | Value |");
            sb.AppendLine("|----------|-------|");
            sb.AppendLine($"| **Line Number** | {method.LineNumber} |");
            sb.AppendLine($"| **Return Type** | `{method.ReturnType}` |");
            sb.AppendLine($"| **Modifiers** | `{string.Join(" ", method.Modifiers)}` |");

            var characteristics = new List<string>();
            if (method.IsAsync) characteristics.Add("Async");
            if (method.IsStatic) characteristics.Add("Static");
            if (method.IsVirtual) characteristics.Add("Virtual");
            if (method.IsOverride) characteristics.Add("Override");
            if (method.IsAbstract) characteristics.Add("Abstract");

            if (characteristics.Any())
                sb.AppendLine($"| **Characteristics** | {string.Join(", ", characteristics)} |");

            sb.AppendLine();

            // XML Documentation (if present)
            if (!string.IsNullOrWhiteSpace(method.XmlDocumentation))
            {
                sb.AppendLine("### Documentation");
                sb.AppendLine();
                sb.AppendLine("```xml");
                sb.AppendLine(method.XmlDocumentation.Trim());
                sb.AppendLine("```");
                sb.AppendLine();
            }

            // Signature
            sb.AppendLine("### Signature");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(method.FullSignature);
            sb.AppendLine("```");
            sb.AppendLine();

            // Parameters (if any)
            if (method.Parameters.Any())
            {
                sb.AppendLine("### Parameters");
                sb.AppendLine();
                sb.AppendLine("| Name | Type | Default |");
                sb.AppendLine("|------|------|---------|");

                foreach (var param in method.Parameters)
                {
                    var defaultValue = string.IsNullOrWhiteSpace(param.DefaultValue)
                        ? "-"
                        : $"`{param.DefaultValue}`";
                    sb.AppendLine($"| `{param.Name}` | `{param.Type}` | {defaultValue} |");
                }
                sb.AppendLine();
            }

            // Attributes (if any)
            if (method.Attributes.Any())
            {
                sb.AppendLine("### Attributes");
                sb.AppendLine();
                foreach (var attr in method.Attributes)
                {
                    sb.Append($"- `[{attr.Name}");
                    if (attr.Properties.Any())
                    {
                        var props = attr.Properties.Select(kvp => $"{kvp.Key}={kvp.Value}");
                        sb.Append($"({string.Join(", ", props)})");
                    }
                    sb.AppendLine("]`");
                }
                sb.AppendLine();
            }

            // Implementation
            sb.AppendLine("### Implementation");
            sb.AppendLine();
            sb.AppendLine("```csharp");
            sb.AppendLine(method.FullMethodCode);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }
    public string FormatMethodImplementation(MethodImplementationInfo methodInfo)
    {
        var md = new StringBuilder();

        // Header
        md.AppendLine($"# Method: `{methodInfo.MethodName}`");
        md.AppendLine();

        // Metadata table
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

        // Method characteristics
        var characteristics = new List<string>();
        if (methodInfo.IsAsync) characteristics.Add("Async");
        if (methodInfo.IsStatic) characteristics.Add("Static");
        if (methodInfo.IsVirtual) characteristics.Add("Virtual");
        if (methodInfo.IsOverride) characteristics.Add("Override");
        if (methodInfo.IsAbstract) characteristics.Add("Abstract");

        if (characteristics.Any())
        {
            md.AppendLine($"| **Characteristics** | {string.Join(", ", characteristics)} |");
        }

        md.AppendLine();

        // XML Documentation (if exists)
        if (!string.IsNullOrWhiteSpace(methodInfo.XmlDocumentation))
        {
            md.AppendLine("## Documentation");
            md.AppendLine();
            md.AppendLine("```xml");
            md.AppendLine(methodInfo.XmlDocumentation.Trim());
            md.AppendLine("```");
            md.AppendLine();
        }

        // Attributes (if any)
        if (methodInfo.Attributes.Any())
        {
            md.AppendLine("## Attributes");
            md.AppendLine();
            foreach (var attr in methodInfo.Attributes)
            {
                md.AppendLine($"- `[{attr.Name}]`");
                if (attr.Properties.Any())
                {
                    foreach (var prop in attr.Properties)
                    {
                        md.AppendLine($"  - `{prop.Key}`: {prop.Value}");
                    }
                }
            }
            md.AppendLine();
        }

        // Signature
        md.AppendLine("## Signature");
        md.AppendLine();
        md.AppendLine("```csharp");
        md.AppendLine(methodInfo.FullSignature);
        md.AppendLine("```");
        md.AppendLine();

        // Parameters (if any)
        if (methodInfo.Parameters.Any())
        {
            md.AppendLine("## Parameters");
            md.AppendLine();
            md.AppendLine("| Name | Type | Default |");
            md.AppendLine("|------|------|---------|");
            foreach (var param in methodInfo.Parameters)
            {
                var defaultVal = string.IsNullOrEmpty(param.DefaultValue) ? "-" : $"`{param.DefaultValue}`";
                md.AppendLine($"| `{param.Name}` | `{param.Type}` | {defaultVal} |");
            }
            md.AppendLine();
        }

        // Method Body with Line Numbers
        md.AppendLine("## Implementation");
        md.AppendLine();

        if (!string.IsNullOrWhiteSpace(methodInfo.MethodBody))
        {
            md.AppendLine("```csharp");

            // Split method body into lines and add line numbers
            var bodyLines = methodInfo.MethodBody.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var currentLine = methodInfo.LineNumber;

            // Add signature line(s)
            var signatureLines = methodInfo.FullSignature.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            foreach (var sigLine in signatureLines)
            {
                md.AppendLine($"{currentLine,4} | {sigLine}");
                currentLine++;
            }

            // Add body lines
            foreach (var line in bodyLines)
            {
                md.AppendLine($"{currentLine,4} | {line}");
                currentLine++;
            }

            md.AppendLine("```");
        }
        else
        {
            md.AppendLine("*Abstract method - no implementation*");
        }

        md.AppendLine();

        // Full Method Code (collapsed for reference)
        md.AppendLine("<details>");
        md.AppendLine("<summary>📋 Full Method Code (Copy-Ready)</summary>");
        md.AppendLine();
        md.AppendLine("```csharp");
        md.AppendLine(methodInfo.FullMethodCode);
        md.AppendLine("```");
        md.AppendLine();
        md.AppendLine("</details>");

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