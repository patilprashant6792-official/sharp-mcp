using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace NuGetExplorer.Services;

public class NuGetPackageLoader : INuGetPackageLoader
{
    private readonly IPackageMetadataCache _cache;
    private readonly INuGetXmlDocCache _xmlDocCache;
    private readonly ILogger<NuGetPackageLoader> _logger;
    private readonly string _packageCacheDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();
    private readonly SourceRepository _repository;
    private readonly SourceCacheContext _sourceCacheContext;

    private static readonly HashSet<string> ExcludedMethods = new()
    {
        "GetHashCode", "ToString", "Equals", "GetType", "Finalize", "MemberwiseClone"
    };

    private static readonly string[] FrameworkFallbackOrder = new[]
    {
        "net10.0", "net9.0", "net8.0", "net6.0",
        "netstandard2.1", "netstandard2.0", "netstandard1.6"
    };

    public NuGetPackageLoader(
        IPackageMetadataCache cache,
        INuGetXmlDocCache xmlDocCache,
        ILogger<NuGetPackageLoader> logger,
        string? packageCacheDirectory = null)
    {
        _cache       = cache       ?? throw new ArgumentNullException(nameof(cache));
        _xmlDocCache = xmlDocCache ?? throw new ArgumentNullException(nameof(xmlDocCache));
        _logger      = logger      ?? throw new ArgumentNullException(nameof(logger));

        _packageCacheDirectory = packageCacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages");

        try
        {
            Directory.CreateDirectory(_packageCacheDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create package cache directory: {Directory}", _packageCacheDirectory);
            throw;
        }

        var packageSource = new PackageSource("https://api.nuget.org/v3/index.json");
        _repository = Repository.Factory.GetCoreV3(packageSource);
        _sourceCacheContext = new SourceCacheContext();
    }

    public async Task<PackageMetadata> LoadPackageMetadata(
        string packageId,
        string? version = null,
        string? targetFramework = null,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new ArgumentException("Package ID cannot be null or empty", nameof(packageId));
        }

        var cacheKey = BuildCacheKey(packageId, version, targetFramework);

        var metadata = await _cache.TryGetAsync(cacheKey);
        if (metadata != null)
        {
            return metadata;
        }

        var semaphore = GetOrCreateSemaphore(cacheKey);

        try
        {
            await semaphore.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Operation cancelled while waiting for semaphore for package: {PackageId}", packageId);
            throw;
        }

        try
        {
            metadata = await _cache.TryGetAsync(cacheKey);
            if (metadata != null)
            {
                return metadata;
            }

            metadata = await LoadAndCache(packageId, version, targetFramework, includePrerelease, cancellationToken);
            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load package metadata: {PackageId}@{Version}", packageId, version ?? "latest");
            throw;
        }
        finally
        {
            semaphore.Release();

            // Evict the semaphore once the cache entry exists — it will never be needed again
            // for this key. TryRemove is atomic; a racing GetOrAdd will create a fresh one.
            if (await _cache.TryGetAsync(cacheKey) != null)
            {
                if (_semaphores.TryRemove(cacheKey, out var evicted))
                    evicted.Dispose();
            }
        }
    }

