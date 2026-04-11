using MCP.Core.Services;
using ModelContextProtocol.Server;
using NuGetExplorer.Services;
using System.ComponentModel;

namespace RisingTideAI.Trade.MCP.Host.MCPServers;

/// <summary>
/// Progressive NuGet exploration mirroring the human IntelliSense workflow.
///
/// WORKFLOW (mirrors what a developer does in an IDE):
///
///   Step 1 — get_package_namespaces
///             "I just installed OpenAI — what namespaces does it expose?"
///             Returns flat namespace list. Always call this first on an unfamiliar package.
///             Feeds directly into get_namespace_types.
///
///   Step 2 — get_namespace_types
///             "I added using OpenAI.Chat — what types exist?"
///             Returns type names + kind only. ~10 tokens per type.
///             Equivalent to the IntelliSense dropdown when typing a class name.
///
///   Step 3a — get_type_surface
///             "I want to use ChatClient — what can I call on it?"
///             Returns constructors + methods of ONE type only.
///             Equivalent to client.| autocomplete.
///
///   Step 3b — get_type_shape
///             "CompleteChat returned ChatCompletion — what's on it?"
///             Returns properties only of ONE type.
///             Equivalent to F12 (Go To Definition) on a return or options type.
///
///   Step 4  — get_method_overloads (existing tool)
///             "CompleteChat shows '+ 2 overloads' — show all signatures."
///
/// WHAT IS SUPPRESSED vs get_namespace_summary:
///   - Empty shell types (0 methods, 0 properties, 0 constructors)
///   - SDK-internal properties (e.g. JsonPatch Patch)
///   - Enums/structs rendered as compact option lists only when explicitly fetched
///   - All members of types you haven't asked about yet
/// </summary>
[McpServerToolType]
public class NuGetIntelliSenseTools
{
    private readonly NuGetIntelliSenseExplorer _explorer;
    private readonly INuGetPackageExplorer _packageExplorer;
    private readonly ITomlSerializerService _tomlSerializer;
    private readonly INuGetSearchService _nugetService;

    public NuGetIntelliSenseTools(
        NuGetIntelliSenseExplorer explorer,
        INuGetPackageExplorer packageExplorer,
        ITomlSerializerService tomlSerializer,
        INuGetSearchService nugetService)
    {
        _explorer = explorer;
        _packageExplorer = packageExplorer;
        _tomlSerializer = tomlSerializer;
        _nugetService = nugetService;
    }

