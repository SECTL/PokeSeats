using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPlugin;

/// <summary>Frame-time readout contributed by the built-in plugin.</summary>
internal sealed class PerformancePanel : IUiPanel
{
    public string Id => DefaultPlugin.PerformancePanelId;

    public string DisplayName => "性能";

    public void Draw(float deltaSeconds)
    {
        if (!ImGui.Begin("性能###pokeseats.default.performance"))
        {
            ImGui.End();
            return;
        }

        ImGui.Text($"帧时间：{deltaSeconds * 1000.0f:F1} ms");

        ImGui.Separator();
        ImGui.TextDisabled("工作区切换：Ctrl+Alt+Shift+X");

        ImGui.End();
    }
}
