using System.Text.Json;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>Editable classroom grid owned by the payload plugin.</summary>
internal sealed class LayoutDocument
{
    private const int DefaultRows = 12;
    private const int DefaultCols = 16;
    private const int MinimumDimension = 1;
    private const int MaximumDimension = 32;

    private CellType[,] _cells;

    private LayoutDocument(int rows, int cols)
    {
        _cells = CreateEmptyCells(rows, cols);
    }

    public int Rows => _cells.GetLength(0);
    public int DeskCount
    {
        get
        {
            int count = 0;
            for (int row = 0; row < Rows; row++)
                for (int col = 0; col < Cols; col++)
                    if (_cells[row, col] == CellType.Desk)
                        count++;
            return count;
        }
    }
    public int Cols => _cells.GetLength(1);
    public int Revision { get; private set; }

    public static LayoutDocument CreateDefault() => new(DefaultRows, DefaultCols);

    public CellType TypeAt(int row, int col)
        => row >= 0 && row < Rows && col >= 0 && col < Cols
            ? _cells[row, col]
            : CellType.OutOfBounds;

    public void Resize(int rows, int cols)
    {
        ValidateDimension(rows, nameof(rows));
        ValidateDimension(cols, nameof(cols));

        var resized = CreateEmptyCells(rows, cols);
        for (int row = 0; row < Math.Min(rows, Rows); row++)
            for (int col = 0; col < Math.Min(cols, Cols); col++)
                resized[row, col] = _cells[row, col];
        _cells = resized;
        Revision++;
    }

    public void Clear()
    {
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Cols; col++)
                _cells[row, col] = CellType.Empty;
        Revision++;
    }

    public void SetRectangle(GridPos first, GridPos last, CellType type)
    {
        if (type is < CellType.Desk or > CellType.Empty)
            throw new ArgumentOutOfRangeException(nameof(type));

        int rowMin = Math.Max(0, Math.Min(first.Row, last.Row));
        int rowMax = Math.Min(Rows - 1, Math.Max(first.Row, last.Row));
        int colMin = Math.Max(0, Math.Min(first.Col, last.Col));
        int colMax = Math.Min(Cols - 1, Math.Max(first.Col, last.Col));

        for (int row = rowMin; row <= rowMax; row++)
            for (int col = colMin; col <= colMax; col++)
                _cells[row, col] = type;
        Revision++;
    }

    public LayoutGrid ToGrid()
    {
        var copy = new CellType[Rows, Cols];
        Array.Copy(_cells, copy, _cells.Length);
        return new LayoutGrid(copy);
    }

    public static LayoutDocument LoadOrCreate(string path, out string? loadMessage)
    {
        loadMessage = null;
        if (!File.Exists(path))
            return CreateDefault();

        try
        {
            var stored = JsonSerializer.Deserialize<StoredLayout>(File.ReadAllText(path));
            if (stored is null)
                throw new InvalidDataException("教室布局文件内容为空。");
            ValidateDimension(stored.Rows, nameof(stored.Rows));
            ValidateDimension(stored.Cols, nameof(stored.Cols));
            if (stored.Cells is null || stored.Cells.Length != checked(stored.Rows * stored.Cols))
                throw new InvalidDataException("教室布局数据损坏：单元格总数与行列尺寸不匹配。");

            var document = new LayoutDocument(stored.Rows, stored.Cols);
            for (int row = 0; row < document.Rows; row++)
                for (int col = 0; col < document.Cols; col++)
                {
                    int value = stored.Cells[row * document.Cols + col];
                    if (value < (int)CellType.Desk || value > (int)CellType.Empty)
                        throw new InvalidDataException($"布局包含未识别的区域类型编号：{value}。");
                    document._cells[row, col] = (CellType)value;
                }
            return document;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OverflowException)
        {
            loadMessage = $"布局文件加载异常，已重置为空白教室：{exception.Message}";
            return CreateDefault();
        }
    }

    public void Save(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var cells = new int[checked(Rows * Cols)];
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < Cols; col++)
                cells[row * Cols + col] = (int)_cells[row, col];

        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(new StoredLayout(Rows, Cols, cells), new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, true);
    }

    private static CellType[,] CreateEmptyCells(int rows, int cols)
    {
        var cells = new CellType[rows, cols];
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
                cells[row, col] = CellType.Empty;
        return cells;
    }

    private static void ValidateDimension(int value, string parameterName)
    {
        if (value is < MinimumDimension or > MaximumDimension)
            throw new InvalidDataException($"教室{(parameterName == "Rows" ? "排数" : "列数")}必须在 {MinimumDimension} 至 {MaximumDimension} 之间。");
    }

    private sealed record StoredLayout(int Rows, int Cols, int[] Cells);
}
