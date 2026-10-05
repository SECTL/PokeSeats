using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;

namespace PokeSeats.Core.Solver;

/// <summary>A person-to-seat variable domain shared by all solver backends.</summary>
public sealed record SolverSeatVariable(PersonId Person, string Name);

/// <summary>One allowed assignment for a binary relation.</summary>
public readonly record struct AllowedSeatPair(int A, int B);

/// <summary>Positive support for one unary rule, before applying polarity.</summary>
public sealed record UnaryRuleExpansion(
    RuleId RuleId,
    PersonId Person,
    RulePolarity Polarity,
    IReadOnlyList<int> MatchingSeats);

/// <summary>Positive support for one binary relation, before applying polarity.</summary>
public sealed record RelationRuleExpansion(
    RuleId RuleId,
    PersonId A,
    PersonId B,
    RulePolarity Polarity,
    IReadOnlyList<AllowedSeatPair> MatchingPairs);

/// <summary>One row/column occupancy bound.</summary>
public sealed record CapacityRuleExpansion(
    RuleId RuleId,
    Axis Axis,
    int Index,
    int? Min,
    int? Max,
    IReadOnlyList<int> Seats);

/// <summary>
/// Backend-neutral expansion of the current IR rules.
/// Seat indices are stable layout.GetDeskCells() order, starting at zero.
/// Matching collections describe the positive predicate; Forbidden is applied by the backend.
/// </summary>
public sealed class RuleExpansionPlan
{
    public IReadOnlyList<GridPos> Seats { get; }
    public IReadOnlyDictionary<PersonId, SolverSeatVariable> Variables { get; }
    public IReadOnlyList<UnaryRuleExpansion> UnaryRules { get; }
    public IReadOnlyList<RelationRuleExpansion> RelationRules { get; }
    public IReadOnlyList<CapacityRuleExpansion> CapacityRules { get; }

    private RuleExpansionPlan(
        IReadOnlyList<GridPos> seats,
        IReadOnlyDictionary<PersonId, SolverSeatVariable> variables,
        IReadOnlyList<UnaryRuleExpansion> unaryRules,
        IReadOnlyList<RelationRuleExpansion> relationRules,
        IReadOnlyList<CapacityRuleExpansion> capacityRules)
    {
        Seats = seats;
        Variables = variables;
        UnaryRules = unaryRules;
        RelationRules = relationRules;
        CapacityRules = capacityRules;
    }

    public static RuleExpansionPlan Build(
        LayoutGrid layout,
        IReadOnlyCollection<Person> roster,
        IEnumerable<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(rules);

        var seats = layout.GetDeskCells().ToArray();
        var variables = roster
            .Select(person => new { person.Id, Variable = new SolverSeatVariable(person.Id, VariableName(person.Id)) })
            .ToDictionary(item => item.Id, item => item.Variable);
        var support = new Analyzer.RuleSupport(layout);
        var unary = new List<UnaryRuleExpansion>();
        var relations = new List<RelationRuleExpansion>();
        var capacities = new List<CapacityRuleExpansion>();

        foreach (var rule in rules)
        {
            switch (rule)
            {
                case PinnedRule pinned:
                    unary.Add(new UnaryRuleExpansion(
                        pinned.Id,
                        pinned.Person,
                        RulePolarity.Required,
                        support.GetPositions(pinned).Select(pos => indexOf(seats, pos)).ToArray()));
                    break;
                case RegionRule region:
                    unary.Add(new UnaryRuleExpansion(
                        region.Id,
                        region.Person,
                        region.Polarity,
                        support.GetPositions(region).Select(pos => indexOf(seats, pos)).ToArray()));
                    break;
                case RelationRule relation:
                    relations.Add(new RelationRuleExpansion(
                        relation.Id,
                        relation.A,
                        relation.B,
                        relation.Polarity,
                        support.GetPairs(relation)
                            .Select(pair => new AllowedSeatPair(indexOf(seats, pair.A), indexOf(seats, pair.B)))
                            .ToArray()));
                    break;
                case CapacityRule capacity:
                    capacities.AddRange(expandCapacity(capacity, seats));
                    break;
                default:
                    throw new NotSupportedException($"不支持的规则类型：{rule.GetType().Name}");
            }
        }

        return new RuleExpansionPlan(seats, variables, unary, relations, capacities);
    }

    public static string VariableName(PersonId id) => $"person_{id.Value:N}";

    private static int indexOf(IReadOnlyList<GridPos> seats, GridPos position)
    {
        for (var i = 0; i < seats.Count; i++)
            if (seats[i] == position)
                return i;
        throw new InvalidOperationException($"支持集包含不在 Desk 域中的位置：{position}");
    }

    private static IEnumerable<CapacityRuleExpansion> expandCapacity(CapacityRule rule, IReadOnlyList<GridPos> seats)
    {
        var grouped = rule.Axis.Type switch
        {
            AxisType.Row => seats.Select((position, index) => (position, index)).Where(item => item.position.Row == rule.Axis.Index),
            AxisType.Column => seats.Select((position, index) => (position, index)).Where(item => item.position.Col == rule.Axis.Index),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule.Axis.Type, "未知轴类型。")
        };
        yield return new CapacityRuleExpansion(rule.Id, rule.Axis, rule.Axis.Index, rule.Min, rule.Max, grouped.Select(item => item.index).ToArray());
    }
}