    private async Task<PackageMetadata> LoadAndCache(
        string packageId,
        string? version,
        string? targetFramework,
        bool includePrerelease,
        CancellationToken cancellationToken)
    {
        NuGetVersion resolvedVersion;

        try
        {
            resolvedVersion = await ResolveVersion(packageId, version, includePrerelease, cancellationToken);
            _logger.LogInformation("Resolved package version: {PackageId}@{Version}", packageId, resolvedVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to resolve version for package: {PackageId}", packageId);
            throw new InvalidOperationException($"Failed to resolve version for package '{packageId}'", ex);
        }

        string packagePath;

        try
        {
            packagePath = await DownloadPackage(packageId, resolvedVersion, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download package: {PackageId}@{Version}", packageId, resolvedVersion);
            throw new InvalidOperationException($"Failed to download package '{packageId}@{resolvedVersion}'", ex);
        }

        var framework = targetFramework ?? "net10.0";

        List<string> dependencyPaths;
        try
        {
            dependencyPaths = await DownloadDependencies(packagePath, resolvedVersion, framework, cancellationToken);
            _logger.LogInformation("Downloaded {Count} dependency packages for {PackageId}@{Version}",
                dependencyPaths.Count, packageId, resolvedVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download dependencies for package: {PackageId}@{Version}",
                packageId, resolvedVersion);
            dependencyPaths = new List<string>();
        }

        List<string> assemblies;
        try
        {
            assemblies = ExtractAssemblies(packagePath, framework);
            _logger.LogInformation("Found {Count} assemblies for {PackageId}@{Version}",
                assemblies.Count, packageId, resolvedVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract assemblies from package: {PackageId}@{Version}",
                packageId, resolvedVersion);
            throw new InvalidOperationException($"Failed to extract assemblies from package '{packageId}@{resolvedVersion}'", ex);
        }

        var allDependencyAssemblies = new List<string>();
        foreach (var depPath in dependencyPaths)
        {
            try
            {
                var depAssemblies = ExtractAssemblies(depPath, framework);
                allDependencyAssemblies.AddRange(depAssemblies);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract assemblies from dependency: {DependencyPath}", depPath);
            }
        }

        _logger.LogInformation("Loaded {Count} dependency assemblies for resolution", allDependencyAssemblies.Count);

        var metadata = new PackageMetadata();

        foreach (var assemblyPath in assemblies)
        {
            try
            {
                var assemblyMetadata = ExtractMetadata(assemblyPath, allDependencyAssemblies);

                foreach (var ns in assemblyMetadata.Keys)
                {
                    if (!metadata.Namespaces.Contains(ns))
                    {
                        metadata.Namespaces.Add(ns);
                    }

                    if (!metadata.MetadataByNamespace.ContainsKey(ns))
                    {
                        metadata.MetadataByNamespace[ns] = new NamespaceMetadata();
                    }

                    metadata.MetadataByNamespace[ns].Types.AddRange(assemblyMetadata[ns].Types);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract metadata from assembly: {AssemblyPath}", assemblyPath);
            }
        }

        // Parse XML docs from the extracted package directory and cache them
        // before CleanupPackage() deletes the files — zero extra network cost.
        try
        {
            var docMap = _xmlDocCache.ParseFromPackagePath(packagePath);
            if (docMap.Count > 0)
            {
                // Always store under the resolved version (e.g. "2.2.0")
                _xmlDocCache.Set(packageId, resolvedVersion.ToString(), docMap);
                // Also store under "latest" alias when caller did not pin a version,
                // so the tool can find it with version=null → probes "latest".
                if (string.IsNullOrEmpty(version))
                    _xmlDocCache.Set(packageId, "latest", docMap);
            }
            else
                _logger.LogDebug("No XML doc file found in package: {PackageId}@{Version}", packageId, resolvedVersion);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse/cache XML docs for {PackageId}@{Version}", packageId, resolvedVersion);
        }

        var cacheKey = BuildCacheKey(packageId, resolvedVersion.ToString(), targetFramework);
        await _cache.SetAsync(cacheKey, metadata);

        // When the caller did not pin a version, also store under the "latest" key
        // (e.g. "Tomlyn@latest@net10.0") so the next version=null call hits the cache
        // instead of triggering a full re-download + reflection cycle every time.
        if (string.IsNullOrEmpty(version))
        {
            var latestKey = BuildCacheKey(packageId, null, targetFramework);
            await _cache.SetAsync(latestKey, metadata);
            _logger.LogDebug("Stored metadata under \"latest\" alias: {Key}", latestKey);
        }

        // Clean up downloaded packages after metadata is cached
        try
        {
            CleanupPackage(packagePath, packageId, resolvedVersion);

            foreach (var depPath in dependencyPaths)
            {
                var depInfo = new DirectoryInfo(depPath);
                var depId = depInfo.Parent?.Name ?? "unknown";
                var depVersion = depInfo.Name;
                CleanupPackage(depPath, depId, new NuGetVersion(depVersion));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup packages after caching metadata for: {PackageId}@{Version}",
                packageId, resolvedVersion);
        }

        return metadata;
    }

    private void CleanupPackage(string packagePath, string packageId, NuGetVersion version)
    {
        if (!Directory.Exists(packagePath))
        {
            return;
        }

        try
        {
            Directory.Delete(packagePath, recursive: true);
            _logger.LogDebug("Deleted package directory after caching: {PackageId}@{Version}", packageId, version);

            // Clean up parent directory if empty
            var parentDir = Directory.GetParent(packagePath);
            if (parentDir != null && parentDir.Exists && !parentDir.EnumerateFileSystemInfos().Any())
            {
                parentDir.Delete();
                _logger.LogDebug("Deleted empty parent directory: {Directory}", parentDir.FullName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete package directory: {PackagePath}", packagePath);
        }
    }

    // Reads the .nuspec that DownloadPackage already extracted to disk — zero extra network cost.
    private async Task<List<string>> DownloadDependencies(
        string packagePath,
        NuGetVersion version,
        string targetFramework,
        CancellationToken cancellationToken)
    {
        var dependencyPaths = new List<string>();

        try
        {
            // The .nuspec is always extracted alongside the .dll files by DownloadPackage.
            var nuspecFile = Directory.GetFiles(packagePath, "*.nuspec", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (nuspecFile == null)
            {
                _logger.LogWarning("No .nuspec found in extracted package path: {PackagePath}", packagePath);
                return dependencyPaths;
            }

            NuspecReader nuspecReader;
            await using (var nuspecStream = File.OpenRead(nuspecFile))
            {
                nuspecReader = new NuspecReader(nuspecStream);
            }

            NuGetFramework nugetFramework;
            try
            {
                nugetFramework = NuGetFramework.Parse(targetFramework);
            }
            catch
            {
                nugetFramework = NuGetFramework.Parse("net10.0");
            }

            var dependencyGroups = nuspecReader.GetDependencyGroups().ToList();

            var reducer = new FrameworkReducer();
            var nearest = reducer.GetNearest(nugetFramework, dependencyGroups.Select(g => g.TargetFramework));

            var matchingGroup = dependencyGroups.FirstOrDefault(g => g.TargetFramework == nearest);

            if (matchingGroup == null)
            {
                _logger.LogDebug("No matching dependency group found for framework: {Framework}", targetFramework);
                return dependencyPaths;
            }

            foreach (var dependency in matchingGroup.Packages)
            {
                try
                {
                    var depVersion = await ResolveVersion(dependency.Id, dependency.VersionRange?.MinVersion?.ToString(), false, cancellationToken);
                    var depPath = await DownloadPackage(dependency.Id, depVersion, cancellationToken);
                    dependencyPaths.Add(depPath);

                    _logger.LogDebug("Downloaded dependency: {DependencyId}@{Version}", dependency.Id, depVersion);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download dependency: {DependencyId}", dependency.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze dependencies from package path: {PackagePath}", packagePath);
        }

        return dependencyPaths;
    }

    private Dictionary<string, NamespaceMetadata> ExtractMetadata(string assemblyPath, List<string> dependencyAssemblies)
    {
        var result = new Dictionary<string, NamespaceMetadata>();

        if (!File.Exists(assemblyPath))
        {
            _logger.LogWarning("Assembly file not found: {AssemblyPath}", assemblyPath);
            return result;
        }

        var runtimeAssemblies = Directory.GetFiles(
            RuntimeEnvironment.GetRuntimeDirectory(),
            "*.dll");

        var resolverPaths = new List<string>(runtimeAssemblies);
        resolverPaths.Add(assemblyPath);
        resolverPaths.AddRange(dependencyAssemblies);

        resolverPaths = resolverPaths.Distinct().ToList();

        var resolver = new PathAssemblyResolver(resolverPaths);

        MetadataLoadContext? mlc = null;

        try
        {
            mlc = new MetadataLoadContext(resolver);
            var assembly = mlc.LoadFromAssemblyPath(assemblyPath);

            foreach (var type in assembly.GetTypes())
            {
                if (!type.IsPublic || string.IsNullOrEmpty(type.Namespace))
                {
                    continue;
                }

                var ns = type.Namespace;

                if (!result.ContainsKey(ns))
                {
                    result[ns] = new NamespaceMetadata();
                }

                var typeMetadata = new TypeMetadata
                {
                    Namespace = ns,
                    TypeName = type.Name,
                    Kind = GetTypeKind(type)
                };

                try
                {
                    typeMetadata.Methods = type.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Where(m => !ExcludedMethods.Contains(m.Name) && !m.IsSpecialName)
                        .Select(m => new MethodSignature
                        {
                            Name = m.Name,
                            ReturnType = m.ReturnType.FullName ?? m.ReturnType.Name,
                            Visibility = GetVisibility(m),
                            IsStatic = m.IsStatic,
                            Parameters = m.GetParameters().Select(p => new ParameterInfo
                            {
                                Name = p.Name ?? string.Empty,
                                ParameterType = p.ParameterType.FullName ?? p.ParameterType.Name
                            }).ToList()
                        }).ToList();

                    typeMetadata.Properties = type.GetProperties(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Select(p => new PropertySignature
                        {
                            Name = p.Name,
                            PropertyType = p.PropertyType.FullName ?? p.PropertyType.Name,
                            Visibility = GetVisibility(p.GetMethod ?? p.SetMethod),
                            CanRead = p.CanRead,
                            CanWrite = p.CanWrite,
                            IsStatic = (p.GetMethod?.IsStatic ?? p.SetMethod?.IsStatic) ?? false
                        }).ToList();

                    typeMetadata.Fields = type.GetFields(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Select(f => new FieldSignature
                        {
                            Name = f.Name,
                            FieldType = f.FieldType.FullName ?? f.FieldType.Name,
                            Visibility = GetFieldVisibility(f),
                            IsStatic = f.IsStatic,
                            IsReadOnly = f.IsInitOnly
                        }).ToList();

                    typeMetadata.Events = type.GetEvents(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                        .Select(e => new EventSignature
                        {
                            Name = e.Name,
                            EventType = e.EventHandlerType?.FullName ?? e.EventHandlerType?.Name ?? string.Empty,
                            Visibility = GetVisibility(e.AddMethod ?? e.RemoveMethod),
                            IsStatic = (e.AddMethod?.IsStatic ?? e.RemoveMethod?.IsStatic) ?? false
                        }).ToList();

                    typeMetadata.Constructors = type.GetConstructors(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                        .Select(c => new ConstructorSignature
                        {
                            Visibility = GetVisibility(c),
                            Parameters = c.GetParameters().Select(p => new ParameterInfo
                            {
                                Name = p.Name ?? string.Empty,
                                ParameterType = p.ParameterType.FullName ?? p.ParameterType.Name
                            }).ToList()
                        }).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to extract members for type: {TypeName}", type.FullName);
                }

                result[ns].Types.Add(typeMetadata);
            }
        }
        catch (FileNotFoundException ex)
        {
            _logger.LogError(ex, "Missing dependency while loading assembly: {AssemblyPath}", assemblyPath);
            throw new InvalidOperationException(
                $"Failed to load assembly '{assemblyPath}'. Missing dependency: {ex.FileName}", ex);
        }
        catch (BadImageFormatException ex)
        {
            _logger.LogError(ex, "Invalid assembly format: {AssemblyPath}", assemblyPath);
            throw new InvalidOperationException($"Invalid assembly format: {assemblyPath}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to extract metadata from assembly: {AssemblyPath}", assemblyPath);
            throw;
        }
        finally
        {
            mlc?.Dispose();
        }

        return result;
    }

    private async Task<NuGetVersion> ResolveVersion(
        string packageId,
        string? version,
        bool includePrerelease,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(version))
        {
            try
            {
                return new NuGetVersion(version);
            }
            catch (Exception ex)
            {
                throw new ArgumentException($"Invalid version format: {version}", nameof(version), ex);
            }
        }

        var resource = await _repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        IEnumerable<NuGetVersion> versions;

        try
        {
            versions = await resource.GetAllVersionsAsync(
                packageId,
                _sourceCacheContext,
                NullLogger.Instance,
                linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout while fetching versions for package '{packageId}'");
        }

        var latestVersion = versions
            .Where(v => includePrerelease || !v.IsPrerelease)
            .OrderByDescending(v => v)
            .FirstOrDefault();

        if (latestVersion == null)
        {
            throw new InvalidOperationException(
                $"No versions found for package '{packageId}'. " +
                $"Include prerelease: {includePrerelease}");
        }

        return latestVersion;
    }

    private async Task<string> DownloadPackage(
        string packageId,
        NuGetVersion version,
        CancellationToken cancellationToken)
    {
        var packagePath = Path.Combine(_packageCacheDirectory, packageId, version.ToString());

        // Check if package already exists with assemblies
        if (Directory.Exists(packagePath))
        {
            var existingDlls = Directory.GetFiles(packagePath, "*.dll", SearchOption.AllDirectories);
            if (existingDlls.Any())
            {
                _logger.LogDebug("Using cached package: {PackageId}@{Version}", packageId, version);
                return packagePath;
            }
        }

        try
        {
            Directory.CreateDirectory(packagePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create directory: {packagePath}", ex);
        }

        var resource = await _repository.GetResourceAsync<FindPackageByIdResource>(cancellationToken);

        using var packageStream = new MemoryStream();

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var success = await resource.CopyNupkgToStreamAsync(
                packageId,
                version,
                packageStream,
                _sourceCacheContext,
                NullLogger.Instance,
                linkedCts.Token);

            if (!success)
            {
                throw new InvalidOperationException($"Failed to download package '{packageId}@{version}'");
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"Timeout while downloading package '{packageId}@{version}'");
        }

        packageStream.Seek(0, SeekOrigin.Begin);

        using var packageReader = new PackageArchiveReader(packageStream);
        var files = packageReader.GetFiles();

        foreach (var file in files)
        {
            try
            {
                var targetPath = Path.Combine(packagePath, file);
                var targetDir = Path.GetDirectoryName(targetPath);

                if (!string.IsNullOrEmpty(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                using var fileStream = packageReader.GetStream(file);
                using var targetFileStream = File.Create(targetPath);
                await fileStream.CopyToAsync(targetFileStream, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract file: {File} from package: {PackageId}@{Version}",
                    file, packageId, version);
            }
        }

        return packagePath;
    }

    private List<string> ExtractAssemblies(string packagePath, string targetFramework)
    {
        var frameworks = FrameworkFallbackOrder;

        if (!string.IsNullOrEmpty(targetFramework) && !frameworks.Contains(targetFramework))
        {
            frameworks = new[] { targetFramework }.Concat(frameworks).ToArray();
        }

        var libPath = Path.Combine(packagePath, "lib");

        if (!Directory.Exists(libPath))
        {
            _logger.LogWarning("No lib folder found in package: {PackagePath}", packagePath);
            return new List<string>();
        }

        foreach (var framework in frameworks)
        {
            var frameworkPath = Path.Combine(libPath, framework);

            if (Directory.Exists(frameworkPath))
            {
                var dlls = Directory.GetFiles(frameworkPath, "*.dll", SearchOption.TopDirectoryOnly).ToList();

                if (dlls.Any())
                {
                    _logger.LogDebug("Using framework: {Framework} with {Count} assemblies", framework, dlls.Count);
                    return dlls;
                }
            }
        }

        _logger.LogDebug("No matching framework found, using all DLLs from lib folder");
        var allDlls = Directory.GetFiles(libPath, "*.dll", SearchOption.AllDirectories).ToList();
        return allDlls;
    }

    private TypeKind GetTypeKind(Type type)
    {
        if (type.IsEnum) return TypeKind.Enum;
        if (type.IsInterface) return TypeKind.Interface;
        if (type.IsValueType) return TypeKind.Struct;
        if (type.BaseType?.FullName == "System.MulticastDelegate") return TypeKind.Delegate;
        return TypeKind.Class;
    }

    private string GetVisibility(MethodBase? method)
    {
        if (method == null) return "private";
        if (method.IsPublic) return "public";
        if (method.IsFamily) return "protected";
        if (method.IsAssembly) return "internal";
        if (method.IsFamilyOrAssembly) return "protected internal";
        return "private";
    }

    private string GetFieldVisibility(FieldInfo field)
    {
        if (field.IsPublic) return "public";
        if (field.IsFamily) return "protected";
        if (field.IsAssembly) return "internal";
        if (field.IsFamilyOrAssembly) return "protected internal";
        return "private";
    }

    private SemaphoreSlim GetOrCreateSemaphore(string key)
    {
        return _semaphores.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
    }

    private string BuildCacheKey(string packageId, string? version, string? targetFramework)
    {
        var normalizedVersion = string.IsNullOrEmpty(version) ? "latest" : version;
        var normalizedFramework = string.IsNullOrEmpty(targetFramework) ? "net10.0" : targetFramework;
        return $"{packageId}@{normalizedVersion}@{normalizedFramework}";
    }

    public void Dispose()
    {
        _sourceCacheContext?.Dispose();

        foreach (var semaphore in _semaphores.Values)
        {
            semaphore?.Dispose();
        }

        _semaphores.Clear();
    }
}
