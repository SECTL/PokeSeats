using PokeSeats.IR.Base;

namespace PokeSeats.Core.Graphing;

/// <summary>
/// <see cref="AdjacencyGraph"/> 上的最短路预言机：跳数距离与半径球。
/// 图是无权、无向的（<see cref="DistanceMetric"/> 已经体现在边的构造里），因此距离就是 BFS 层数。
/// 图节点即座位；不在图中的坐标（过道、讲台、越界）不是可查询的位置，一律视为不可达。
/// </summary>
/// <remarks>
/// 邻接表在构造时压成 CSR（扁平 int 索引），因此每个 BFS 步只做索引算术，不再访问字典或 List。
/// 单源距离按需计算并缓存：图无向，所以一个方向的缓存能同时回答两个方向。
/// 缓存不设上限——查询的不同源点越多，内存越接近 O(N²)，这是调用方的选择。
/// </remarks>
public sealed class GraphDistanceOracle : IDistanceOracle
{
    private const int Unreachable = -1;

    private readonly GridPos[]                _nodes;   // index -> 坐标
    private readonly Dictionary<GridPos, int> _index;   // 坐标 -> index
    private readonly int[]                    _offsets; // CSR 行起止：节点 u 的边在 [_offsets[u], _offsets[u+1])
    private readonly int[]                    _targets; // CSR 列：邻居的 index

    private readonly Dictionary<int, int[]> _bySource = new();

    /// <param name="graph">
    /// 邻接表必须闭合——每条边的两个端点都要是 <see cref="AdjacencyGraph.Neighbors"/> 的键，
    /// <see cref="AdjacencyGraph.Build"/> 已经保证这一点。
    /// </param>
    public GraphDistanceOracle(AdjacencyGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        // 固定节点顺序，使 Ball 的输出只取决于集合本身，与字典的枚举顺序无关。
        _nodes = graph.Neighbors.Keys
            .OrderBy(p => p.Row)
            .ThenBy(p => p.Col)
            .ToArray();

        _index = new Dictionary<GridPos, int>(_nodes.Length);
        for (int i = 0; i < _nodes.Length; i++)
            _index[_nodes[i]] = i;

        _offsets = new int[_nodes.Length + 1];
        for (int i = 0; i < _nodes.Length; i++)
            _offsets[i + 1] = _offsets[i] + graph.Neighbors[_nodes[i]].Count;

        _targets = new int[_offsets[_nodes.Length]];
        for (int i = 0; i < _nodes.Length; i++)
        {
            int at = _offsets[i];
            foreach (var neighbor in graph.Neighbors[_nodes[i]])
                _targets[at++] = _index[neighbor];
        }
    }

    public int? Distance(GridPos p, GridPos q)
    {
        if (!_index.TryGetValue(p, out int from) || !_index.TryGetValue(q, out int to))
            return null;

        int[] distances;
        int lookup = to;
        if (!_bySource.TryGetValue(from, out distances!))
        {
            // 图无向：dist(p, q) == dist(q, p)，已有反向缓存时不必重算，但查的是 from。
            if (_bySource.TryGetValue(to, out distances!))
                lookup = from;
            else
                distances = DistancesFrom(from);
        }

        int distance = distances[lookup];
        return distance == Unreachable ? null : distance;
    }

    public IEnumerable<GridPos> Ball(GridPos center, int radius) => BallCore(new[] { center }, radius);

    public IEnumerable<GridPos> Ball(IReadOnlySet<GridPos> centers, int radius) => BallCore(centers, radius);

    /// <summary>
    /// 闭球：与任一源点的图距离 ≤ <paramref name="radius"/> 的全部座位。
    /// 一次多源 BFS 就得到各单源球的并集。结果按 (Row, Col) 排序并立即求值，
    /// 返回的序列不持有任何可变的共享状态。
    /// </summary>
    private GridPos[] BallCore(IEnumerable<GridPos> centers, int radius)
    {
        ArgumentNullException.ThrowIfNull(centers);

        if (radius < 0)
            return Array.Empty<GridPos>();

        var visited = new bool[_nodes.Length];
        var frontier = new List<int>();

        foreach (var center in centers)
        {
            if (_index.TryGetValue(center, out int seed) && !visited[seed])
            {
                visited[seed] = true;
                frontier.Add(seed);
            }
        }

        var ball = new List<GridPos>();
        for (int level = 0; frontier.Count > 0; level++)
        {
            foreach (int u in frontier)
                ball.Add(_nodes[u]);

            if (level == radius)
                break; // 半径处的节点不再扩展，免去一整层无用功

            var next = new List<int>();
            foreach (int u in frontier)
            {
                for (int e = _offsets[u]; e < _offsets[u + 1]; e++)
                {
                    int v = _targets[e];
                    if (!visited[v])
                    {
                        visited[v] = true;
                        next.Add(v);
                    }
                }
            }
            frontier = next;
        }

        ball.Sort(static (a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));
        return ball.ToArray();
    }

    /// <summary>从 <paramref name="source"/> 出发的完整 BFS 层数；不可达为 <see cref="Unreachable"/>。</summary>
    private int[] DistancesFrom(int source)
    {
        var distances = new int[_nodes.Length];
        Array.Fill(distances, Unreachable);
        distances[source] = 0;

        var queue = new Queue<int>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            int u = queue.Dequeue();
            int next = distances[u] + 1;
            for (int e = _offsets[u]; e < _offsets[u + 1]; e++)
            {
                int v = _targets[e];
                if (distances[v] == Unreachable)
                {
                    distances[v] = next;
                    queue.Enqueue(v);
                }
            }
        }

        _bySource[source] = distances;
        return distances;
    }
}
