using PokeSeats.IR.Base;

namespace PokeSeats.IR.Rules;

public abstract record Rule(RuleId Id, string DisplayName);

public sealed record RelationRule(
    RuleId       Id,
    string       DisplayName,
    PersonId     A,
    PersonId     B,
    RelationKind Kind,
    RulePolarity Polarity,
    int          OffsetDx   = 0,
    int          OffsetDy   = 0,
    bool         SameColumn = false
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
    AxisType Axis,
    int?     Min,
    int?     Max
) : Rule(Id, DisplayName);


