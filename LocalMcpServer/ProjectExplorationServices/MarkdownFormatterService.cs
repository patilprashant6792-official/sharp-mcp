using MCP.Core.Models;
using System.Text;

namespace MCP.Core.Services;

public interface IMarkdownFormatterService
{
    string FormatCSharpAnalysis(CSharpFileAnalysis analysis);
}

public class MarkdownFormatterService : IMarkdownFormatterService
{
    public string FormatCSharpAnalysis(CSharpFileAnalysis analysis)
    {
        var md = new StringBuilder();

        // Header with metadata
        md.AppendLine($"# C# File Analysis: `{analysis.FileName}`");
        md.AppendLine();
        md.AppendLine($"**Project:** {analysis.ProjectName}  ");
        md.AppendLine($"**Path:** `{analysis.FilePath}`  ");
        md.AppendLine($"**Namespace:** `{analysis.Namespace}`");
        md.AppendLine();

        // Using directives (collapsed)
        if (analysis.UsingDirectives.Any())
        {
            md.AppendLine("<details>");
            md.AppendLine("<summary>Using Directives</summary>");
            md.AppendLine();
            md.AppendLine("```csharp");
            foreach (var usingDir in analysis.UsingDirectives)
            {
                md.AppendLine($"using {usingDir};");
            }
            md.AppendLine("```");
            md.AppendLine("</details>");
            md.AppendLine();
        }

        // Classes
        if (!analysis.Classes.Any())
        {
            md.AppendLine("*No public classes found*");
            return md.ToString();
        }

        md.AppendLine($"## Classes ({analysis.Classes.Count})");
        md.AppendLine();

        foreach (var classInfo in analysis.Classes)
        {
            FormatClass(md, classInfo, analysis.Namespace);
            md.AppendLine();
        }

        return md.ToString();
    }

    private void FormatClass(StringBuilder md, ClassInfo classInfo, string namespaceName)
    {
        md.AppendLine($"### `{classInfo.Name}` (Line {classInfo.LineNumber})");
        md.AppendLine();

        // Full class declaration
        md.AppendLine("```csharp");

        var modifiers = string.Join(" ", classInfo.Modifiers);
        var inheritance = BuildInheritanceString(classInfo);

        if (string.IsNullOrEmpty(inheritance))
        {
            md.AppendLine($"{modifiers} class {classInfo.Name}");
        }
        else
        {
            md.AppendLine($"{modifiers} class {classInfo.Name} : {inheritance}");
        }

        md.AppendLine("```");
        md.AppendLine();

        // Constructor (if exists)
        if (classInfo.ConstructorParameters.Any())
        {
            md.AppendLine("#### Constructor");
            md.AppendLine();
            md.AppendLine("```csharp");
            md.AppendLine($"public {classInfo.Name}(");

            var parameters = classInfo.ConstructorParameters
                .Select(p => $"    {p.Type} {p.Name}")
                .ToList();

            md.AppendLine(string.Join(",\n", parameters));
            md.AppendLine(")");
            md.AppendLine("```");
            md.AppendLine();

            // Constructor dependencies summary
            md.AppendLine("**Dependencies:**");
            foreach (var param in classInfo.ConstructorParameters)
            {
                md.AppendLine($"- `{param.Type}` → `{param.Name}`");
            }
            md.AppendLine();
        }

        // Properties (if any)
        if (classInfo.Properties.Any())
        {
            md.AppendLine($"#### Properties ({classInfo.Properties.Count})");
            md.AppendLine();
            md.AppendLine("```csharp");
            foreach (var prop in classInfo.Properties)
            {
                var modi = string.Join(" ", prop.Modifiers);
                var accessors = BuildAccessorString(prop);
                var lineInfo = FormatLineRange(prop.LineNumberStart, prop.LineNumberEnd);
                md.AppendLine($"{modi} {prop.Type} {prop.Name} {{ {accessors} }} {lineInfo}");
            }
            md.AppendLine("```");
            md.AppendLine();
        }

        // Fields (if any)
        if (classInfo.Fields.Any())
        {
            md.AppendLine();
            md.AppendLine("#### Fields (" + classInfo.Fields.Count + ")");
            md.AppendLine();
            md.AppendLine("```csharp");
            foreach (var field in classInfo.Fields)
            {
                var modif = string.Join(" ", field.Modifiers);
                var lineInfo = FormatLineRange(field.LineNumberStart, field.LineNumberEnd);
                md.AppendLine($"{modif} {field.Type} {field.Name}; {lineInfo}");
            }
            md.AppendLine("```");
            md.AppendLine();
        }

        // Methods (if any)
        if (classInfo.Methods.Any())
        {
            md.AppendLine($"#### Methods ({classInfo.Methods.Count})");
            md.AppendLine();

            foreach (var method in classInfo.Methods)
            {
                FormatMethod(md, method);
            }
        }
    }

    private void FormatMethod(StringBuilder md, MethodInfo method)
    {
        md.AppendLine("```csharp");

        // Display attributes before method signature
        if (method.Attributes.Any())
        {
            foreach (var attr in method.Attributes)
            {
                if (attr.Properties.Any())
                {
                    var args = string.Join(", ", attr.Properties.Select(kvp =>
                        kvp.Key.StartsWith("arg")
                            ? $"\"{kvp.Value}\""
                            : $"{kvp.Key} = \"{kvp.Value}\""));
                    md.AppendLine($"[{attr.Name}({args})]");
                }
                else
                {
                    md.AppendLine($"[{attr.Name}]");
                }
            }
        }

        var modifiers = string.Join(" ", method.Modifiers);
        var paramStr = FormatParameters(method.Parameters);
        var lineInfo = FormatLineRange(method.LineNumberStart, method.LineNumberEnd);

        md.AppendLine($"{modifiers} {method.ReturnType} {method.Name}({paramStr}) {lineInfo}");
        md.AppendLine("```");
        md.AppendLine();
    }

    private string FormatLineRange(int start, int end)
    {
        if (start == 0 && end == 0)
            return string.Empty;

        if (start == end)
            return $"// Line {start}";

        return $"// Lines {start}-{end}";
    }

    private string FormatParameters(List<ParameterInfo> parameters)
    {
        if (!parameters.Any())
            return string.Empty;

        // Short parameters (inline)
        if (parameters.Count <= 2 && parameters.All(p => p.Type.Length + p.Name.Length < 40))
        {
            return string.Join(", ", parameters.Select(p =>
            {
                var defaultVal = !string.IsNullOrEmpty(p.DefaultValue) ? $" = {p.DefaultValue}" : "";
                return $"{p.Type} {p.Name}{defaultVal}";
            }));
        }

        // Long parameters (multiline)
        var lines = parameters.Select(p =>
        {
            var defaultVal = !string.IsNullOrEmpty(p.DefaultValue) ? $" = {p.DefaultValue}" : "";
            return $"\n    {p.Type} {p.Name}{defaultVal}";
        });

        return string.Join(",", lines) + "\n";
    }

    private string BuildInheritanceString(ClassInfo classInfo)
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(classInfo.BaseClass))
        {
            parts.Add(classInfo.BaseClass);
        }

        parts.AddRange(classInfo.Interfaces);

        return string.Join(", ", parts);
    }

    private string BuildAccessorString(PropertyInfo prop)
    {
        var parts = new List<string>();

        if (prop.HasGetter) parts.Add("get;");
        if (prop.HasSetter) parts.Add("set;");

        return string.Join(" ", parts);
    }
}