using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultWorkspace;

public sealed class DefaultUIPlugin : IUiPlugin
{
    public const string WORKSPACE_ID = "pokeseats.defaultworkspace.workspace";
    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterWorkspace(id: WORKSPACE_ID, displayName: WorkspaceStrings.WorkspaceName, static () => new DefaultWorkspace());
    }

    public void Dispose()
    {
        // Workspace instances are owned and disposed by the host workspace manager.
    }
}
