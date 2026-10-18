using PokeSeats.IR.Base;

namespace PokeSeats.Core.Solver;

/// <summary>缺失键表示该学生的目标代价为零。</summary>
internal sealed record PersonObjectiveCost(IReadOnlyDictionary<PersonId, long> Costs, SeatObjectiveId ObjectiveId);
internal sealed record SeatObjectiveCost(IReadOnlyList<long> SeatWeights, SeatObjectiveId ObjectiveId);

internal static class ObjectiveExpander
{
    internal static (PersonObjectiveCost Person, SeatObjectiveCost Seat)[] Expand(
        RuleExpansionPlan plan, IReadOnlyList<SeatObjective> objectives, RosterSnapshot roster)
    {
        var definitions = roster.AttributeDefinitions.ToDictionary(item => item.Id);
        return objectives.Select(objective =>
        {
            var costs = new Dictionary<PersonId, long>();
            long[] weights = objective.Shape == ObjectiveShape.GroupBalance
                ? Enumerable.Repeat(1L, plan.Seats.Count).ToArray()
                : seatWeights(plan.Seats, objective.Dimension);
            if (definitions.TryGetValue(objective.AttributeId, out var definition)
                && definition.Kind == PersonAttributeKind.Number)
            {
                var values = roster.Where(person => person.Attributes.ContainsKey(objective.AttributeId))
                    .Select(person => (Person: person, Value: person.Attributes[objective.AttributeId].Number!.Value))
                    .ToArray();
                if (objective.Shape == ObjectiveShape.GroupBalance)
                {
                    // 批准计划的固定分组均值代理：与座位分配无关，不表示空间均衡。
                    var groups = values.GroupBy(item => item.Person.Group is { } group
                        ? (Group: group, Individual: Guid.Empty)
                        : (Group: (string?)null, Individual: item.Person.Id.Value))
                        .Select(group => (Members: group.ToArray(), Mean: group.Average(item => item.Value)))
                        .ToArray();
                    if (groups.Length > 0)
                    {
                        decimal min = groups.Min(group => group.Mean);
                        decimal max = groups.Max(group => group.Mean);
                        foreach (var group in groups)
                            foreach (var item in group.Members)
                                costs[item.Person.Id] = scale(group.Mean, min, max);
                    }
                }
                else if (values.Length > 0)
                {
                    decimal min = values.Min(item => item.Value);
                    decimal max = values.Max(item => item.Value);
                    if (min != max)
                        foreach (var item in values)
                        {
                            long scaled = scale(item.Value, min, max);
                            // 最小化 rank × 座位权重：较大 rank 才优先占较小权重的座位。
                            costs[item.Person.Id] = objective.Shape switch
                            {
                                ObjectiveShape.AscendingToFront or ObjectiveShape.Bimodal => 1000 - scaled,
                                ObjectiveShape.DescendingToFront or ObjectiveShape.Centered => scaled,
                                _ => throw new InvalidOperationException("无效目标形状。"),
                            };
                        }
                }
            }
            return (new PersonObjectiveCost(costs, objective.Id), new SeatObjectiveCost(weights, objective.Id));
        }).ToArray();
    }

    private static long scale(decimal value, decimal min, decimal max) =>
        min == max ? 0 : (long)Math.Round((value - min) / (max - min) * 1000m);

    private static long[] seatWeights(IReadOnlyList<GridPos> seats, SeatDimension dimension)
    {
        if (seats.Count == 0)
            return Array.Empty<long>();
        long center = ((long)seats.Min(seat => seat.Col) + seats.Max(seat => seat.Col)) / 2;
        return seats.Select(seat => dimension switch
        {
            SeatDimension.Row => (long)seat.Row,
            SeatDimension.Column => seat.Col,
            SeatDimension.DistanceFromCenter => Math.Abs(seat.Col - center),
            _ => throw new InvalidOperationException("无效座位维度。"),
        }).ToArray();
    }
}
