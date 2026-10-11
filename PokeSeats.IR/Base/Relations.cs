namespace PokeSeats.IR.Base;

/// <summary>相邻关系的几何范围：横向、四向或八向。</summary>
public enum AdjacencyMode
{
    Horizontal,
    FourWay,
    EightWay,
}

public static class AdjacencyGeometry
{
    /// <summary>Immediate neighbours in a relative grid without aisle cells.</summary>
    public static bool Matches(AdjacencyMode mode, long rowOffset, long columnOffset)
    {
        long row = Math.Abs(rowOffset);
        long col = Math.Abs(columnOffset);
        return mode switch
        {
            AdjacencyMode.Horizontal => row == 0 && col == 1,
            AdjacencyMode.FourWay => row + col == 1,
            AdjacencyMode.EightWay => Math.Max(row, col) == 1,
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    /// <summary>Desk neighbours, including a single orthogonal narrow-aisle bridge.</summary>
    public static bool Matches(AdjacencyMode mode, LayoutGrid layout, GridPos a, GridPos b)
    {
        if (!layout.IsDesk(a) || !layout.IsDesk(b))
            return false;

        long row = (long)a.Row - b.Row;
        long col = (long)a.Col - b.Col;
        if (Matches(mode, row, col))
            return true;

        bool bridge = mode == AdjacencyMode.Horizontal
            ? row == 0 && Math.Abs(col) == 2
            : row == 0 && Math.Abs(col) == 2 || col == 0 && Math.Abs(row) == 2;
        return bridge && layout.TypeAt(new GridPos(
            (int)(((long)a.Row + b.Row) / 2),
            (int)(((long)a.Col + b.Col) / 2))) == CellType.NarrowAisle;
    }
}

/// <summary>非相邻关系使用的座位距离度量。</summary>
public enum DistanceMetric
{
    Chebyshev,
    Manhattan,
};

public enum RelationKind
{
    Proximity,     // Distance=1 为严格相邻；其他值保留加权距离语义
    Behind, InFrontOf, LeftOf, RightOf,
    SameRow, SameColumn, // 轴锁定（"必须在同一排"）
    ExactOffset,         // 精确偏移
}

