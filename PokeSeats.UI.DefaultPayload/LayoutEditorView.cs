using System.Numerics;
using ImGuiNET;
using PokeSeats.IR.Base;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>
/// Paint-style classroom layout editor. The grid is rendered with ImDrawList rather than an
/// ImGui table so a drag selects one rectangular region and a popup assigns its cell type.
/// </summary>
internal sealed class LayoutEditorView : IUiView
{
    private const string TypePopupId = "##layout-cell-type";
    private static readonly string ResizePopupId = PayloadStrings.LayoutResizeConfirmTitle + "###layout-resize-confirm";
    private float _cellSize = 36f;
    private float _gridLineWidth = 1f;
    private UiDesignTokens _designTokens = UiDesignTokens.Light;

    private readonly LayoutDocument _layout;
    private readonly string _storagePath;
    private string _status;
    private int _requestedRows;
    private int _requestedCols;
    private GridPos? _selectionStart;
    private GridPos? _selectionEnd;
    private bool _dragging;
    private bool _requestResizeConfirmation;

    public LayoutEditorView(LayoutDocument layout, string storagePath, string? loadMessage)
    {
        _layout = layout;
        _storagePath = storagePath;
        _requestedRows = layout.Rows;
        _requestedCols = layout.Cols;
        _status = loadMessage ?? string.Empty;
    }

    public string Id => "pokeseats.defaultpayload.layout";

    public string DisplayName => PayloadStrings.LayoutViewTitle;

    public Vector2? MinimumSize => new(620, 520);

    public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
    {
        _designTokens = designTokens;
        _cellSize = UiScale.Pixels(36f);
        _gridLineWidth = UiScale.Pixels(designTokens.Metrics.BorderWidth);
        DrawToolbar();
        ImGui.Separator();
        DrawCanvas();
        DrawResizeConfirmation();
    }

