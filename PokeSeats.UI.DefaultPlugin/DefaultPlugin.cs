using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPlugin;

/// <summary>
/// Built-in UI plugin: contributes the "Default" workspace (viewport dockspace plus main menu)
/// and the performance panel.
/// </summary>
public sealed class DefaultPlugin : IUiPlugin
{
    public const string WorkspaceId = "pokeseats.default.workspace";
    public const string PerformancePanelId = "pokeseats.default.performance";

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterWorkspace(WorkspaceId, "默认工作区", static () => new DefaultWorkspace());
        context.RegisterPanel(new PerformancePanel());
    }

    public void Dispose()
    {
        // Workspace instances are owned and disposed by the host workspace manager.
    }
}
