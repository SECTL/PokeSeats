using System.Numerics;
using ImGuiNET;
using PokeSeats.IR.Base;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

internal readonly record struct ClassroomGridGeometry(float Cell, Vector2 Size, Vector2 Origin, int Rows, int Cols)
{
    internal static ClassroomGridGeometry Place(LayoutDocument layout)
    {
        Vector2 available = ImGui.GetContentRegionAvail();
        float cell = Math.Max(1, Math.Min((available.X - 1) / layout.Cols, (available.Y - 1) / layout.Rows));
        Vector2 size = new Vector2(layout.Cols, layout.Rows) * cell;
        ImGui.SetCursorPos(ImGui.GetCursorPos() + Vector2.Max(Vector2.Zero, (available - size) / 2));
        return new ClassroomGridGeometry(cell, size, ImGui.GetCursorScreenPos(), layout.Rows, layout.Cols);
    }

    internal GridPos Hit(Vector2 mouse) => new(
        Math.Clamp((int)((mouse.Y - Origin.Y) / Cell), 0, Rows - 1),
        Math.Clamp((int)((mouse.X - Origin.X) / Cell), 0, Cols - 1));

    internal static uint CellColor(CellType type, UiDesignTokens tokens) => ImGui.ColorConvertFloat4ToU32(type switch
    {
        CellType.Desk => tokens.Colors.AccentSurface,
        CellType.Podium => tokens.Colors.SecondarySurface,
        CellType.NarrowAisle => tokens.Colors.WarningSurface,
        CellType.Aisle => tokens.Colors.Surface,
        _ => tokens.Colors.Canvas
    });
}

internal sealed class ClassroomRegionPicker
{
    private readonly LayoutDocument layout;
    private bool dragging;
    private GridPos first;
    private GridPos last;
    private string? activeId;

    internal ClassroomRegionPicker(LayoutDocument layout) => this.layout = layout;

    public bool Draw(string id, ref SeatRegion? region, UiDesignTokens designTokens, float height)
    {
        if (activeId != id) { dragging = false; activeId = id; }
        bool changed = false;
        ImGui.PushID(id);
        if (!ImGui.BeginChild("##objective-classroom", new Vector2(0, height), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.HorizontalScrollbar))
        { ImGui.EndChild(); ImGui.PopID(); return false; }
        var geometry = ClassroomGridGeometry.Place(layout);
        ImGui.InvisibleButton("##region-surface", geometry.Size);
        GridPos hit = geometry.Hit(ImGui.GetIO().MousePos);
        if (ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        { first = last = hit; dragging = true; }
        if (dragging)
        {
            last = hit;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                SeatRegion next = Rectangle(first, last);
                changed = region != next;
                region = next;
                dragging = false;
            }
        }
        SeatRegion? preview = dragging ? Rectangle(first, last) : region;
        var draw = ImGui.GetWindowDrawList();
        for (int row = 0; row < layout.Rows; row++)
            for (int col = 0; col < layout.Cols; col++)
            {
                Vector2 min = geometry.Origin + new Vector2(col, row) * geometry.Cell;
                Vector2 max = min + new Vector2(geometry.Cell);
                CellType type = layout.TypeAt(row, col);
                draw.AddRectFilled(min, max, ClassroomGridGeometry.CellColor(type, designTokens));
                draw.AddRect(min, max, ImGui.ColorConvertFloat4ToU32(designTokens.Colors.Border));
                if (preview is { } scope && scope.Contains(new GridPos(row, col)))
                {
                    draw.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(designTokens.Colors.Selection));
                    draw.AddRect(min + Vector2.One, max - Vector2.One, ImGui.ColorConvertFloat4ToU32(designTokens.Colors.SelectionBorder));
                }
                if (type == CellType.Podium)
                {
                    string label = RulesStrings.PodiumMarker;
                    Vector2 textSize = ImGui.CalcTextSize(label);
                    float scale = Math.Min(1, Math.Max(1, geometry.Cell - UiScale.Pixels(designTokens.Metrics.SpaceSmall)) / Math.Max(textSize.X, textSize.Y));
                    draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
                        min + (new Vector2(geometry.Cell) - textSize * scale) / 2,
                        ImGui.ColorConvertFloat4ToU32(designTokens.Colors.Text), label);
                }
            }
        ImGui.EndChild();
        ImGui.PopID();
        return changed;
    }

    private static SeatRegion Rectangle(GridPos first, GridPos last) => new(
        Math.Min(first.Row, last.Row), Math.Max(first.Row, last.Row),
        Math.Min(first.Col, last.Col), Math.Max(first.Col, last.Col));
}
