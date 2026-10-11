using PokeSeats.Core.Graphing;
using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;

namespace PokeSeats.Core.Analyzer;

/// <summary>
/// 规则正向基础支持（Positive/Base Support）的统计结果。
/// </summary>
/// <param name="UniverseCount">合法指派总数；用 long 计数，大布局下不会因乘法/累加溢出。</param>
/// <param name="MatchingCount">
/// 合法指派中基础谓词为真的数量。基础谓词与规则极性无关（Forbidden 同样统计"谓词成立"的指派），
/// 由调用方（如分析器）结合极性判读为 AlwaysTrue/AlwaysFalse。
/// </param>
public readonly record struct RuleSupportSummary(long UniverseCount, long MatchingCount);

/// <summary>
/// 在布局的全部合法指派上枚举/统计规则的正向基础支持。
/// </summary>
/// <remarks>
/// <para>
/// 支持宇宙：一元规则（<see cref="PinnedRule"/>、<see cref="RegionRule"/>）为全部 Desk 座位坐标；
/// 关系规则在 A、B 为不同人时为互异座位的有序对，为同一人时仅 (p, p) 对角线。
/// 基础谓词不区分极性：<see cref="RulePolarity.Forbidden"/> 的规则同样返回"关系/区域成立"的指派；
/// 空宇宙（没有座位、或互异有序对不存在）表示没有任何合法指派。
/// </para>
/// <para>
/// Proximity 且 Distance=1 使用 Adjacency 的座位邻接，包含单格正交窄过道桥接，不含自座位。
/// 其他情况保留以下距离语义。
/// 距离语义（<see cref="RelationRule.Distance"/>）：加权图距离的上界（≤ d），
/// 普通邻接边权重 1、窄过道桥边权重 2，宽过道不建桥边；<c>null</c> 表示既不限距离也不要求连通；
/// 负值表示空支持。<see cref="RelationKind.ExactOffset"/> 不使用距离，只按 OffsetDx/OffsetDy 比较
/// （用 long 计算，避免 int 溢出）。方向量（LeftOf/RightOf/InFrontOf/Behind）只约束自己的轴，
/// 严格不等式在自指（A == B）时为假；SameRow/SameColumn 为等值约束。
/// </para>
/// <para>
/// 生命周期：构造时对 <see cref="LayoutGrid"/> 做快照（深拷贝格子并缓存按 (Row, Col) 升序的座位表），
/// 之后对原布局的修改（如 <see cref="LayoutGrid.ReplaceWith"/>）不会影响本实例，
/// 也不会出现"座位表新、距离图旧"的错配。
/// 各度量的 <see cref="GraphDistanceOracle"/> 惰性构造并缓存：只有真正需要图距离时才按度量建图；
/// 预言机内部按源点缓存最短路，查询源点越多内存占用越大。
/// 本类除惰性缓存外不可变；惰性缓存未加锁，不是线程安全的，分析应在单线程内完成。
/// </para>
/// </remarks>
public sealed class RuleSupport
{
    private readonly LayoutGrid                                  layout;
    private readonly GridPos[]                                   desks;
    private readonly Dictionary<DistanceMetric, IDistanceOracle> oracles = new();

    /// <summary>
    /// 在 <paramref name="layout"/> 的快照上建立支持计算器。
    /// </summary>
    /// <param name="layout">布局；构造后调用方对它的修改不影响本实例。</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> 为 null。</exception>
    public RuleSupport(LayoutGrid layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        // 快照：深拷贝格子，后续所有枚举与预言机都基于同一份数据。
        var cells = new CellType[layout.Rows, layout.Cols];
        for (int r = 0; r < layout.Rows; r++)
            for (int c = 0; c < layout.Cols; c++)
                cells[r, c] = layout.TypeAt(new GridPos(r, c));

        this.layout = new LayoutGrid(cells);
        // GetDeskCells 按行、列升序产出，天然满足确定性顺序，无需再排序。
        desks = this.layout.GetDeskCells().ToArray();
    }

