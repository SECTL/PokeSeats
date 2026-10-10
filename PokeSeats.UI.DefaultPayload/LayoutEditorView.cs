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
    private const string ResizePopupId = "缩小布局###layout-resize-confirm";
    private float _cellSize = 36f;
    private float _gridLineWidth = 1f;

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

    public string DisplayName => "教室布局";

    public Vector2? MinimumSize => new(620, 520);

    public void DrawContent(float deltaSeconds)
    {
        _cellSize = UiScale.Pixels(36f);
        _gridLineWidth = UiScale.Pixels(1f);
        DrawToolbar();
        ImGui.Separator();
        DrawCanvas();
        DrawResizeConfirmation();
    }

    private void DrawToolbar()
    {
        ImGui.Text("布局尺寸");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(100));
        ImGui.InputInt("行##layout-rows", ref _requestedRows, 1, 1);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(100));
        ImGui.InputInt("列##layout-cols", ref _requestedCols, 1, 1);
        ImGui.SameLine();
        if (ImGui.Button("应用尺寸"))
            TryResize();
        ImGui.SameLine();
        if (ImGui.Button("清空布局"))
        {
            _layout.Clear();
            ClearSelection();
            SaveLayout("布局已清空并保存。");
        }
        ImGui.SameLine();
        if (ImGui.Button("保存"))
            SaveLayout("布局已保存。");

        ImGui.TextDisabled($"{_layout.Rows} × {_layout.Cols}，座位 {_layout.DeskCount} 个");
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
        drawList.AddRectFilled(origin, backgroundEnd, Color(0.12f, 0.14f, 0.17f, 1f));
        Vector2 inset = new(_gridLineWidth);

        for (int row = 0; row < _layout.Rows; row++)
            for (int col = 0; col < _layout.Cols; col++)
            {
                Vector2 topLeft = CellTopLeft(origin, row, col);
                Vector2 bottomRight = topLeft + new Vector2(_cellSize, _cellSize);
                CellType type = _layout.TypeAt(row, col);
                drawList.AddRectFilled(topLeft + inset, bottomRight - inset, ColorFor(type));
                drawList.AddRect(topLeft, bottomRight, Color(0.30f, 0.34f, 0.40f, 1f), 0, ImDrawFlags.None, _gridLineWidth);
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
        drawList.AddRectFilled(topLeft, bottomRight, Color(0.20f, 0.55f, 0.95f, 0.25f));
        Vector2 inset = new(_gridLineWidth);
        drawList.AddRect(topLeft + inset, bottomRight - inset, Color(0.35f, 0.75f, 1f, 1f), 0, ImDrawFlags.None, _gridLineWidth * 2f);
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
                drawList.AddText(topLeft + (new Vector2(_cellSize, _cellSize) - textSize) / 2f, Color(0.95f, 0.97f, 1f, 1f), label);
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

        ImGui.Text("选择区域类型");
        ImGui.Separator();
        DrawTypeMenuItem("桌子 / 座位", CellType.Desk);
        DrawTypeMenuItem("宽过道", CellType.Aisle);
        DrawTypeMenuItem("窄过道", CellType.NarrowAisle);
        DrawTypeMenuItem("讲台", CellType.Podium);
        DrawTypeMenuItem("空白", CellType.Empty);
        ImGui.Separator();
        if (ImGui.MenuItem("取消"))
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
            SaveLayout($"已设置 {SelectedCellCount(start, end)} 个格子：{label}。");
        }
        ClearSelection();
    }

    private void TryResize(bool confirmed = false)
    {
        try
        {
            if (_requestedRows is < 1 or > 32 || _requestedCols is < 1 or > 32)
                throw new InvalidDataException("行和列必须在 1 到 32 之间。");
            if (!confirmed && (_requestedRows < _layout.Rows || _requestedCols < _layout.Cols))
            {
                _requestResizeConfirmation = true;
                return;
            }
            _layout.Resize(_requestedRows, _requestedCols);
            ClearSelection();
            SaveLayout($"布局已调整为 {_layout.Rows} × {_layout.Cols}。");
        }
        catch (InvalidDataException exception)
        {
            _requestedRows = _layout.Rows;
            _requestedCols = _layout.Cols;
            _status = $"尺寸无效：{exception.Message}";
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
        ImGui.Text($"缩小到 {_requestedRows} × {_requestedCols}？");
        ImGui.Text("超出新边界的格子将被删除。");
        if (ImGui.Button("确认缩小"))
        {
            TryResize(confirmed: true);
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button("取消") || ImGui.IsKeyPressed(ImGuiKey.Escape))
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
            _status = $"布局保存失败：{exception.Message}";
        }
        catch (UnauthorizedAccessException exception)
        {
            _status = $"布局保存失败：{exception.Message}";
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
            CellType.Desk => "桌",
            CellType.Aisle => "宽",
            CellType.NarrowAisle => "窄",
            CellType.Podium => "讲",
            _ => null
        };

    private static uint ColorFor(CellType type)
        => type switch
        {
            CellType.Desk => Color(0.16f, 0.40f, 0.66f, 1f),
            CellType.Aisle => Color(0.28f, 0.30f, 0.34f, 1f),
            CellType.NarrowAisle => Color(0.55f, 0.39f, 0.16f, 1f),
            CellType.Podium => Color(0.47f, 0.22f, 0.50f, 1f),
            _ => Color(0.12f, 0.14f, 0.17f, 1f)
        };

    private static uint Color(float r, float g, float b, float a)
        => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, a));
}
