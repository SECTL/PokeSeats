using System.Numerics;

namespace PokeSeats.UI.PluginContracts;

/// <summary>The in-process UI contract version understood by this assembly.</summary>
public static class PluginApi
{
    public const int Version = 2;
}

/// <summary>One entry point per plugin manifest. All callbacks run on the UI thread.</summary>
public interface IUiPlugin : IDisposable
{
    void Initialize(IPluginContext context);
}

/// <summary>Registrations belong to this plugin and are committed only after initialization succeeds.</summary>
public interface IPluginContext
{
    string PluginId { get; }
    string DataDirectory { get; }
    /// <summary>Factory creates a fresh instance. The host retains and disposes it.</summary>
    void RegisterWorkspace(string id, string displayName, Func<IWorkspace> create);
    void RegisterView(IUiView view);
    void RegisterCommand(IUiCommand command);
}

/// <summary>Owns the entire application content area, including layout, window chrome, and navigation.</summary>
public interface IWorkspace : IDisposable
{
    void Activate(IWorkspaceContext context);
    void Draw(float deltaSeconds);
    void Deactivate();
}

/// <summary>Contributions from all successfully initialized plugins, in stable ID order.</summary>
public interface IWorkspaceContext
{
    IReadOnlyList<IUiView> Views { get; }
    IReadOnlyList<IUiCommand> Commands { get; }
}

/// <summary>
/// A business view draws only content. The workspace owns Begin/End, position, size, flags, and decoration.
/// </summary>
public interface IUiView
{
    /// <summary>Stable for the lifetime of this registration.</summary>
    string Id { get; }
    string DisplayName { get; }

    /// <summary>
    /// Minimum content window size. Null means the view supports responsive layout and can use any available space.
    /// </summary>
    Vector2? MinimumSize => null;

    /// <summary>Draw content between the workspace-owned Begin and End calls.</summary>
    void DrawContent(float deltaSeconds);
}

public interface IUiCommand
{
    /// <summary>Stable for the lifetime of this registration.</summary>
    string Id { get; }
    string DisplayName { get; }
    void Execute();
}
