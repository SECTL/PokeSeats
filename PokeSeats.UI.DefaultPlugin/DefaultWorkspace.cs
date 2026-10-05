using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPlugin;

/// <summary>
/// Default workspace: owns the viewport dockspace and the main menu (panel toggles and
/// contributed commands) and draws every visible contributed panel. Panels draw their own
/// windows; this class never touches the ImGui frame or context lifecycle.
/// </summary>
internal sealed class DefaultWorkspace : IWorkspace
{
    private readonly Dictionary<string, bool> _panelVisibility = new(StringComparer.Ordinal);
    private IWorkspaceContext? _context;

    internal bool IsDisposed { get; private set; }

    public void Activate(IWorkspaceContext context)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(DefaultWorkspace));

        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public void Draw(float deltaSeconds)
    {
        // Kept in the same order as the previous host loop: dockspace first, windows afterwards.
        ImGui.DockSpaceOverViewport();

        DrawMainMenuBar();

        IWorkspaceContext? context = _context;
        if (context is null)
            return;

        foreach (IUiPanel panel in context.Panels)
        {
            if (IsPanelVisible(panel.Id))
                panel.Draw(deltaSeconds);
        }
    }

    public void Deactivate()
    {
        // Visible-panel choices deliberately survive deactivation.
        _context = null;
    }

    public void Dispose()
    {
        _context = null;
        _panelVisibility.Clear();
        IsDisposed = true;
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
            if (context is null || context.Panels.Count == 0)
            {
                ImGui.MenuItem("（未注册面板）", null, false, false);
            }
            else
            {
                foreach (IUiPanel panel in context.Panels)
                {
                    bool visible = IsPanelVisible(panel.Id);
                    ImGui.PushID(panel.Id);
                    bool clicked = ImGui.MenuItem(panel.DisplayName, null, visible, true);
                    ImGui.PopID();
                    if (clicked)
                        SetPanelVisible(panel.Id, !visible);
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

    /// <summary>Panels a workspace has not seen yet start visible; explicit choices stick.</summary>
    private bool IsPanelVisible(string panelId)
        => !_panelVisibility.TryGetValue(panelId, out bool visible) || visible;

    private void SetPanelVisible(string panelId, bool visible)
        => _panelVisibility[panelId] = visible;
}
