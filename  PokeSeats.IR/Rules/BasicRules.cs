using PokeSeats.IR.Base;

namespace PokeSeats.IR.Rules;

public abstract record Rule(RuleId Id, string DisplayName);

public sealed record RelationRule(
    RuleId         Id,
    string         DisplayName,
    PersonId       A,
    PersonId       B,
    RelationKind   Kind,
    RulePolarity   Polarity,
    DistanceMetric Metric          = DistanceMetric.Manhattan,
    int?           Distance        = 1, // null = 不限距离，仅方向约束
    int            OffsetDx        = 0,
    int            OffsetDy        = 0
) : Rule(Id, DisplayName);
public sealed record PinnedRule(
    RuleId   Id,
    string   DisplayName,
    PersonId Person,
    GridPos  Pos
) : Rule(Id, DisplayName);

public sealed record CapacityRule(
    RuleId   Id,
    string   DisplayName,
    Axis Axis,
    int?     Min,
    int?     Max
) : Rule(Id, DisplayName);

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