    [McpServerTool]
    [Description(
        "PREFERRED FIRST STEP for NuGet exploration. " +
        "Returns all type names + kind (Class/Interface/Enum/Struct) in a namespace. " +
        "NO member details — just the list so you can pick what to explore next. " +
        "Each type shows a hint: [2 ctors, 14 methods] or [5 options] so you know what it is before drilling in. " +
        "Cost: ~10 tokens per type vs 100+ tokens in get_namespace_summary. " +
        "NEXT: call get_type_surface(typeName) for a client/service type you want to call, " +
        "or get_type_shape(typeName) for a result/options type you want to read.")]
    public async Task<string> GetNamespaceTypes(
        [Description("NuGet package ID (e.g. 'OpenAI', 'Serilog')")]
        string packageId,
        [Description("Namespace to list (from get_nu_get_package_namespaces)")]
        string @namespace,
        [Description("Optional: specific version. Omit for latest stable.")]
        string? version = null,
        [Description("Optional: target framework (default net10.0)")]
        string? targetFramework = null,
        [Description("Optional: include prerelease (default false)")]
        bool includePrerelease = false)
    {
        try
        {
            return await _explorer.GetNamespaceTypes(
                packageId, @namespace, version, targetFramework, includePrerelease);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to list namespace types: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description(
        "Returns constructors + methods of ONE specific type. " +
        "Use this for client, service, or builder types — things you instantiate and call methods on. " +
        "Equivalent to typing 'client.' in the IDE and seeing the method autocomplete list. " +
        "Suppressed automatically: empty base types, SDK-internal properties. " +
        "Methods are collapsed: one representative signature shown, '+ N overloads' if more exist. " +
        "NEXT: call get_method_overloads(typeName, methodName) to expand any collapsed overload set, " +
        "or get_type_shape(returnTypeName) to inspect what a method returns.")]
    public async Task<string> GetTypeSurface(
        [Description("NuGet package ID")]
        string packageId,
        [Description("Namespace containing the type")]
        string @namespace,
        [Description("Type name to inspect (e.g. 'ChatClient', 'ResponsesClient')")]
        string typeName,
        [Description("Optional: specific version")]
        string? version = null,
        [Description("Optional: target framework")]
        string? targetFramework = null,
        [Description("Optional: include prerelease")]
        bool includePrerelease = false)
    {
        try
        {
            return await _explorer.GetTypeSurface(
                packageId, @namespace, typeName, version, targetFramework, includePrerelease);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to get type surface: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description(
        "Returns properties only of ONE specific type — its readable/writable shape. " +
        "Use this for result types (what did the call return?) and options/config types (what can I set?). " +
        "Equivalent to pressing F12 (Go To Definition) on a return type or parameter type in the IDE. " +
        "Enums show their values. Structs show their valid named options. " +
        "SDK-internal properties (e.g. Patch) are suppressed automatically. " +
        "EXAMPLES: get_type_shape('ChatCompletion') to read response fields, " +
        "get_type_shape('ChatCompletionOptions') to know what you can configure.")]
    public async Task<string> GetTypeShape(
        [Description("NuGet package ID")]
        string packageId,
        [Description("Namespace containing the type")]
        string @namespace,
        [Description("Type name to inspect (e.g. 'ChatCompletion', 'CreateResponseOptions')")]
        string typeName,
        [Description("Optional: specific version")]
        string? version = null,
        [Description("Optional: target framework")]
        string? targetFramework = null,
        [Description("Optional: include prerelease")]
        bool includePrerelease = false)
    {
        try
        {
            return await _explorer.GetTypeShape(
                packageId, @namespace, typeName, version, targetFramework, includePrerelease);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to get type shape: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description(
        "STEP 1 of the IntelliSense workflow: lists all namespaces exposed by a NuGet package. " +
        "Call this when you don't know what namespaces a package contains. " +
        "Returns a flat list — e.g. 'OpenAI' → ['OpenAI', 'OpenAI.Chat', 'OpenAI.Responses', ...]. " +
        "NEXT: pick the relevant namespace and call get_namespace_types to see what types it contains.")]
    public async Task<string> GetPackageNamespaces(
        [Description("NuGet package ID (e.g. 'OpenAI', 'Serilog')")]
        string packageId,
        [Description("Optional: specific version. Omit for latest stable.")]
        string? version = null,
        [Description("Optional: target framework (default net10.0)")]
        string? targetFramework = null,
        [Description("Optional: include prerelease (default false)")]
        bool includePrerelease = false)
    {
        try
        {
            var namespaces = await _packageExplorer.GetNamespaces(
                packageId, version, targetFramework, includePrerelease);
            return _tomlSerializer.Serialize(new { Namespaces = namespaces });
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to retrieve namespaces: {ex.Message}", ex);
        }
    }

    [McpServerTool]
    [Description(
        "Search NuGet.org for packages by name or keywords. " +
        "Use ONLY when you don't know the exact package ID. " +
        "CRITICAL: use the EXACT package ID for best results — e.g. 'Newtonsoft.Json' not 'Json'. " +
        "NEXT: once you have the package ID, call get_package_namespaces.")]
    public async Task<string> SearchNuGetPackages(
        [Description("Package name or keywords (e.g. 'Serilog', 'json serializer')")]
        string query,
        [Description("Optional: max results (default 20)")]
        int take = 20,
        [Description("Optional: include prerelease (default false)")]
        bool includePrerelease = false)
    {
        var results = await _nugetService.SearchPackagesAsync(query, take, includePrerelease);
        return _tomlSerializer.Serialize(results);
    }

    [McpServerTool]
    [Description(
        "Expands collapsed overloads for a specific method on a specific type. " +
        "Use ONLY when get_type_surface or get_namespace_summary shows '+ N overloads' and you need all parameter variations. " +
        "Returns full signatures for every overload of the named method.")]
    public async Task<string> GetMethodOverloads(
        [Description("NuGet package ID")]
        string packageId,
        [Description("Namespace containing the type")]
        string @namespace,
        [Description("Type name (e.g. 'ChatClient', 'Log')")]
        string typeName,
        [Description("Method name to expand (e.g. 'CompleteChat', 'Write')")]
        string methodName,
        [Description("Optional: specific version")]
        string? version = null,
        [Description("Optional: target framework")]
        string? targetFramework = null,
        [Description("Optional: include prerelease")]
        bool includePrerelease = false)
    {
        try
        {
            var metadata = await _packageExplorer.GetMethodOverloads(
                packageId, @namespace, typeName, methodName,
                version, targetFramework, includePrerelease);
            return metadata;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Failed to retrieve method overloads: {ex.Message}", ex);
        }
    }
}


