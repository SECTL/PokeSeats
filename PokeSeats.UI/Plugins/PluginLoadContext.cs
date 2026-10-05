using System.Reflection;
using System.Runtime.Loader;

namespace PokeSeats.UI.Plugins;

/// <summary>
/// Load context for a single plugin. Private managed and native dependencies are resolved through
/// the plugin's own *.deps.json (<see cref="AssemblyDependencyResolver"/>), while assemblies owned
/// by the host — the plugin contracts, ImGui.NET and Silk.NET — deliberately fall through to
/// the default context so type identity and the
/// single ImGui native context are preserved.
/// </summary>
/// <remarks>
/// This is a dependency-resolution boundary, not a security sandbox, and the context is never
/// unloaded: installing, updating or removing a plugin requires an application restart.
/// </remarks>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    internal PluginLoadContext(string pluginId, string mainAssemblyPath)
        : base($"PokeSeats.Plugin:{pluginId}", isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simpleName = assemblyName.Name;
        if (simpleName is not null && IsOwnedByHost(simpleName))
            return null; // Let the default context resolve host-owned assemblies.

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
