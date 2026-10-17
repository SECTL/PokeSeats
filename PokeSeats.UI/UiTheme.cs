using System.Numerics;
using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI;

/// <summary>Applies reference-pixel tokens before the workspace customizes its activation style.</summary>
internal static class UiTheme
{
    internal static void Apply(UiDesignTokens tokens)
    {
        UiColors colors = tokens.Colors;
        UiMetrics metrics = tokens.Metrics;
        ImGui.StyleColorsLight();
        ImGuiStylePtr style = ImGui.GetStyle();
        style.WindowPadding = new Vector2(metrics.SpaceMedium);
        style.FramePadding = new Vector2(metrics.SpaceMedium, metrics.SpaceSmall);
        style.ItemSpacing = new Vector2(metrics.SpaceMedium, metrics.SpaceSmall);
        style.ItemInnerSpacing = new Vector2(metrics.SpaceSmall);
        style.CellPadding = new Vector2(metrics.SpaceSmall);
        style.WindowRounding = metrics.WindowRounding;
        style.ChildRounding = style.FrameRounding = style.PopupRounding = style.TabRounding = metrics.ControlRounding;
        style.GrabRounding = style.ScrollbarRounding = metrics.ControlRounding;
        style.WindowBorderSize = style.ChildBorderSize = style.PopupBorderSize = metrics.BorderWidth;
        for (int index = 0; index < (int)ImGuiCol.COUNT; index++)
        {
            ImGuiCol role = (ImGuiCol)index;
            style.Colors[index] = role switch
            {
                ImGuiCol.Text => colors.Text,
                ImGuiCol.TextDisabled => colors.TextMuted,
                ImGuiCol.WindowBg or ImGuiCol.ChildBg or ImGuiCol.PopupBg => colors.Surface,
                ImGuiCol.Border or ImGuiCol.Separator or ImGuiCol.TableBorderStrong or ImGuiCol.TableBorderLight => colors.Border,
                ImGuiCol.BorderShadow or ImGuiCol.TableRowBg => Vector4.Zero,
                ImGuiCol.FrameBgHovered or ImGuiCol.ButtonHovered or ImGuiCol.HeaderHovered
                    or ImGuiCol.TabHovered or ImGuiCol.ScrollbarGrabHovered
                    => Vector4.Lerp(colors.AccentSurface, colors.Accent, .15f),
                ImGuiCol.FrameBgActive or ImGuiCol.ButtonActive or ImGuiCol.HeaderActive
                    or ImGuiCol.ScrollbarGrabActive => Vector4.Lerp(colors.AccentSurface, colors.Accent, .30f),
                ImGuiCol.FrameBg or ImGuiCol.TitleBg or ImGuiCol.TitleBgCollapsed
                    or ImGuiCol.ScrollbarBg or ImGuiCol.TableHeaderBg or ImGuiCol.TableRowBgAlt
                    or ImGuiCol.TabDimmed or ImGuiCol.DockingEmptyBg => colors.Background,
                ImGuiCol.CheckMark or ImGuiCol.SliderGrab or ImGuiCol.SliderGrabActive
                    or ImGuiCol.SeparatorHovered or ImGuiCol.SeparatorActive
                    or ImGuiCol.ResizeGripHovered or ImGuiCol.ResizeGripActive
                    or ImGuiCol.TabSelectedOverline or ImGuiCol.TabDimmedSelectedOverline
                    or ImGuiCol.NavCursor or ImGuiCol.TextLink => colors.Accent,
                ImGuiCol.PlotLines or ImGuiCol.PlotHistogram => colors.Success,
                ImGuiCol.PlotLinesHovered or ImGuiCol.PlotHistogramHovered => colors.Warning,
                ImGuiCol.TextSelectedBg or ImGuiCol.DockingPreview => colors.Selection,
                ImGuiCol.ModalWindowDimBg or ImGuiCol.NavWindowingDimBg => new Vector4(0, 0, 0, .35f),
                ImGuiCol.DragDropTarget => colors.SelectionBorder,
                _ => colors.AccentSurface
            };
        }
    }
}
