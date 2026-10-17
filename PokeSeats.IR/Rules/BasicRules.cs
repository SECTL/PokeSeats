using PokeSeats.IR.Base;

namespace PokeSeats.IR.Rules;

public abstract record Rule(RuleId Id, string DisplayName);

/// <summary>
/// A 相对于 B 的基础关系；Required 要求谓词为真，Forbidden 要求谓词为假。
/// Proximity 且 Distance=1 使用 Adjacency 指定横向、四向或八向；横向模式下
/// AllowNarrowAisle 控制是否把只隔一条窄过道的课桌算作相邻。
/// 其他距离值保留加权图距离。
/// </summary>
/// <remarks>
/// 方向只约束对应轴：上为前、下为后；SameRow/SameColumn 锁定相应轴。
/// 不同人的合法座位必须互异；同一人的关系在 (p,p) 上判断。
/// </remarks>
public sealed record RelationRule(
    RuleId         Id,
    string         DisplayName,
    PersonId       A,
    PersonId       B,
    RelationKind   Kind,
    RulePolarity   Polarity,
    DistanceMetric Metric          = DistanceMetric.Manhattan,
    int?           Distance        = 1,
    int            OffsetDx        = 0,
    int            OffsetDy        = 0,
    AdjacencyMode  Adjacency       = AdjacencyMode.FourWay,
    bool           AllowNarrowAisle = true
) : Rule(Id, DisplayName);
public sealed record PinnedRule(
    RuleId   Id,
    string   DisplayName,
    PersonId Person,
    GridPos  Pos
) : Rule(Id, DisplayName);

/// <summary>指定行/列占用人数的闭区间限制；Min 与 Max 同时生效，null 表示该端不限。</summary>
public sealed record CapacityRule(
    RuleId   Id,
    string   DisplayName,
    Axis Axis,
    int?     Min,
    int?     Max
) : Rule(Id, DisplayName);

/// <summary>含端点的矩形区域；null 表示该方向不限，反向区间为空区域。</summary>
public sealed record RegionRule(
    RuleId       Id,
    string       DisplayName,
    PersonId     Person,
    RulePolarity Polarity,
    int?         RowMin = null,
    int?         RowMax = null,
    int?         ColMin = null,
    int?         ColMax = null
) : Rule(Id, DisplayName);

