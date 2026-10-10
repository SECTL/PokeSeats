using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.TestPlugin;

/// <summary>Frame-time readout contributed by the test plugin.</summary>
internal sealed class PerformancePanel : IUiView
{
    public string Id => TestPlugin.PerformancePanelId;

    public string DisplayName => "性能";

    public void DrawContent(float deltaSeconds)
    {
        ImGui.Text($"帧时间：{deltaSeconds * 1000.0f:F1} ms");

        ImGui.Separator();
        ImGui.TextDisabled("工作区切换：Ctrl+Alt+Shift+X");
    }
}
