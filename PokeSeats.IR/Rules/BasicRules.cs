using PokeSeats.IR.Base;

namespace PokeSeats.IR.Rules;

public abstract record Rule(RuleId Id, string DisplayName);

/// <summary>
/// A 相对于 B 的基础关系；Required 要求谓词为真，Forbidden 要求为假。
/// 方向只约束对应轴：上为前、下为后；SameRow/SameColumn 锁定相应轴。
/// 除 ExactOffset 外，还要求加权图距离不超过 Distance；null 取消距离及连通性限制。
/// 不同人的合法座位必须互异；同一人的关系在 (p,p) 上判断。
/// </summary>
public sealed record RelationRule(
    RuleId         Id,
    string         DisplayName,
    PersonId       A,
    PersonId       B,
    RelationKind   Kind,
    RulePolarity   Polarity,
    DistanceMetric Metric          = DistanceMetric.Manhattan,
    int?           Distance        = 1, // 加权距离上限；null 不限，负值空支持；ExactOffset 忽略此值
    int            OffsetDx        = 0, // ExactOffset: A.Col = B.Col + OffsetDx（右为正）
    int            OffsetDy        = 0  // ExactOffset: A.Row = B.Row + OffsetDy（下为正）
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

