using PokeSeats.IR.Base;

namespace PokeSeats.Core.Graphing;

public sealed class AdjacencyGraph
{
    public IReadOnlyDictionary<GridPos, List<GridPos>> Neighbors { get; }

    private AdjacencyGraph(IReadOnlyDictionary<GridPos, List<GridPos>> neighbors)
        => Neighbors = neighbors;

    // 只枚举"半方向"集合：每条无向边 {p,q} 恰好被生成一次（AddEdge 负责写入双向）。
    private static readonly (int Dr, int Dc)[] ChebyshevHalf =
        { (0, 1), (1, -1), (1, 0), (1, 1) };

    private static readonly (int Dr, int Dc)[] OrthoHalf =
        { (0, 1), (1, 0) };

    public static AdjacencyGraph Build(LayoutGrid layout, DistanceMetric metric)
    {
        var neighbors = new Dictionary<GridPos, List<GridPos>>();

        // 每个座位都是图中的节点（孤立座位同样保留，邻接表为空）
        foreach (var cell in layout.GetDeskCells())
            neighbors[cell] = new List<GridPos>();

        var dirs = metric == DistanceMetric.Chebyshev ? ChebyshevHalf : OrthoHalf;

        foreach (var d in layout.GetDeskCells())
        {
            // 紧邻：Manhattan 只连正交，Chebyshev 允许斜角
            foreach (var (dr, dc) in dirs)
            {
                var q = new GridPos(d.Row + dr, d.Col + dc);
                if (layout.IsDesk(q))
                    AddEdge(neighbors, d, q);
            }

            // 窄过道：允许跨越 2 格正交连通
            foreach (var (dr, dc) in OrthoHalf)
            {
                var mid = new GridPos(d.Row + dr,       d.Col + dc);
                var far = new GridPos(d.Row + 2 * dr,   d.Col + 2 * dc);
                if (layout.TypeAt(mid) == CellType.NarrowAisle && layout.IsDesk(far))
                    AddEdge(neighbors, d, far);
            }
        }

        return new AdjacencyGraph(neighbors);
    }

    /// <summary>
    /// 提交一条无向边 {p, q}：两个方向的邻接表都写入，因此 p、q 互为邻居。
    /// 调用方只需保证每条无向边恰好提交一次（见 Build 中的半方向枚举）。
    /// </summary>
    private static void AddEdge(Dictionary<GridPos, List<GridPos>> neighbors, GridPos p, GridPos q)
    {
        neighbors[p].Add(q);
        neighbors[q].Add(p);
    }
}
