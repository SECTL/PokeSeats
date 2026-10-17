using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.TestPlugin;

/// <summary>Frame-time readout contributed by the test plugin.</summary>
internal sealed class PerformancePanel : IUiView
{
    public string Id => TestPlugin.PerformancePanelId;

    public string DisplayName => WorkspaceStrings.PerformanceName;

    public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
    {
        ImGui.Text(string.Format(WorkspaceStrings.FrameTime, deltaSeconds * 1000.0f));

        ImGui.Separator();
        ImGui.TextDisabled(WorkspaceStrings.WorkspaceShortcut);
    }
}
