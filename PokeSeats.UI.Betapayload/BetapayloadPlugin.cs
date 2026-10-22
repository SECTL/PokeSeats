using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.Betapayload;

public sealed class BetapayloadPlugin : IUiPlugin
{
    public const string WorkspaceId = "pokeseats.betapayload.workspace";

    private GitHubUpdater? _updater;

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string settingsPath = Path.Combine(context.DataDirectory, "workspace-settings.json");
        context.RegisterWorkspace(WorkspaceId, WorkspaceStrings.WorkspaceName,
            () => new BetaWorkspace(settingsPath));

        var channelStore = new UpdateChannelStore(Path.Combine(context.DataDirectory, "update-settings.json"));
        _updater = new GitHubUpdater(
            () => context.Events.Publish(new UiViewOpenRequested(UpdateView.ViewId)),
            channelStore.Load(), channelStore.Save);
        var updateView = new UpdateView(_updater, context.Events);
        context.RegisterView(updateView);
        context.RegisterCommand(updateView);
        _updater.Check();
    }

    public void Dispose()
    {
        _updater?.Dispose();
        _updater = null;
        // Workspace instances are owned and disposed by the host workspace manager.
    }
}
