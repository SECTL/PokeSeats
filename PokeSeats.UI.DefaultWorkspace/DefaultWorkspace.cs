using System.Numerics;
using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultWorkspace;

public class DefaultWorkspace : IWorkspace
{
    internal bool               IsDisposed { get; private set; }
    private IWorkspaceContext? context;
    private bool openAboutPopup;
    private readonly Dictionary<string, bool> _viewVisibility = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ViewGeometry> _viewGeometry = new(StringComparer.Ordinal);

    private readonly record struct ViewGeometry(Vector2 Position, Vector2 Size, float Scale, bool Docked);
    public void Activate(IWorkspaceContext _context)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(DefaultWorkspace));

        this.context = _context ?? throw new ArgumentNullException(nameof(_context));
        
        ImGui.StyleColorsLight();
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowRounding = 8.0f;
        style.FrameRounding = 6.0f;
        style.PopupRounding = 6.0f;
        style.TabRounding = 6.0f;
    }

    private bool IsViewVisible(string viewId)
        => !_viewVisibility.TryGetValue(viewId, out bool visible) || visible;

    private void SetViewVisible(string viewId, bool visible)
        => _viewVisibility[viewId] = visible;
    private void DrawMainMenuBar(IReadOnlyList<IUiCommand> uiCommands, IReadOnlyList<IUiView> uiViews)
    {
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, new Vector4(1f, 1f, 1f, 0.6f)); // 半透明白色
        if (!ImGui.BeginMainMenuBar())
        {
            ImGui.PopStyleColor();
            return;
        }

        if (ImGui.BeginMenu("应用"))
        {
            if (ImGui.MenuItem("关于 PokeSeats"))
                openAboutPopup = true;

            if (ImGui.MenuItem("退出"))
                context!.Events.Publish(new ApplicationExitRequested());

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu("面板"))
        {
            if (uiViews.Count == 0)
            {
                ImGui.MenuItem("（无面板）", null, false, false);
            }
            else
            {
                foreach (IUiView view in uiViews)
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
        foreach (IUiCommand command in uiCommands)
        {
            ImGui.PushID(command.Id);
            bool clicked = ImGui.MenuItem(command.DisplayName);
            ImGui.PopID();
            if (clicked)
                pendingCommand = command;
        }

        ImGui.EndMainMenuBar();
        ImGui.PopStyleColor();

        pendingCommand?.Execute();
    }


    private void DrawView(IUiView view, Vector2 initialPosition, Vector2 initialSize, float deltaSeconds)
    {
        float scale = UiScale.Current;
        Vector2 viewportOrigin = ImGui.GetMainViewport().Pos;
        if (!_viewGeometry.TryGetValue(view.Id, out ViewGeometry previous))
        {
            ImGui.SetNextWindowPos(initialPosition, ImGuiCond.Always);
            ImGui.SetNextWindowSize(initialSize, ImGuiCond.Always);
        }
        else if (previous.Scale != scale && !previous.Docked)
        {
            ImGui.SetNextWindowPos(viewportOrigin + previous.Position * scale, ImGuiCond.Always);
            ImGui.SetNextWindowSize(previous.Size * scale, ImGuiCond.Always);
        }
        if (view.MinimumSize is { } minimumSize)
            ImGui.SetNextWindowSizeConstraints(minimumSize * scale, new Vector2(float.MaxValue, float.MaxValue));

        bool open = true;
        string title = $"{view.DisplayName}###{view.Id}";
        bool visible = ImGui.Begin(title, ref open);
        _viewGeometry[view.Id] = new ViewGeometry(
            (ImGui.GetWindowPos() - viewportOrigin) / scale,
            ImGui.GetWindowSize() / scale, scale, ImGui.IsWindowDocked());
        if (visible)
            view.DrawContent(deltaSeconds);
        ImGui.End();

        if (!open)
            SetViewVisible(view.Id, false);
    }

    private void DrawViews(float deltaSeconds)
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        float scale = UiScale.Current;
        float margin = 16f * scale;
        Vector2 offset = new(margin, 40f * scale);
        float rowHeight = 0f;
        foreach (IUiView view in context!.Views)
        {
            if (!IsViewVisible(view.Id))
                continue;
            Vector2 size = (view.MinimumSize ?? new Vector2(520, 360)) * scale;
            if (offset.X > margin && offset.X + size.X > viewport.Size.X - margin)
            {
                offset.X = margin;
                offset.Y += rowHeight + margin;
                rowHeight = 0f;
            }
            // Additional views must remain reachable even when the tiled rows exceed the viewport.
            Vector2 position = new(offset.X, Math.Min(offset.Y, Math.Max(40f * scale, viewport.Size.Y - size.Y - margin)));
            DrawView(view, viewport.Pos + position, size, deltaSeconds);
            offset.X += size.X + margin;
            rowHeight = Math.Max(rowHeight, size.Y);
        }
    }

    public void Draw(float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.SetNextWindowViewport(viewport.ID);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNavFocus;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.9f, 0.9f, 0.9f, 1f)); // 浅灰色背景
        bool visible = ImGui.Begin("###pokeseats.defaultworkspace.root", flags);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        try
        {
            if (visible)
            {
                uint dockspaceId = ImGui.GetID("pokeseats.defaultworkspace.dockspace");
                ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
                DrawViews(deltaSeconds);
                DrawMainMenuBar(context.Commands, context.Views);
                DrawAboutPopup();

            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private void DrawAboutPopup()
    {
        const string popupId = "关于 PokeSeats###pokeseats.defaultworkspace.about";
        if (openAboutPopup)
        {
            ImGui.OpenPopup(popupId);
            openAboutPopup = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.Text("PokeSeats 戳座");
            ImGui.TextDisabled("Copyright (c) chenjintang-shrimp 2026");
            ImGui.Separator();
            ImGui.Text("本软件使用 HarmonyOS Sans 作为用户界面字体。");
            ImGui.TextDisabled("版权所有 2021 Huawei Device Co., Ltd.");
            
            if (ImGui.Button("关闭"))
                ImGui.CloseCurrentPopup();
            
            ImGui.EndPopup();
        }
    }

    public void Deactivate()
    {
        // Visible-view choices deliberately survive deactivation.
        openAboutPopup = false;
        context = null;
    }

    public void Dispose()
    {
        context = null;
        // _panelVisibility.Clear();
        _viewVisibility.Clear();
        _viewGeometry.Clear();
        IsDisposed = true;
    }
}
