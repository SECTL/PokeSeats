using ImGuiNET;
using PokeSeats.UI.PluginContracts;
using System.Numerics;

namespace PokeSeats.UI.TestPlugin;

/// <summary>
/// Test workspace: owns the viewport dockspace and the main menu (panel toggles and
/// contributed commands) and draws every visible contributed panel. Panels draw content only;
/// this class owns their window lifetime and layout.
/// </summary>
internal sealed class TestWorkspace : IWorkspace
{
    public UiDesignTokens DesignTokens { get; } = UiDesignTokens.Dark;

    private readonly Dictionary<string, bool> _panelVisibility = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingFocus = new(StringComparer.Ordinal);
    private IWorkspaceContext? _context;
    private IDisposable? _viewOpenSubscription;

    internal bool IsDisposed { get; private set; }

    public void Activate(IWorkspaceContext _context)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(TestWorkspace));

        this._context = _context ?? throw new ArgumentNullException(nameof(_context));
        _viewOpenSubscription = _context.Events.Subscribe<UiViewOpenRequested>(OnViewOpenRequested);
    }

    public void Draw(float deltaSeconds)
    {
        ImGui.DockSpaceOverViewport();
        DrawMainMenuBar();

        IWorkspaceContext? context = _context;
        if (context is null)
            return;

        foreach (IUiView view in context.Views)
        {
            if (IsViewVisible(view))
                DrawView(view, deltaSeconds);
        }
    }

    public void Deactivate()
    {
        _viewOpenSubscription?.Dispose();
        _viewOpenSubscription = null;
        _pendingFocus.Clear();
        // Visible-view choices deliberately survive deactivation.
        _context = null;
    }

    public void Dispose()
    {
        _viewOpenSubscription?.Dispose();
        _viewOpenSubscription = null;
        _context = null;
        _panelVisibility.Clear();
        _pendingFocus.Clear();
        IsDisposed = true;
    }

    private void DrawView(IUiView view, float deltaSeconds)
    {
        if (_pendingFocus.Remove(view.Id))
        {
            // Reopen a collapsed panel and bring it to the front.
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
            ImGui.SetNextWindowFocus();
        }

        if (view.MinimumSize is { } minimumSize)
            ImGui.SetNextWindowSizeConstraints(minimumSize * UiScale.Current, new Vector2(float.MaxValue, float.MaxValue));

        bool open = true;
        string title = $"{view.DisplayName}###{view.Id}";
        if (ImGui.Begin(title, ref open))
            view.DrawContent(deltaSeconds, DesignTokens);
        ImGui.End();

        if (!open)
            SetViewVisible(view.Id, false);
    }

    private void DrawMainMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
            return;

        IWorkspaceContext? context = _context;

        if (ImGui.BeginMenu(WorkspaceStrings.HelpMenu))
        {
            ImGui.MenuItem(WorkspaceStrings.FontDescription, null, false, false);
            ImGui.TextDisabled(WorkspaceStrings.FontCopyright);
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(WorkspaceStrings.PanelsMenu))
        {
            if (context is null || context.Views.Count == 0)
            {
                ImGui.MenuItem(WorkspaceStrings.NoPanels, null, false, false);
            }
            else
            {
                foreach (IUiView view in context.Views)
                {
                    bool visible = IsViewVisible(view);
                    ImGui.PushID(view.Id);
                    bool clicked = ImGui.MenuItem(view.DisplayName, null, visible, true);
                    ImGui.PopID();
                    if (clicked)
                        SetViewVisible(view.Id, !visible);
                }
            }

            ImGui.EndMenu();
        }

        IUiCommand? pendingCommand = null;
        if (context is not null)
        {
            foreach (IUiCommand command in context.Commands)
            {
                ImGui.PushID(command.Id);
                bool clicked = ImGui.MenuItem(command.DisplayName);
                ImGui.PopID();
                if (clicked)
                    pendingCommand = command;
            }
        }

        ImGui.EndMainMenuBar();

        // Run after the menu bar is closed so a failing command cannot leave the menu stack open.
        pendingCommand?.Execute();
    }

    /// <summary>Views follow InitiallyVisible until the user makes an explicit choice, which sticks.</summary>
    private bool IsViewVisible(IUiView view)
        => _panelVisibility.TryGetValue(view.Id, out bool visible) ? visible : view.InitiallyVisible;

    private void SetViewVisible(string viewId, bool visible)
        => _panelVisibility[viewId] = visible;

    private void OnViewOpenRequested(UiViewOpenRequested request)
    {
        IWorkspaceContext? context = _context;
        if (context is null)
            return;

        foreach (IUiView view in context.Views)
        {
            if (!string.Equals(view.Id, request.ViewId, StringComparison.Ordinal))
                continue;

            SetViewVisible(view.Id, true);
            _pendingFocus.Add(view.Id);
            return;
        }
    }
}
