using MCP.Core.Models;
using System.Text;

namespace MCP.Core.Services;

public interface ICodeSearchFormatterService
{
    string FormatSearchResults(CodeSearchResponse response);
}

public class CodeSearchFormatterService : ICodeSearchFormatterService
{
    public string FormatSearchResults(CodeSearchResponse response)
    {
        var md = new StringBuilder();

        md.AppendLine($"# 🔍 Search Results: \"{response.Query}\" in {response.ProjectName}");
        md.AppendLine();
        md.AppendLine($"**Total Results:** {response.TotalResults} (showing top {response.Results.Count})  ");
        md.AppendLine($"**Files Scanned:** {response.FilesScanned}  ");
        md.AppendLine($"**Search Duration:** {response.SearchDuration.TotalMilliseconds:F0}ms");
        md.AppendLine();

        if (response.Results.Count == 0)
        {
            md.AppendLine("*No results found.*");
            md.AppendLine();
            md.AppendLine("## 💡 Suggestions");
            md.AppendLine("- Check spelling");
            md.AppendLine("- Try broader search terms");
            md.AppendLine("- Search without case sensitivity");
            return md.ToString();
        }

        var groupedResults = response.Results.GroupBy(r => r.MemberType);

        foreach (var group in groupedResults.OrderBy(g => g.Key))
        {
            md.AppendLine($"## {GetMemberTypeIcon(group.Key)} {group.Key}s ({group.Count()})");
            md.AppendLine();

            foreach (var result in group)
            {
                md.AppendLine($"### `{result.Name}`");
                md.AppendLine($"- **Location:** `{result.FilePath}:{result.LineNumber}`");

                if (!string.IsNullOrEmpty(result.ParentClass))
                    md.AppendLine($"- **Parent:** `{result.ParentClass}`");

                if (!string.IsNullOrEmpty(result.Signature))
                    md.AppendLine($"- **Signature:** `{result.Signature}`");

                if (!string.IsNullOrEmpty(result.TypeInfo))
                    md.AppendLine($"- **Type:** `{result.TypeInfo}`");

                if (result.Modifiers.Any())
                    md.AppendLine($"- **Modifiers:** `{string.Join(" ", result.Modifiers)}`");

                md.AppendLine();
            }
        }

        md.AppendLine("---");
        md.AppendLine("## 💡 Next Steps");
        md.AppendLine();

        var firstResult = response.Results.FirstOrDefault();
        if (firstResult != null)
        {
            md.AppendLine($"**Explore this file:**");
            md.AppendLine($"```");
            md.AppendLine($"analyze_c_sharp_file(\"{response.ProjectName}\", \"{firstResult.FilePath}\")");
            md.AppendLine($"```");
            md.AppendLine();

            if (firstResult.MemberType == CodeMemberType.Method)
            {
                md.AppendLine($"**Get method implementation:**");
                md.AppendLine($"```");
                md.AppendLine($"fetch_method_implementation(\"{response.ProjectName}\", \"{firstResult.FilePath}\", \"{firstResult.Name}\")");
                md.AppendLine($"```");
            }
        }

        return md.ToString();
    }

    private static string GetMemberTypeIcon(CodeMemberType type)
    {
        return type switch
        {
            CodeMemberType.Class => "📦",
            CodeMemberType.Interface => "🔌",
            CodeMemberType.Method => "🔧",
            CodeMemberType.Property => "🏷️",
            CodeMemberType.Field => "📌",
            CodeMemberType.Enum => "🎯",
            CodeMemberType.Struct => "🧱",
            _ => "❓"
        };
    }
}