    /// <summary>
    /// 统计规则的基础支持：接受 <see cref="PinnedRule"/>、<see cref="RegionRule"/> 与 <see cref="RelationRule"/>。
    /// </summary>
    /// <remarks>
    /// MatchingCount 是"基础谓词为真"的合法指派数，与极性无关。Pinned 的谓词是位置等于固定坐标
    /// （该坐标不是 Desk 时为 0）；Region 的谓词是落在闭区间内（null 无界，反向边界为空）；
    /// 关系的谓词是方向/轴/偏移约束（ExactOffset 除外）再叠加距离上界。
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// <see cref="CapacityRule"/> 是独立的计数检查（与位置支持无关），不在这里支持；
    /// 其他未知规则类型同样抛出。
    /// </exception>
    public RuleSupportSummary Summarize(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule switch
        {
            PinnedRule pinned => new RuleSupportSummary(desks.Length, countPositions(pinned)),
            RegionRule region => new RuleSupportSummary(desks.Length, countPositions(region)),
            RelationRule relation => new RuleSupportSummary(pairUniverseCount(relation), countMatchingPairs(relation)),
            CapacityRule => throw new NotSupportedException(
                "CapacityRule 由独立的容量检查处理，不属于位置支持计算。"),
            _ => throw new NotSupportedException($"不支持的规则类型：{rule.GetType().Name}"),
        };
    }

    /// <summary>
    /// 一元规则的合法位置枚举（正向基础支持），仅接受 <see cref="PinnedRule"/> 与 <see cref="RegionRule"/>。
    /// </summary>
    /// <remarks>
    /// Pinned 返回其固定坐标（不是 Desk 时为空）；Region 返回落在闭区间内的座位（null 无界，反向边界为空）。
    /// 结果与极性无关，按 (Row, Col) 升序且无重复。
    /// </remarks>
    /// <exception cref="NotSupportedException">规则不是 PinnedRule/RegionRule。</exception>
    public IEnumerable<GridPos> GetPositions(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return rule switch
        {
            PinnedRule or RegionRule => enumeratePositions(rule),
            _ => throw new NotSupportedException($"仅支持 PinnedRule/RegionRule：{rule.GetType().Name}"),
        };
    }

    /// <summary>
    /// 关系的合法有序对枚举（正向基础支持），接受任意 <see cref="RelationKind"/>，与极性无关。
    /// </summary>
    /// <remarks>
    /// A、B 为不同人时枚举互异座位的有序对；为同一人时只枚举 (p, p) 对角线。
    /// 顺序确定：先按 A 的 (Row, Col) 升序，再按 B 的 (Row, Col) 升序。
    /// 距离为 null 时不限距离也不要求连通；为负时为空集；ExactOffset 不使用距离。
    /// </remarks>
    public IEnumerable<(GridPos A, GridPos B)> GetPairs(RelationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return enumerateMatchingPairs(rule);
    }

    // ---------------- 共享基础谓词（枚举与统计必须走同一份实现） ----------------

    private bool matches(Rule rule, GridPos p) => rule switch
    {
        PinnedRule pinned => p == pinned.Pos,
        RegionRule region => matchesRegion(region, p),
        _ => throw new NotSupportedException($"不支持的规则类型：{rule.GetType().Name}"),
    };

    private static bool matchesRegion(RegionRule rule, GridPos p)
    {
        // 闭区间、可空无界；反向区间为空。只在实际座位上判断，不做 clamp。
        if (rule.RowMin is int rowMin && p.Row < rowMin) return false;
        if (rule.RowMax is int rowMax && p.Row > rowMax) return false;
        if (rule.ColMin is int colMin && p.Col < colMin) return false;
        if (rule.ColMax is int colMax && p.Col > colMax) return false;
        return true;
    }

