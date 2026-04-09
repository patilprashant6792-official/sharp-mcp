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

        // ✨ ENHANCED: Different header for wildcard vs single-project searches
        if (response.ProjectName == "*")
        {
            md.AppendLine($"# 🔍 Cross-Project Search Results: \"{response.Query}\"");
            md.AppendLine();
            md.AppendLine($"**Projects Searched:** {response.ProjectsSearched}");
        }
        else
        {
            md.AppendLine($"# 🔍 Search Results: \"{response.Query}\" in {response.ProjectName}");
            md.AppendLine();
        }

        md.AppendLine($"**Total Results:** {response.TotalResults}  ");
        md.AppendLine($"**Showing:** page {response.Page} of {response.TotalPages} ({response.Results.Count} results, {response.PageSize} per page)  ");
        md.AppendLine($"**Files Scanned:** {response.FilesScanned}  ");
        md.AppendLine($"**Search Duration:** {response.SearchDuration.TotalMilliseconds:F0}ms");

        if (response.Results.Count == 0)
        {
            md.AppendLine("*No results found.*");
            md.AppendLine();
            md.AppendLine("## 💡 Suggestions");
            md.AppendLine("- Check spelling");
            md.AppendLine("- Try broader search terms");
            md.AppendLine("- Search without case sensitivity");
            if (response.ProjectName != "*")
            {
                md.AppendLine("- Try searching across all projects with `projectName='*'`");
            }
            return md.ToString();
        }

        // ✨ ENHANCED: Group by project if wildcard search
        if (response.ProjectName == "*")
        {
            var projectGroups = response.Results
                .GroupBy(r => r.ProjectName ?? "Unknown")
                .OrderByDescending(g => g.Count());

            foreach (var projectGroup in projectGroups)
            {
                md.AppendLine($"## 📦 Project: {projectGroup.Key} ({projectGroup.Count()} results)");
                md.AppendLine();

                var typeGroups = projectGroup.GroupBy(r => r.MemberType);

                foreach (var typeGroup in typeGroups.OrderBy(g => g.Key))
                {
                    md.AppendLine($"### {GetMemberTypeIcon(typeGroup.Key)} {typeGroup.Key}s ({typeGroup.Count()})");
                    md.AppendLine();

                    foreach (var result in typeGroup)
                    {
                        FormatResultItem(md, result, isGrouped: true);
                    }
                }
            }
        }
        else
        {
            // Original single-project format
            var groupedResults = response.Results.GroupBy(r => r.MemberType);

            foreach (var group in groupedResults.OrderBy(g => g.Key))
            {
                md.AppendLine($"## {GetMemberTypeIcon(group.Key)} {group.Key}s ({group.Count()})");
                md.AppendLine();

                foreach (var result in group)
                {
                    FormatResultItem(md, result, isGrouped: false);
                }
            }
        }

        md.AppendLine("---");
        if (response.HasNextPage)
        {
            md.AppendLine($"⏭️ **More results available** — call again with `page={response.Page + 1}` to see the next {response.PageSize} results ({response.TotalResults - (response.Page * response.PageSize)} remaining).");
            md.AppendLine();
        }
        md.AppendLine("## 💡 Next Steps");
        md.AppendLine();

        var firstResult = response.Results.FirstOrDefault();
        if (firstResult != null)
        {
            var projectContext = firstResult.ProjectName ?? response.ProjectName;

            md.AppendLine($"**To Explore this file:**");
            md.AppendLine($"```");
            md.AppendLine($"analyze_c_sharp_file(\"{projectContext}\", \"{firstResult.FilePath}\")");
            md.AppendLine($"```");
            md.AppendLine();

            if (firstResult.MemberType == CodeMemberType.Method)
            {
                md.AppendLine($"**Get method implementation:**");
                md.AppendLine($"```");
                md.AppendLine($"fetch_method_implementation(\"{projectContext}\", \"{firstResult.FilePath}\", \"{firstResult.Name}\")");
                md.AppendLine($"```");
            }
        }

        return md.ToString();
    }

    private static void FormatResultItem(StringBuilder md, CodeSearchResult result, bool isGrouped)
    {
        // ✨ NEW: Special formatting for attributes
        if (result.MemberType == CodeMemberType.Attribute)
        {
            md.AppendLine($"- **{result.Name}**");
            md.AppendLine($"  - **Location:** `{result.FilePath}:{result.LineNumber}`");

            if (!string.IsNullOrEmpty(result.ParentClass))
            {
                if (!string.IsNullOrEmpty(result.ParentMember))
                {
                    md.AppendLine($"  - **Applied to:** `{result.ParentClass}.{result.ParentMember}` (method/property)");
                }
                else
                {
                    md.AppendLine($"  - **Applied to:** `{result.ParentClass}` (class)");
                }
            }

            if (!string.IsNullOrEmpty(result.Signature))
            {
                md.AppendLine($"  - **Full Syntax:** `{result.Signature}`");
            }

            md.AppendLine();
            return;
        }

        // Original formatting for other member types
        if (isGrouped)
        {
            md.AppendLine($"- **{result.Name}**");
            md.AppendLine($"  - **Location:** `{result.FilePath}:{result.LineNumber}`");

            if (!string.IsNullOrEmpty(result.ParentClass))
                md.AppendLine($"  - **Parent:** `{result.ParentClass}`");

            if (!string.IsNullOrEmpty(result.Signature))
                md.AppendLine($"  - **Signature:** `{result.Signature}`");

            if (!string.IsNullOrEmpty(result.TypeInfo))
                md.AppendLine($"  - **Type:** `{result.TypeInfo}`");

            if (result.Modifiers.Any())
                md.AppendLine($"  - **Modifiers:** `{string.Join(" ", result.Modifiers)}`");

            md.AppendLine();
        }
        else
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
            CodeMemberType.Attribute => "🏷️",  // ✨ NEW: Icon for attributes
            _ => "❓"
        };
    }
}
