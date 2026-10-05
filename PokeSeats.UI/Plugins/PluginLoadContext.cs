using System.Reflection;
using System.Runtime.Loader;

namespace PokeSeats.UI.Plugins;

/// <summary>
/// Load context for a single plugin. Declared dependency plugins share their entry assemblies;
/// other private managed and native dependencies resolve through the plugin's *.deps.json
/// (<see cref="AssemblyDependencyResolver"/>). Host-owned contracts, ImGui.NET and Silk.NET
/// fall through to the default context to preserve type identity and one ImGui native context.
/// </summary>
/// <remarks>
/// This is a dependency-resolution boundary, not a security sandbox, and the context is never
/// unloaded: installing, updating or removing a plugin requires an application restart.
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly IReadOnlyDictionary<string, Assembly> _dependencies;

    internal PluginLoadContext(string pluginId, string mainAssemblyPath, IReadOnlyDictionary<string, Assembly> dependencies)
        : base($"PokeSeats.Plugin:{pluginId}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _dependencies = dependencies;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (simpleName is not null && IsOwnedByHost(simpleName))
            return null; // Let the default context resolve host-owned assemblies.
        if (simpleName is not null && _dependencies.TryGetValue(simpleName, out Assembly? dependency))
            return dependency;

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }

    private static bool IsOwnedByHost(string simpleName)
    {
        if (simpleName.Equals("ImGui.NET", StringComparison.OrdinalIgnoreCase))
            return true;
        if (simpleName.Equals("Silk.NET", StringComparison.OrdinalIgnoreCase)
            || simpleName.StartsWith("Silk.NET.", StringComparison.OrdinalIgnoreCase))
            return true;
        return simpleName.Equals("PokeSeats.UI.PluginContracts", StringComparison.OrdinalIgnoreCase);
    }
}
