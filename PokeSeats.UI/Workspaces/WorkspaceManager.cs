using PokeSeats.UI.Events;
using PokeSeats.UI.PluginContracts;
using PokeSeats.UI.Plugins;

namespace PokeSeats.UI;

internal sealed class WorkspaceManager : IDisposable
{
    private readonly PluginCatalog _catalog;
    private readonly AppConfigStore _config;
    private readonly UiScaling _scaling;
    private readonly IUiTextureService _textures;
    private readonly Dictionary<string, IWorkspace> _instances = new(StringComparer.Ordinal);
    private readonly List<string> _diagnostics = new();
    private WorkspaceContext? _activeContext;
    private UiDesignTokens? _appliedTokens;
    private bool _disposed;

    public WorkspaceManager(PluginCatalog catalog, AppConfigStore config, UiScaling scaling, IUiTextureService textures)
    {
        _catalog = catalog;
        _config = config;
        _scaling = scaling;
        _textures = textures;
        string? preferredId = config.WorkspaceId;
        if (preferredId is not null)
        {
            WorkspaceRegistration? preferred = Workspaces.FirstOrDefault(item => item.Id == preferredId);
            if (preferred is not null && Switch(preferred.Id, remember: false))
                return;
        }

        foreach (WorkspaceRegistration registration in Workspaces)
        {
            if (registration.Id != preferredId && Switch(registration.Id, remember: false))
                break;
        }
    }

    public IReadOnlyList<WorkspaceRegistration> Workspaces => _catalog.Workspaces;
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public IReadOnlyList<string> PluginDiagnostics => _catalog.Diagnostics;
    public WorkspaceRegistration? Active { get; private set; }

    public void Draw(float deltaSeconds)
    {
        if (Active is null)
            return;

        IWorkspace workspace = _instances[Active.Id];
        workspace.Draw(deltaSeconds);
        ApplyDesignTokens(workspace);
    }

    // Called outside an ImGui frame so an outgoing workspace never draws after deactivation.
    public bool Switch(string id, bool remember = true)
    {
        WorkspaceRegistration? registration = Workspaces.FirstOrDefault(item => item.Id == id);
        if (registration is null)
            return false;
        if (Active?.Id == id)
        {
            if (remember)
                Remember(id);
            return true;
        }

        IWorkspace next;
        try
        {
            if (!_instances.TryGetValue(id, out next!))
            {
                next = registration.Create() ?? throw new InvalidOperationException("Workspace factory returned null.");
                _instances.Add(id, next);
            }
        }
        catch (Exception exception)
        {
            Report($"Cannot create workspace '{id}': {exception.Message}");
            return false;
        }

        WorkspaceRegistration? previous = Active;
        IWorkspace? previousInstance = previous is null ? null : _instances[previous.Id];
        Active = null;
        try
        {
            DeactivateWorkspace(previousInstance);
        }
        catch (Exception exception)
        {
            Report($"Cannot deactivate workspace '{previous!.Id}': {exception.Message}");
            Restore(previous, previousInstance);
            return false;
        }

        try
        {
            ActivateWorkspace(next);
            Active = registration;
        }
        catch (Exception exception)
        {
            Report($"Cannot activate workspace '{id}': {exception.Message}");
            try { next.Dispose(); }
            catch (Exception cleanup) { Report($"Cannot dispose workspace '{id}': {cleanup.Message}"); }
            _instances.Remove(id);
            Restore(previous, previousInstance);
            return false;
        }

        Console.WriteLine($"Workspace activated: {registration.Id}");
        if (remember)
            Remember(id);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (Active is not null)
        {
            try { DeactivateWorkspace(_instances[Active.Id]); }
            catch (Exception exception) { Report($"Cannot deactivate workspace '{Active.Id}': {exception.Message}"); }
            Active = null;
        }
        foreach (var (id, workspace) in _instances.Reverse())
        {
            try { workspace.Dispose(); }
            catch (Exception exception) { Report($"Cannot dispose workspace '{id}': {exception.Message}"); }
        }
        _instances.Clear();
    }

    private void ActivateWorkspace(IWorkspace workspace)
    {
        var context = new WorkspaceContext(_catalog, _catalog.CreateEventScope(), _textures);
        try
        {
            UiDesignTokens tokens = workspace.DesignTokens;
            _scaling.RestoreStyle();
            UiTheme.Apply(tokens);
            workspace.Activate(context);
            _scaling.CaptureStyle();
            _appliedTokens = tokens;
            _activeContext = context;
        }
        catch
        {
            try { workspace.Deactivate(); }
            catch (Exception cleanup) { Report($"Cannot clean up workspace activation: {cleanup.Message}"); }
            finally { context.Dispose(); }
            throw;
        }
    }

    private void DeactivateWorkspace(IWorkspace? workspace)
    {
        try { workspace?.Deactivate(); }
        finally
        {
            _activeContext?.Dispose();
            _activeContext = null;
        }
    }

    private void ApplyDesignTokens(IWorkspace workspace)
    {
        UiDesignTokens tokens = workspace.DesignTokens;
        if (_appliedTokens is { } applied && applied == tokens)
            return;

        _scaling.RestoreStyle();
        UiTheme.Apply(tokens);
        _scaling.CaptureStyle();
        _scaling.ApplyLast();
        _appliedTokens = tokens;
    }

    private void Restore(WorkspaceRegistration? registration, IWorkspace? workspace)
    {
        if (workspace is null)
            return;
        try
        {
            ActivateWorkspace(workspace);
            Active = registration;
        }
        catch (Exception exception)
        {
            Report($"Cannot restore workspace '{registration!.Id}': {exception.Message}");
        }
    }

    private sealed class WorkspaceContext : IWorkspaceContext, IDisposable
    {
        private readonly PluginCatalog _catalog;
        private readonly UiEventBus.Scope _events;
        private readonly IUiTextureService _textures;

        internal WorkspaceContext(PluginCatalog catalog, UiEventBus.Scope events, IUiTextureService textures)
        {
            _catalog = catalog;
            _events = events;
            _textures = textures;
        }

        public IReadOnlyList<IUiView> Views => _catalog.Views;
        public IReadOnlyList<IUiCommand> Commands => _catalog.Commands;
        public IUiTextureService Textures => _textures;
        public IUiEventBus Events => _events;
        public void Dispose() => _events.Dispose();
    }

    private void Remember(string id)
    {
        try { _config.SetWorkspace(id); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Report($"Workspace switched, but selection could not be saved: {exception.Message}");
        }
    }

    private void Report(string message)
    {
        _diagnostics.Add(message);
        Console.Error.WriteLine(message);
    }
}
