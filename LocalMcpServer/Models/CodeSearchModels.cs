namespace MCP.Core.Models;

public enum CodeMemberType
{
    All,
    Class,
    Interface,
    Method,
    Property,
    Field,
    Enum,
    Struct,
    Attribute  // ✨ NEW: Support searching for attributes like [HttpPost], [Authorize], etc.
}

public class CodeSearchRequest
{
    public required string ProjectName { get; set; }
    public required string Query { get; set; }
    public CodeMemberType MemberType { get; set; } = CodeMemberType.All;
    public bool CaseSensitive { get; set; } = false;
    public int TopK { get; set; } = 20;
}

public class CodeSearchResponse
{
    public required string Query { get; set; }
    public required string ProjectName { get; set; }
    public int TotalResults { get; set; }
    public required List<CodeSearchResult> Results { get; set; }
    public TimeSpan SearchDuration { get; set; }
    public int FilesScanned { get; set; }
    public int ProjectsSearched { get; set; } = 1;
}

public class CodeSearchResult
{
    public required string Name { get; set; }
    public CodeMemberType MemberType { get; set; }
    public required string FilePath { get; set; }
    public int LineNumber { get; set; }
    public string? ParentClass { get; set; }
    public string? ParentMember { get; set; }  // ✨ NEW: For attributes - shows which method/class/property has this attribute
    public string? Signature { get; set; }
    public string? TypeInfo { get; set; }
    public required List<string> Modifiers { get; set; }
    public double RelevanceScore { get; set; }
    public string? ProjectName { get; set; }
}