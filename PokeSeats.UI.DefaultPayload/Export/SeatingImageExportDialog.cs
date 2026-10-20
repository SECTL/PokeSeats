using System.Numerics;
using ImGuiNET;
using PokeSeats.IR.Base;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class SeatingImageExportDialog
{
    private const string PopupId = "###seating-image-export";
    private readonly SeatingImageOptions options = new();
    private string path;
    private string? status;
    private GridPos? selected;
    private bool openRequested;

    public SeatingImageExportDialog(string dataDirectory)
        => path = Path.Combine(dataDirectory, "exports", "seating.png");

    public void Open() => openRequested = true;

    public void ResetResult()
    {
        selected = null;
        options.Cells.Clear();
        status = null;
    }

    public void Draw(LayoutGrid layout, IReadOnlyDictionary<GridPos, Person> occupants)
    {
        string popup = ExportStrings.Title + PopupId;
        if (openRequested)
        {
            ImGui.OpenPopup(popup);
            openRequested = false;
        }
        Vector2 screen = ImGui.GetMainViewport().Size;
        ImGui.SetNextWindowSize(Vector2.Min(new Vector2(UiScale.Pixels(1080), UiScale.Pixels(780)), screen * .94f), ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(popup, ref open, ImGuiWindowFlags.NoSavedSettings))
            return;

        float footer = UiScale.Pixels(160);
        if (ImGui.BeginTable("##export-columns", 2, ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("##settings", ImGuiTableColumnFlags.WidthFixed, UiScale.Pixels(360));
            ImGui.TableSetupColumn("##preview", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableNextColumn();
            if (ImGui.BeginChild("##export-settings", new Vector2(0, Math.Max(UiScale.Pixels(120), ImGui.GetContentRegionAvail().Y - footer))))
                DrawSettings(layout, occupants);
            ImGui.EndChild();
            ImGui.TableNextColumn();
            ImGui.TextWrapped(ExportStrings.PreviewHint);
            DrawPreview(layout, occupants, Math.Max(UiScale.Pixels(120), ImGui.GetContentRegionAvail().Y - footer));
            ImGui.EndTable();
        }
        ImGui.Separator();
        ImGui.TextUnformatted(ExportStrings.Path);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##export-path", ref path, 4096);
        if (OperatingSystem.IsWindows())
        {
            if (ImGui.Button(ExportStrings.Browse))
            {
                string? chosen = WindowsFilePicker.SelectPngFile(path);
                if (chosen is not null) path = chosen;
            }
            ImGui.SameLine();
        }
        if (ImGui.Button(ExportStrings.Save))
        {
            try
            {
                path = NormalizePath(path);
                if (File.Exists(path)) ImGui.OpenPopup(ExportStrings.OverwriteTitle);
                else Save(layout, occupants);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
            { status = string.Format(ExportStrings.Failure, exception.Message); }
        }
        ImGui.SameLine();
        if (ImGui.Button(ExportStrings.Close)) ImGui.CloseCurrentPopup();
        if (status is not null) ImGui.TextWrapped(status);
        if (ImGui.BeginPopupModal(ExportStrings.OverwriteTitle, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(ExportStrings.OverwritePrompt);
            ImGui.TextUnformatted(path);
            if (ImGui.Button(ExportStrings.Overwrite))
            {
                Save(layout, occupants);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(ExportStrings.Cancel)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.EndPopup();
    }

    private void DrawSettings(LayoutGrid layout, IReadOnlyDictionary<GridPos, Person> occupants)
    {
        ImGui.TextUnformatted(ExportStrings.ImageTitle);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##export-title", ref options.Title, 512);
        if (ImGui.RadioButton(ExportStrings.PodiumTop, !options.PodiumAtBottom)) options.PodiumAtBottom = false;
        if (ImGui.RadioButton(ExportStrings.PodiumBottom, options.PodiumAtBottom)) options.PodiumAtBottom = true;
        ImGui.TextWrapped(ExportStrings.OrientationHint);
        ImGui.SeparatorText(ExportStrings.Contents);
        ImGui.Checkbox(ExportStrings.Name, ref options.ShowName);
        ImGui.SameLine();
        ImGui.Checkbox(ExportStrings.StudentNumber, ref options.ShowStudentNumber);
        ImGui.SameLine();
        ImGui.Checkbox(ExportStrings.Group, ref options.ShowGroup);
        ImGui.Checkbox(ExportStrings.Coordinates, ref options.ShowCoordinates);
        ImGui.Checkbox(ExportStrings.RowNumbers, ref options.ShowRowNumbers);
        ImGui.SameLine();
        ImGui.Checkbox(ExportStrings.ColumnNumbers, ref options.ShowColumnNumbers);
        if (selected is { } seat)
        {
            ImGui.SeparatorText(string.Format(ExportStrings.SelectedCell, seat.Row + 1, seat.Col + 1));
            options.Cells.TryGetValue(seat, out SeatingCellStyle? style);
            bool customText = style?.Text is not null;
            if (ImGui.Checkbox(ExportStrings.CustomText, ref customText))
            {
                style ??= new SeatingCellStyle();
                occupants.TryGetValue(seat, out Person? person);
                style.Text = customText ? options.GetText(seat, layout.TypeAt(seat), person) : null;
                options.Cells[seat] = style;
            }
            if (customText)
            {
                string text = style!.Text!;
                if (ImGui.InputTextMultiline("##cell-text", ref text, 4096, new Vector2(-1, UiScale.Pixels(82)))) style.Text = text;
            }
            bool customColor = style?.Color is not null;
            if (ImGui.Checkbox(ExportStrings.CustomColor, ref customColor))
            {
                style ??= new SeatingCellStyle();
                occupants.TryGetValue(seat, out Person? person);
                style.Color = customColor ? options.GetColor(seat, layout.TypeAt(seat), person) : null;
                options.Cells[seat] = style;
            }
            if (customColor)
            {
                Vector4 color = style!.Color!.Value;
                if (ImGui.ColorEdit4("##cell-color", ref color, ImGuiColorEditFlags.NoAlpha)) style.Color = color;
            }
            if (ImGui.Button(ExportStrings.ResetCell)) options.Cells.Remove(seat);
        }
        if (ImGui.CollapsingHeader(ExportStrings.Colors, ImGuiTreeNodeFlags.DefaultOpen))
        {
            EditColor(ExportStrings.Background, ref options.Background);
            EditColor(ExportStrings.TextColor, ref options.TextColor);
            EditColor(ExportStrings.Border, ref options.BorderColor);
            EditColor(ExportStrings.Occupied, ref options.OccupiedColor);
            EditColor(ExportStrings.Vacant, ref options.VacantColor);
            EditColor(ExportStrings.Podium, ref options.PodiumColor);
            EditColor(ExportStrings.Aisle, ref options.AisleColor);
            EditColor(ExportStrings.NarrowAisle, ref options.NarrowAisleColor);
            EditColor(ExportStrings.Empty, ref options.EmptyColor);
        }
    }

    private void DrawPreview(LayoutGrid layout, IReadOnlyDictionary<GridPos, Person> occupants, float height)
    {
        if (!ImGui.BeginChild("##export-preview", new Vector2(0, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        { ImGui.EndChild(); return; }
        Vector2 available = ImGui.GetContentRegionAvail();
        float rowHeader = options.ShowRowNumbers ? UiScale.Pixels(58) : 0;
        float columnHeader = options.ShowColumnNumbers ? UiScale.Pixels(30) : 0;
        Vector2 cell = new(Math.Max(UiScale.Pixels(72), (available.X - rowHeader) / layout.Cols), UiScale.Pixels(72));
        float titleHeight = string.IsNullOrEmpty(options.Title) ? 0 : UiScale.Pixels(38);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 grid = origin + new Vector2(rowHeader, columnHeader + titleHeight);
        Vector2 size = new(rowHeader + cell.X * layout.Cols, titleHeight + columnHeader + cell.Y * layout.Rows);
        ImGui.InvisibleButton("##export-surface", size);
        if (ImGui.IsItemClicked())
        {
            Vector2 point = ImGui.GetMousePos() - grid;
            int row = (int)MathF.Floor(point.Y / cell.Y), col = (int)MathF.Floor(point.X / cell.X);
            if (row >= 0 && row < layout.Rows && col >= 0 && col < layout.Cols)
                selected = options.SourcePosition(layout, row, col);
        }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(origin, origin + size, Color(options.Background));
        if (titleHeight > 0) DrawText(draw, origin, new Vector2(size.X, titleHeight), options.Title);
        if (options.ShowColumnNumbers)
            for (int col = 0; col < layout.Cols; col++)
            {
                GridPos seat = options.SourcePosition(layout, 0, col);
                DrawText(draw, origin + new Vector2(rowHeader + col * cell.X, titleHeight), new Vector2(cell.X, columnHeader), string.Format(PayloadStrings.SeatingColumnHeaderFormat, seat.Col + 1));
            }
        for (int row = 0; row < layout.Rows; row++)
        {
            GridPos first = options.SourcePosition(layout, row, 0);
            if (options.ShowRowNumbers)
                DrawText(draw, origin + new Vector2(0, titleHeight + columnHeader + row * cell.Y), new Vector2(rowHeader, cell.Y), string.Format(PayloadStrings.SeatingRowHeaderFormat, first.Row + 1));
            for (int col = 0; col < layout.Cols; col++)
            {
                GridPos seat = options.SourcePosition(layout, row, col);
                occupants.TryGetValue(seat, out Person? person);
                CellType type = layout.TypeAt(seat);
                Vector2 min = grid + new Vector2(col * cell.X, row * cell.Y);
                draw.AddRectFilled(min, min + cell, Color(options.GetColor(seat, type, person)));
                draw.AddRect(min, min + cell, Color(options.BorderColor));
                DrawText(draw, min, cell, options.GetText(seat, type, person));
                if (selected == seat) draw.AddRect(min + Vector2.One, min + cell - Vector2.One, Color(new Vector4(.1f, .45f, .95f, 1)), 0, ImDrawFlags.None, UiScale.Pixels(3));
            }
        }
        ImGui.EndChild();
    }

    private void DrawText(ImDrawListPtr draw, Vector2 min, Vector2 size, string text)
    {
        if (text.Length == 0) return;
        Vector2 measured = ImGui.CalcTextSize(text);
        float scale = Math.Min(1f, Math.Min((size.X - UiScale.Pixels(8)) / Math.Max(1, measured.X), (size.Y - UiScale.Pixels(8)) / Math.Max(1, measured.Y)));
        draw.PushClipRect(min, min + size, true);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, min + (size - measured * scale) / 2, Color(options.TextColor), text);
        draw.PopClipRect();
    }

    private static void EditColor(string label, ref Vector4 color)
        => ImGui.ColorEdit4(label, ref color, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs);
    private static uint Color(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(ExportStrings.PathRequired);
        string full = Path.GetFullPath(value.Trim());
        if (Path.GetExtension(full).Length == 0) full += ".png";
        if (!string.Equals(Path.GetExtension(full), ".png", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(ExportStrings.PngOnly);
        return full;
    }

    private void Save(LayoutGrid layout, IReadOnlyDictionary<GridPos, Person> occupants)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            SeatingImageExporter.SavePng(path, layout, occupants, options);
            status = string.Format(ExportStrings.Success, path);
        }
        catch (Exception exception)
        { status = string.Format(ExportStrings.Failure, exception.Message); }
    }
}
