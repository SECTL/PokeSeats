namespace PokeSeats.IR.Base;

public enum CellType
{
    Desk,
    Aisle,
    NarrowAisle,
    Podium,
    Empty,
    OutOfBounds
}

public sealed class LayoutGrid
{
    private CellType[,] cells;

    public int Rows => cells.GetLength(0);
    public int Cols => cells.GetLength(1);

    public LayoutGrid(CellType[,] cells) => this.cells = cells;

    public static LayoutGrid Blank(int rows, int cols) => new(new CellType[rows, cols]);

    public CellType TypeAt(GridPos p) =>
        p.Row >= 0 && p.Row < Rows && p.Col >= 0 && p.Col < Cols
            ? cells[p.Row, p.Col]
            : CellType.OutOfBounds;

    public bool IsDesk(GridPos p) => TypeAt(p) == CellType.Desk;

    public void ReplaceWith(CellType[,] newCells) => cells = newCells;

    public IEnumerable<GridPos> GetDeskCells(
        int? rowMin = null,
        int? rowMax = null,
        int? colMin = null,
        int? colMax = null
    )
    {
        int rMin = rowMin ?? 0, rMax = rowMax ?? Rows - 1;
        int cMin = colMin ?? 0, cMax = colMax ?? Cols - 1;

        if (rMin > rMax || cMin > cMax) yield break;

        for (int r = rMin; r <= rMax; r++)
            for (int c = cMin; c <= cMax; c++)
                if (cells[r, c] == CellType.Desk)
                    yield return new GridPos(r, c);
    }

    public int GetDeskCount(
        int? rowMin = null,
        int? rowMax = null,
        int? colMin = null,
        int? colMax = null
    ) => GetDeskCells(rowMin, rowMax, colMin, colMax).Count();
}
