namespace PokeSeats.IR.Base;

public enum CellType
{
    Desk,
    Aisle,
    NarrowAisle,
    Podium,
    OutOfBounds
}

public class LayoutGrid
{
    private CellType[,] cells;

    public int Rows => cells.GetLength(0);
    public int Cols => cells.GetLength(1);

    public LayoutGrid(int r, int c) => cells = new CellType[r, c];


}