    private void DrawToolbar()
    {
        ImGui.Text(PayloadStrings.LayoutClassroomSpecLabel);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(100));
        ImGui.InputInt(PayloadStrings.LayoutRowInputLabel + "##layout-rows", ref _requestedRows, 1, 1);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(100));
        ImGui.InputInt(PayloadStrings.LayoutColumnInputLabel + "##layout-cols", ref _requestedCols, 1, 1);
        ImGui.SameLine();
        if (ImGui.Button(PayloadStrings.LayoutApplySizeButton))
            TryResize();
        ImGui.SameLine();
        if (ImGui.Button(PayloadStrings.LayoutClearButton))
        {
            _layout.Clear();
            ClearSelection();
            SaveLayout(PayloadStrings.LayoutResetStatus);
        }
        ImGui.SameLine();
        if (ImGui.Button(PayloadStrings.LayoutSaveButton))
            SaveLayout(PayloadStrings.LayoutSavedStatus);

        ImGui.TextDisabled(string.Format(PayloadStrings.LayoutCurrentSpecFormat, _layout.Rows, _layout.Cols, _layout.DeskCount));
        if (!string.IsNullOrEmpty(_status))
            ImGui.TextWrapped(_status);
    }

    private void DrawCanvas()
    {
        if (!ImGui.BeginChild("##layout-canvas", new Vector2(0, UiScale.Pixels(-1)), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        {
            ImGui.EndChild();
            return;
        }

        Vector2 canvasOrigin = ImGui.GetCursorScreenPos();
        Vector2 canvasSize = new(_layout.Cols * _cellSize, _layout.Rows * _cellSize);
        ImGui.InvisibleButton("##layout-surface", canvasSize);
        bool hovered = ImGui.IsItemHovered();
        Vector2 mouse = ImGui.GetIO().MousePos;

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            GridPos? cell = CellAt(ImGui.GetIO().MousePos, canvasOrigin);
            if (cell is { } rightClicked)
            {
                _selectionStart = rightClicked;
                _selectionEnd = rightClicked;
                _dragging = false;
                ImGui.OpenPopup(TypePopupId);
            }
        }

        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            GridPos? cell = CellAt(ImGui.GetIO().MousePos, canvasOrigin);
            if (cell is { } clicked)
            {
                _selectionStart = clicked;
                _selectionEnd = clicked;
                _dragging = true;
            }
        }

        if (_dragging)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                ClearSelection();
            else
            {
                int row = Math.Clamp((int)MathF.Floor((mouse.Y - canvasOrigin.Y) / _cellSize), 0, _layout.Rows - 1);
                int col = Math.Clamp((int)MathF.Floor((mouse.X - canvasOrigin.X) / _cellSize), 0, _layout.Cols - 1);
                _selectionEnd = new GridPos(row, col);
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    _dragging = false;
                    ImGui.OpenPopup(TypePopupId);
                }
                else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    ClearSelection();
            }
        }

        ImDrawListPtr drawList = ImGui.GetWindowDrawList();
        DrawGrid(drawList, canvasOrigin);
        DrawTypePopup();
        DrawSelection(drawList, canvasOrigin);
        DrawCellLabels(drawList, canvasOrigin);

        ImGui.EndChild();
    }

    private void DrawGrid(ImDrawListPtr drawList, Vector2 origin)
    {
        Vector2 backgroundEnd = origin + new Vector2(_layout.Cols * _cellSize, _layout.Rows * _cellSize);
        drawList.AddRectFilled(origin, backgroundEnd, Color(_designTokens.Colors.Canvas));
        Vector2 inset = new(_gridLineWidth);

        for (int row = 0; row < _layout.Rows; row++)
            for (int col = 0; col < _layout.Cols; col++)
            {
                Vector2 topLeft = CellTopLeft(origin, row, col);
                Vector2 bottomRight = topLeft + new Vector2(_cellSize, _cellSize);
                CellType type = _layout.TypeAt(row, col);
                drawList.AddRectFilled(topLeft + inset, bottomRight - inset, ColorFor(type));
                drawList.AddRect(topLeft, bottomRight, Color(_designTokens.Colors.Border), 0, ImDrawFlags.None, _gridLineWidth);
            }
    }

    private void DrawSelection(ImDrawListPtr drawList, Vector2 origin)
    {
        if (_selectionStart is not { } start || _selectionEnd is not { } end)
            return;

        int rowMin = Math.Min(start.Row, end.Row);
        int rowMax = Math.Max(start.Row, end.Row);
        int colMin = Math.Min(start.Col, end.Col);
        int colMax = Math.Max(start.Col, end.Col);
        Vector2 topLeft = CellTopLeft(origin, rowMin, colMin);
        Vector2 bottomRight = CellTopLeft(origin, rowMax + 1, colMax + 1);
        drawList.AddRectFilled(topLeft, bottomRight, Color(_designTokens.Colors.Selection));
        Vector2 inset = new(_gridLineWidth);
        drawList.AddRect(topLeft + inset, bottomRight - inset, Color(_designTokens.Colors.SelectionBorder), 0, ImDrawFlags.None, _gridLineWidth * 2f);
    }

    private void DrawCellLabels(ImDrawListPtr drawList, Vector2 origin)
    {
        for (int row = 0; row < _layout.Rows; row++)
            for (int col = 0; col < _layout.Cols; col++)
            {
                string? label = LabelFor(_layout.TypeAt(row, col));
                if (label is null)
                    continue;

                Vector2 topLeft = CellTopLeft(origin, row, col);
                Vector2 textSize = ImGui.CalcTextSize(label);
                drawList.AddText(topLeft + (new Vector2(_cellSize, _cellSize) - textSize) / 2f, Color(_designTokens.Colors.Text), label);
            }
    }

    private void DrawTypePopup()
    {
        if (!ImGui.BeginPopup(TypePopupId))
        {
            if (!_dragging && _selectionStart is not null)
                ClearSelection();
            return;
        }

        ImGui.Text(PayloadStrings.LayoutAssignTypeHeading);
        ImGui.Separator();
        DrawTypeMenuItem(PayloadStrings.LayoutCellTypeDesk, CellType.Desk);
        DrawTypeMenuItem(PayloadStrings.LayoutCellTypeAisle, CellType.Aisle);
        DrawTypeMenuItem(PayloadStrings.LayoutCellTypeNarrowAisle, CellType.NarrowAisle);
        DrawTypeMenuItem(PayloadStrings.LayoutCellTypePodium, CellType.Podium);
        DrawTypeMenuItem(PayloadStrings.LayoutCellTypeEmpty, CellType.Empty);
        ImGui.Separator();
        if (ImGui.MenuItem(PayloadStrings.CommonCancel))
            ClearSelection();
        ImGui.EndPopup();
    }

    private void DrawTypeMenuItem(string label, CellType type)
    {
        if (!ImGui.MenuItem(label))
            return;
        if (_selectionStart is { } start && _selectionEnd is { } end)
        {
            _layout.SetRectangle(start, end, type);
            SaveLayout(string.Format(PayloadStrings.LayoutAssignedStatusFormat, SelectedCellCount(start, end), label));
        }
        ClearSelection();
    }

    private void TryResize(bool confirmed = false)
    {
        try
        {
            if (_requestedRows is < 1 or > 32 || _requestedCols is < 1 or > 32)
                throw new InvalidDataException(PayloadStrings.LayoutDimensionRangeError);
            if (!confirmed && (_requestedRows < _layout.Rows || _requestedCols < _layout.Cols))
            {
                _requestResizeConfirmation = true;
                return;
            }
            _layout.Resize(_requestedRows, _requestedCols);
            ClearSelection();
            SaveLayout(string.Format(PayloadStrings.LayoutResizedStatusFormat, _layout.Rows, _layout.Cols));
        }
        catch (InvalidDataException exception)
        {
            _requestedRows = _layout.Rows;
            _requestedCols = _layout.Cols;
            _status = string.Format(PayloadStrings.LayoutSizeInvalidFormat, exception.Message);
        }
    }

    private void DrawResizeConfirmation()
    {
        if (_requestResizeConfirmation)
        {
            ImGui.OpenPopup(ResizePopupId);
            _requestResizeConfirmation = false;
        }
        if (!ImGui.BeginPopupModal(ResizePopupId, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.Text(string.Format(PayloadStrings.LayoutResizeConfirmQuestionFormat, _requestedRows, _requestedCols));
        ImGui.Text(PayloadStrings.LayoutResizeConfirmWarning);
        if (ImGui.Button(PayloadStrings.LayoutResizeConfirmButton))
        {
            TryResize(confirmed: true);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(PayloadStrings.CommonCancel) || ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            _requestedRows = _layout.Rows;
            _requestedCols = _layout.Cols;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void ClearSelection()
    {
        _selectionStart = null;
        _selectionEnd = null;
        _dragging = false;
    }

    private void SaveLayout(string status)
    {
        try
        {
            _layout.Save(_storagePath);
            _status = status;
        }
        catch (IOException exception)
        {
            _status = string.Format(PayloadStrings.LayoutSaveFailedFormat, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            _status = string.Format(PayloadStrings.LayoutSaveFailedFormat, exception.Message);
        }
    }

    private GridPos? CellAt(Vector2 mouse, Vector2 origin)
    {
        int col = (int)MathF.Floor((mouse.X - origin.X) / _cellSize);
        int row = (int)MathF.Floor((mouse.Y - origin.Y) / _cellSize);
        return row >= 0 && row < _layout.Rows && col >= 0 && col < _layout.Cols
            ? new GridPos(row, col)
            : null;
    }

    private Vector2 CellTopLeft(Vector2 origin, int row, int col)
        => origin + new Vector2(col * _cellSize, row * _cellSize);

    private static int SelectedCellCount(GridPos start, GridPos end)
        => (Math.Abs(end.Row - start.Row) + 1) * (Math.Abs(end.Col - start.Col) + 1);

    private static string? LabelFor(CellType type)
        => type switch
        {
            CellType.Desk => PayloadStrings.LayoutCellShortDesk,
            CellType.Aisle => PayloadStrings.LayoutCellShortAisle,
            CellType.NarrowAisle => PayloadStrings.LayoutCellShortNarrowAisle,
            CellType.Podium => PayloadStrings.LayoutCellShortPodium,
            _ => null
        };

    private uint ColorFor(CellType type)
        => type switch
        {
            CellType.Desk => Color(_designTokens.Colors.AccentSurface),
            CellType.Aisle => Color(_designTokens.Colors.Surface),
            CellType.NarrowAisle => Color(_designTokens.Colors.WarningSurface),
            CellType.Podium => Color(_designTokens.Colors.SecondarySurface),
            _ => Color(_designTokens.Colors.Canvas)
        };

    private static uint Color(Vector4 color)
        => ImGui.ColorConvertFloat4ToU32(color);
}
