namespace NuGetExplorer.Services;

public interface IPackageMetadataCache
{
    bool TryGet(string key, out PackageMetadata? metadata);
    void Set(string key, PackageMetadata metadata);
    Task<PackageMetadata?> TryGetAsync(string key);
    Task SetAsync(string key, PackageMetadata metadata);
}

public class PackageMetadata
{
    public List<string> Namespaces { get; set; } = new();
    public Dictionary<string, NamespaceMetadata> MetadataByNamespace { get; set; } = new();
}

public class NamespaceMetadata
{
    public List<TypeMetadata> Types { get; set; } = new();
}

// Summary view - just distinct names
public class NamespaceSummary
{
    public List<string> Classes { get; set; } = new();
    public List<string> Interfaces { get; set; } = new();
    public List<string> Enums { get; set; } = new();
    public List<string> Structs { get; set; } = new();
    public List<string> Delegates { get; set; } = new();
}

public class TypeSummary
{
    public string TypeName { get; set; } = string.Empty;
    public TypeKind Kind { get; set; }
    public List<string> MethodNames { get; set; } = new();
    public List<string> PropertyNames { get; set; } = new();
    public List<string> FieldNames { get; set; } = new();
    public List<string> EventNames { get; set; } = new();
    public int ConstructorCount { get; set; }
}

public class TypeMetadata
{
    public string Namespace { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public TypeKind Kind { get; set; }
    public List<MethodSignature> Methods { get; set; } = new();
    public List<PropertySignature> Properties { get; set; } = new();
    public List<FieldSignature> Fields { get; set; } = new();
    public List<EventSignature> Events { get; set; } = new();
    public List<ConstructorSignature> Constructors { get; set; } = new();
}

public enum TypeKind
{
    Class,
    Interface,
    Enum,
    Struct,
    Delegate
}

public class MethodSignature
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public List<ParameterInfo> Parameters { get; set; } = new();
    public string Visibility { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
}

public class PropertySignature
{
    public string Name { get; set; } = string.Empty;
    public string PropertyType { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool IsStatic { get; set; }
}

public class FieldSignature
{
    public string Name { get; set; } = string.Empty;
    public string FieldType { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
    public bool IsReadOnly { get; set; }
}

public class EventSignature
{
    public string Name { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool IsStatic { get; set; }
}

public class ConstructorSignature
{
    public List<ParameterInfo> Parameters { get; set; } = new();
    public string Visibility { get; set; } = string.Empty;
}

public class ParameterInfo
{
    public string Name { get; set; } = string.Empty;
    public string ParameterType { get; set; } = string.Empty;
}
