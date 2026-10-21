using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.Betapayload;

public sealed class BetapayloadPlugin : IUiPlugin
{
    public const string WorkspaceId = "pokeseats.betapayload.workspace";

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string settingsPath = Path.Combine(context.DataDirectory, "workspace-settings.json");
        context.RegisterWorkspace(WorkspaceId, WorkspaceStrings.WorkspaceName,
            () => new BetaWorkspace(settingsPath));
    }

    public void Dispose()
    {
        // Workspace instances are owned and disposed by the host workspace manager.
    }
}
