using Microsoft.Extensions.Logging;
using NuGetExplorer.Extensions;
using System.Text;

namespace NuGetExplorer.Services;

/// <summary>
/// Mirrors the human IntelliSense workflow for NuGet package exploration.
/// Progressive: type list → one type's methods → one type's properties.
/// Zero changes to existing NuGetPackageExplorer — purely additive.
/// Revert: delete this file.
/// </summary>
public class NuGetIntelliSenseExplorer
{
    // Properties the SDK injects for serialization — never user-facing.
    private static readonly HashSet<string> _suppressedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "Patch"
    };

    private readonly INuGetPackageLoader _loader;
    private readonly ILogger<NuGetIntelliSenseExplorer> _logger;

    public NuGetIntelliSenseExplorer(
        INuGetPackageLoader loader,
        ILogger<NuGetIntelliSenseExplorer> logger)
    {
        _loader = loader;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    // STEP 1 — "I typed 'using OpenAI.Chat' — show me what types exist"
    // Equivalent: IntelliSense dropdown when you start typing a type name.
    // Returns ONLY type names + kind. No members. Cheap: ~10 tokens per type.
    // -------------------------------------------------------------------------
    public async Task<string> GetNamespaceTypes(
        string packageId,
        string @namespace,
        string? version = null,
        string? targetFramework = null,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var ns = await ResolveNamespace(packageId, @namespace, version, targetFramework, includePrerelease, cancellationToken);

        var sb = new StringBuilder();
        sb.AppendLine($"# Types in {packageId} › {@namespace}");
        sb.AppendLine();

        var groups = ns.Types
            .Where(IsUsableType)
            .OrderBy(t => t.Kind)
            .ThenBy(t => t.TypeName)
            .GroupBy(t => t.Kind);

        foreach (var g in groups)
        {
            sb.AppendLine($"## {g.Key}s");
            foreach (var t in g)
            {
                var hint = BuildTypeHint(t);
                sb.AppendLine($"  {t.TypeName}{hint}");
            }
            sb.AppendLine();
        }

        sb.AppendLine($"---");
        sb.AppendLine($"Call get_type_surface(typeName) to explore methods on a specific type.");
        sb.AppendLine($"Call get_type_shape(typeName) to see properties of a result or options type.");

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // STEP 2a — "I picked ChatClient — show me what I can call on it"
    // Equivalent: client.| autocomplete — constructors + callable methods.
    // Suppresses: Streaming* types (handled separately), empty shells,
    //             SDK-internal properties, enum/struct option types.
    // -------------------------------------------------------------------------
    public async Task<string> GetTypeSurface(
        string packageId,
        string @namespace,
        string typeName,
        string? version = null,
        string? targetFramework = null,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var ns = await ResolveNamespace(packageId, @namespace, version, targetFramework, includePrerelease, cancellationToken);

        var type = ns.Types.FirstOrDefault(t =>
            string.Equals(t.TypeName, typeName, StringComparison.OrdinalIgnoreCase));

        if (type is null)
        {
            var available = ns.Types
                .Where(IsUsableType)
                .Select(t => t.TypeName);
            return $"Type '{typeName}' not found in '{@namespace}'.\n\nAvailable types:\n" +
                   string.Join("\n", available.Select(n => $"  {n}"));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {type.Kind} {type.TypeName}");
        sb.AppendLine($"**Package:** {packageId} › {@namespace}");
        sb.AppendLine();

        // Constructors — how do I create this
        if (type.Constructors.Count > 0 && type.Kind is TypeKind.Class or TypeKind.Struct)
        {
            sb.AppendLine("```csharp");
            foreach (var ctor in type.Constructors.OrderBy(c => c.Parameters.Count))
            {
                sb.AppendLine($"{ctor.Visibility.ToLower()} {type.TypeName}({FormatParams(ctor.Parameters)});");
            }
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // Methods — what can I call
        var methodGroups = type.Methods
            .GroupBy(m => m.Name)
            .OrderBy(g => g.Key)
            .ToList();

        if (methodGroups.Count > 0)
        {
            sb.AppendLine("## Methods");
            sb.AppendLine("```csharp");
            foreach (var g in methodGroups)
            {
                var rep = g.OrderBy(m => m.Parameters.Count).First();
                var staticMod = rep.IsStatic ? "static " : "";
                sb.AppendLine($"{rep.Visibility.ToLower()} {staticMod}{rep.ReturnType.SimplifyTypeName()} {rep.Name}({FormatParams(rep.Parameters)});");
                if (g.Count() > 1)
                    sb.AppendLine($"    // + {g.Count() - 1} overload{(g.Count() > 2 ? "s" : "")}");
            }
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("Call get_type_shape(typeName) to inspect properties of any return type or options type.");
        sb.AppendLine("Call get_method_overloads(typeName, methodName) to expand collapsed overloads.");

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // STEP 2b — "CompleteChat returns ChatCompletion — what's on it?"
    // Equivalent: Go To Definition on a return/options type — you want
    // to see its shape (properties), not call anything on it.
    // Suppresses: SDK-internal Patch property, write-only properties.
    // -------------------------------------------------------------------------
    public async Task<string> GetTypeShape(
        string packageId,
        string @namespace,
        string typeName,
        string? version = null,
        string? targetFramework = null,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        var ns = await ResolveNamespace(packageId, @namespace, version, targetFramework, includePrerelease, cancellationToken);

        var type = ns.Types.FirstOrDefault(t =>
            string.Equals(t.TypeName, typeName, StringComparison.OrdinalIgnoreCase));

        if (type is null)
        {
            var available = ns.Types
                .Where(IsUsableType)
                .Select(t => t.TypeName);
            return $"Type '{typeName}' not found in '{@namespace}'.\n\nAvailable types:\n" +
                   string.Join("\n", available.Select(n => $"  {n}"));
        }

        var sb = new StringBuilder();
        sb.AppendLine($"# {type.Kind} {type.TypeName} — Shape");
        sb.AppendLine($"**Package:** {packageId} › {@namespace}");
        sb.AppendLine();

        // Enum values — the only time enums are useful is when you need the values
        if (type.Kind == TypeKind.Enum)
        {
            sb.AppendLine("```csharp");
            sb.AppendLine($"enum {type.TypeName}");
            sb.AppendLine("{");
            foreach (var f in type.Fields.Where(f => f.IsStatic))
                sb.AppendLine($"    {f.Name},");
            sb.AppendLine("}");
            sb.AppendLine("```");
            return sb.ToString();
        }

        // Struct discriminated unions (e.g. ChatOutputAudioFormat) — show valid values
        if (type.Kind == TypeKind.Struct)
        {
            var staticProps = type.Properties.Where(p => p.IsStatic).ToList();
            if (staticProps.Count > 0)
            {
                sb.AppendLine("Valid values:");
                sb.AppendLine("```csharp");
                foreach (var p in staticProps)
                    sb.AppendLine($"{type.TypeName}.{p.Name}");
                sb.AppendLine("```");
                return sb.ToString();
            }
        }

        // Properties — what can I read or set
        var props = type.Properties
            .Where(p => !_suppressedProperties.Contains(p.Name) && p.CanRead)
            .OrderBy(p => p.Name)
            .ToList();

        if (props.Count == 0)
        {
            sb.AppendLine($"*No readable properties. Use get_type_surface('{typeName}') to see methods instead.*");
            return sb.ToString();
        }

        sb.AppendLine("```csharp");
        foreach (var p in props)
        {
            var accessors = p.CanWrite ? "{ get; set; }" : "{ get; }";
            var staticMod = p.IsStatic ? "static " : "";
            sb.AppendLine($"{p.Visibility.ToLower()} {staticMod}{p.PropertyType.SimplifyTypeName()} {p.Name} {accessors}");
        }
        sb.AppendLine("```");

        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------------

    private async Task<NamespaceMetadata> ResolveNamespace(
        string packageId,
        string @namespace,
        string? version,
        string? targetFramework,
        bool includePrerelease,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packageId))
            throw new ArgumentException("Package ID cannot be null or empty.", nameof(packageId));
        if (string.IsNullOrWhiteSpace(@namespace))
            throw new ArgumentException("Namespace cannot be null or empty.", nameof(@namespace));

        try
        {
            var metadata = await _loader.LoadPackageMetadata(
                packageId, version, targetFramework, includePrerelease, cancellationToken);

            return metadata.MetadataByNamespace.GetValueOrDefault(@namespace, new NamespaceMetadata());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load package metadata: {PackageId}@{Version} › {Namespace}",
                packageId, version ?? "latest", @namespace);
            throw;
        }
    }

    /// <summary>
    /// A type is "usable" if it has any API surface at all.
    /// Filters: empty shells, streaming event payloads (explored separately via GetTypeSurface).
    /// </summary>
    private static bool IsUsableType(TypeMetadata t)
    {
        // Empty shells — base/marker types with nothing callable
        if (t.Methods.Count == 0 && t.Properties.Count == 0 && t.Constructors.Count == 0)
            return false;

        return true;
    }

    /// <summary>
    /// One-line contextual hint shown in the type list so the caller
    /// can pick the right type without fetching full surface.
    /// </summary>
    private static string BuildTypeHint(TypeMetadata t)
    {
        if (t.Kind == TypeKind.Enum)
            return $" [{t.Fields.Count} values]"; // from static fields

        if (t.Kind == TypeKind.Struct)
        {
            var vals = t.Properties.Count(p => p.IsStatic);
            return vals > 0 ? $" [{vals} options]" : string.Empty;
        }

        var parts = new List<string>();
        if (t.Constructors.Count > 0) parts.Add($"{t.Constructors.Count} ctor{(t.Constructors.Count > 1 ? "s" : "")}");
        if (t.Methods.Count > 0)      parts.Add($"{t.Methods.Count} method{(t.Methods.Count > 1 ? "s" : "")}");
        if (t.Properties.Count > 0)   parts.Add($"{t.Properties.Count} prop{(t.Properties.Count > 1 ? "s" : "")}");
        return parts.Count > 0 ? $" [{string.Join(", ", parts)}]" : string.Empty;
    }

    private static string FormatParams(IEnumerable<ParameterInfo> parameters)
        => string.Join(", ", parameters.Select(p => $"{p.ParameterType.SimplifyTypeName()} {p.Name}"));
}