    private bool matches(RelationRule rule, GridPos a, GridPos b) => rule.Kind switch
    {
        RelationKind.Proximity => rule.Distance == 1
            ? AdjacencyGeometry.Matches(rule.Adjacency, layout, a, b)
            : distanceSatisfied(rule, a, b),
        RelationKind.LeftOf => a.Col < b.Col && distanceSatisfied(rule, a, b),
        RelationKind.RightOf => a.Col > b.Col && distanceSatisfied(rule, a, b),
        RelationKind.InFrontOf => a.Row < b.Row && distanceSatisfied(rule, a, b),
        RelationKind.Behind => a.Row > b.Row && distanceSatisfied(rule, a, b),
        RelationKind.SameRow => a.Row == b.Row && distanceSatisfied(rule, a, b),
        RelationKind.SameColumn => a.Col == b.Col && distanceSatisfied(rule, a, b),
        // 精确偏移不使用距离；两侧提升为 long，避免 int 加法及 int.MinValue 溢出。
        RelationKind.ExactOffset => a.Col == (long)b.Col + rule.OffsetDx
                                 && a.Row == (long)b.Row + rule.OffsetDy,
        _ => throw new NotSupportedException($"不支持的 RelationKind：{rule.Kind}"),
    };

    private bool distanceSatisfied(RelationRule rule, GridPos a, GridPos b)
    {
        if (rule.Distance is not int distance)
            return true; // null：不限距离，也不要求连通

        if (distance < 0)
            return false; // 负距离：空支持

        if (a == b)
            return true; // 自距离恒为 0，不必构造图或运行最短路。

        int? weighted = oracleFor(rule.Metric).Distance(a, b);
        return weighted is int value && value <= distance;
    }

    // ---------------- 共享枚举 ----------------

    private IEnumerable<GridPos> enumeratePositions(Rule rule)
    {
        foreach (var p in desks)
            if (matches(rule, p))
                yield return p;
    }

    private IEnumerable<(GridPos A, GridPos B)> enumeratePairs(RelationRule rule)
    {
        bool self = rule.A == rule.B;

        foreach (var a in desks)
        {
            if (self)
            {
                // 自指：宇宙只含对角线 (p, p)。
                yield return (a, a);
                continue;
            }

            // 不同人：宇宙是互异座位的有序对。
            foreach (var b in desks)
                if (b != a)
                    yield return (a, b);
        }
    }

    private IEnumerable<(GridPos A, GridPos B)> enumerateMatchingPairs(RelationRule rule)
    {
        foreach (var (a, b) in enumeratePairs(rule))
            if (matches(rule, a, b))
                yield return (a, b);
    }

    // ---------------- 统计（惰性枚举累加，不落地全量对列表） ----------------

    private long countPositions(Rule rule)
    {
        long count = 0;
        foreach (var _ in enumeratePositions(rule))
            count++;
        return count;
    }

    private long countMatchingPairs(RelationRule rule)
    {
        // 自关联的谓词在所有 (p,p) 上结果相同，复用同一谓词即可 O(1) 分类。
        if (rule.A == rule.B)
            return desks.Length != 0 && matches(rule, desks[0], desks[0]) ? desks.Length : 0;

        long count = 0;
        foreach (var (a, b) in enumeratePairs(rule))
            if (matches(rule, a, b))
                count++;
        return count;
    }

    private long pairUniverseCount(RelationRule rule)
    {
        long n = desks.Length;
        return rule.A == rule.B ? n : n * (n - 1);
    }

    /// <summary>按度量惰性构造并缓存图距离预言机；都基于构造时的布局快照。</summary>
    private IDistanceOracle oracleFor(DistanceMetric metric)
    {
        if (!oracles.TryGetValue(metric, out var oracle))
        {
            oracle = new GraphDistanceOracle(AdjacencyGraph.Build(layout, metric));
            oracles[metric] = oracle;
        }

        return oracle;
    }
}
