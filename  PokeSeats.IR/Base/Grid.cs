namespace PokeSeats.IR.Base;

public readonly record struct GridPos(int Row, int Col)
{
    public int DeltaRowTo(GridPos other) => other.Row - Row;
    public int DeltaColTo(GridPos other) => other.Col - Col;

    public int ChebyshevTo(GridPos other) =>
        Math.Max(Math.Abs(DeltaRowTo(other)), Math.Abs(DeltaColTo(other)));

    public bool IsAdjacentTo(GridPos other) => this != other && ChebyshevTo(other) <= 1;

    public static GridPos operator +(GridPos p, (int dr, int dc) d) => new(p.Row + d.dr, p.Col + d.dc);
}

public enum AxisType
{
    Row,
    Column
}
