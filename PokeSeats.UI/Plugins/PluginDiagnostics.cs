using System.Reflection;

namespace PokeSeats.UI.Plugins;

/// <summary>Formatting helpers shared by the plugin loader diagnostics.</summary>
internal static class PluginDiagnostics
{
    /// <summary>Formats an exception, unwrapping the reflection invocation wrapper.</summary>
    internal static string Describe(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException { InnerException: { } inner })
            current = inner;
        return $"{current.GetType().Name}: {current.Message}";
    }
}
