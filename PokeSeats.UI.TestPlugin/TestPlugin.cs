using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.TestPlugin;

/// <summary>Test UI plugin that contributes a docking workspace and performance panel.</summary>
public sealed class TestPlugin : IUiPlugin
{
    public const string WorkspaceId = "pokeseats.test.workspace";
    public const string PerformancePanelId = "pokeseats.test.performance";

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterWorkspace(WorkspaceId, "测试工作区", static () => new TestWorkspace());
        context.RegisterView(new PerformancePanel());
    }

    public void Dispose()
    {
        // Workspace instances are owned and disposed by the host workspace manager.
    }

}
