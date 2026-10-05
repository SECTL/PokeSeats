using ImGuiNET;

namespace PokeSeats.UI.PluginContracts;

/// <summary>The in-process UI contract version understood by this assembly.</summary>
public static class PluginApi
{
    public const int Version = 1;
}

/// <summary>One entry point per plugin manifest. All callbacks run on the UI thread.</summary>
public interface IUiPlugin : IDisposable
{
    void Initialize(IPluginContext context);
}

/// <summary>Registrations belong to this plugin and are removed if initialization fails.</summary>
public interface IPluginContext
{
    string PluginId { get; }
    string DataDirectory { get; }
    /// <summary>Factory creates a fresh instance. The host retains and disposes it.</summary>
    void RegisterWorkspace(string id, string displayName, Func<IWorkspace> create);
    void RegisterPanel(IUiPanel panel);
    void RegisterCommand(IUiCommand command);
}

/// <summary>Owns the entire application content area, including docking and navigation.</summary>
public interface IWorkspace : IDisposable
{
    void Activate(IWorkspaceContext context);
    void Draw(float deltaSeconds);
    void Deactivate();
}

/// <summary>Contributions from all successfully initialized plugins, in stable ID order.</summary>
public interface IWorkspaceContext
{
    IReadOnlyList<IUiPanel> Panels { get; }
    IReadOnlyList<IUiCommand> Commands { get; }
}

/// <summary>
/// A panel provides ImGui content. The workspace owns the window container
/// (Begin/End, position, size, and decoration); the panel only draws inside.
/// </summary>
public interface IUiPanel
{
    /// <summary>Stable for the lifetime of this registration.</summary>
    string Id { get; }
    string DisplayName { get; }

    /// <summary>
    /// Flags the workspace should pass to ImGui.Begin when creating this panel's window.
    /// Common choices: NoTitleBar, NoResize, NoCollapse, NoBackground, etc.
    /// </summary>
    ImGuiWindowFlags WindowFlags => ImGuiWindowFlags.None;

    /// <summary>
    /// Draw content between ImGui.Begin and ImGui.End. Do not call Begin/End yourself.
    /// </summary>
    void DrawContent(float deltaSeconds);
}

public interface IUiCommand
{
    /// <summary>Stable for the lifetime of this registration.</summary>
    string Id { get; }
    string DisplayName { get; }
    void Execute();
}
