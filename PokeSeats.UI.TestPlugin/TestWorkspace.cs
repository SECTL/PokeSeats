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
    private readonly Dictionary<string, bool> _panelVisibility = new(StringComparer.Ordinal);
    private IWorkspaceContext? _context;

    internal bool IsDisposed { get; private set; }

    public void Activate(IWorkspaceContext _context)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(TestWorkspace));

        this._context = _context ?? throw new ArgumentNullException(nameof(_context));
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
            if (IsViewVisible(view.Id))
                DrawView(view, deltaSeconds);
        }
    }

    public void Deactivate()
    {
        // Visible-view choices deliberately survive deactivation.
        _context = null;
    }

    public void Dispose()
    {
        _context = null;
        _panelVisibility.Clear();
        IsDisposed = true;
    }

    private void DrawView(IUiView view, float deltaSeconds)
    {
        if (view.MinimumSize is { } minimumSize)
            ImGui.SetNextWindowSizeConstraints(minimumSize * UiScale.Current, new Vector2(float.MaxValue, float.MaxValue));

        bool open = true;
        string title = $"{view.DisplayName}###{view.Id}";
        if (ImGui.Begin(title, ref open))
            view.DrawContent(deltaSeconds);
        ImGui.End();

        if (!open)
            SetViewVisible(view.Id, false);
    }

    private void DrawMainMenuBar()
    {
        if (!ImGui.BeginMainMenuBar())
            return;

        IWorkspaceContext? context = _context;

        if (ImGui.BeginMenu("帮助"))
        {
            ImGui.MenuItem("已内置 HarmonyOS Sans 中文字体", null, false, false);
            ImGui.TextDisabled("版权所有 2021 Huawei Device Co., Ltd.");
            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("面板"))
        {
            if (context is null || context.Views.Count == 0)
            {
                ImGui.MenuItem("（未注册面板）", null, false, false);
            }
            else
            {
                foreach (IUiView view in context.Views)
                {
                    bool visible = IsViewVisible(view.Id);
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

    /// <summary>Views a workspace has not seen yet start visible; explicit choices stick.</summary>
    private bool IsViewVisible(string viewId)
        => !_panelVisibility.TryGetValue(viewId, out bool visible) || visible;

    private void SetViewVisible(string viewId, bool visible)
        => _panelVisibility[viewId] = visible;
}
