using ImGuiNET;
using System.Numerics;
using Silk.NET.Input;
using PokeSeats.UI.Plugins;

namespace PokeSeats.UI;

internal sealed class WorkspaceSelector
{
    private readonly WorkspaceManager _manager;
    private bool _shortcutWasDown;
    private bool _open;
    private string? _pendingWorkspaceId;
    private bool _requestOpen;

    // ImGui popup ids must match between OpenPopup and BeginPopupModal, so the label and the
    // stable ###suffix are combined once instead of per frame.
    private static readonly string SelectorPopupId =
        HostStrings.WorkspaceSelectorTitle + "###pokeseats.host.workspace-selector";

    public WorkspaceSelector(WorkspaceManager manager)
    {
        _manager = manager;
        _open = manager.Active is null || manager.PluginDiagnostics.Count > 0;
        _requestOpen = _open;
    }

    public void HandleShortcut(IInputContext input)
    {
        bool shortcutDown = false;
        foreach (IKeyboard keyboard in input.Keyboards)
        {
            shortcutDown = keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight);
            shortcutDown = shortcutDown
                && (keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight))
                && (keyboard.IsKeyPressed(Key.ShiftLeft) || keyboard.IsKeyPressed(Key.ShiftRight))
                && keyboard.IsKeyPressed(Key.X);
            if (shortcutDown)
                break;
        }

        if (shortcutDown && !_shortcutWasDown)
        {
            _open = true;
            _requestOpen = true;
        }
        _shortcutWasDown = shortcutDown;
    }

    public void Draw()
    {
        if (!_open)
            return;

        if (_requestOpen)
        {
            ImGui.OpenPopup(SelectorPopupId);
            _requestOpen = false;
        }
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos + viewport.Size * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f));
        bool open = _open;
        if (ImGui.BeginPopupModal(SelectorPopupId, ref open,
                ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoDocking))
        {
            ImGui.Text(HostStrings.WorkspaceSelectorPrompt);
            ImGui.TextDisabled(HostStrings.WorkspaceSelectorRestartHint);
            ImGui.Separator();

            if (_manager.Workspaces.Count == 0)
            {
                ImGui.Text(HostStrings.WorkspaceSelectorEmpty);
                ImGui.TextDisabled(HostStrings.WorkspaceSelectorEmptyHint);
            }
            else
            {
                foreach (WorkspaceRegistration workspace in _manager.Workspaces)
                {
                    bool selected = _manager.Active?.Id == workspace.Id;
                    ImGui.PushID(workspace.Id);
                    bool clicked = ImGui.Selectable(workspace.DisplayName, selected);
                    ImGui.PopID();
                    if (clicked)
                    {
                        _pendingWorkspaceId = workspace.Id;
                        break;
                    }
                    if (selected)
                        ImGui.SetItemDefaultFocus();
                }
            }

            DrawDiagnostics();

            ImGui.Separator();
            if (ImGui.Button(HostStrings.Close) || _pendingWorkspaceId is not null)
            {
                ImGui.CloseCurrentPopup();
                open = false;
            }
            ImGui.EndPopup();
        }
        _open = open;
    }

    public void ApplyPendingSwitch()
    {
        if (_pendingWorkspaceId is not { } id)
            return;
        _pendingWorkspaceId = null;
        _open = !_manager.Switch(id);
        _requestOpen = _open;
    }

    private void DrawDiagnostics()
    {
        if (_manager.PluginDiagnostics.Count == 0 && _manager.Diagnostics.Count == 0)
            return;

        ImGui.Separator();
        ImGui.TextDisabled(HostStrings.DiagnosticsTitle);
        foreach (string diagnostic in _manager.PluginDiagnostics)
            ImGui.TextWrapped(diagnostic);
        foreach (string diagnostic in _manager.Diagnostics)
            ImGui.TextWrapped(diagnostic);
    }
}
