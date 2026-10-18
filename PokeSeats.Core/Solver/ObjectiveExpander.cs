using PokeSeats.IR.Base;

namespace PokeSeats.Core.Solver;

/// <summary>缺失键表示该学生的目标代价为零；均衡项独立于线性座位权重。</summary>
internal sealed record ExpandedObjective(
    SeatObjectiveId ObjectiveId,
    IReadOnlyDictionary<PersonId, long> PersonCosts,
    IReadOnlyList<long> SeatWeights,
    RowBalanceTerm? RowBalanceTerm,
    IReadOnlySet<PersonId> DistinguishedPeople);

internal sealed record RowBalanceRow(int Row, IReadOnlyList<int> Seats)
{
    internal long Capacity => Seats.Count;
}

internal sealed record RowBalanceTerm(
    IReadOnlyList<RowBalanceRow> Rows,
    long TotalCapacity,
    long TotalValue,
    IReadOnlyDictionary<PersonId, long> Values);

internal static class ObjectiveExpander
{
    internal static ExpandedObjective[] Expand(
        RuleExpansionPlan plan, IReadOnlyList<SeatObjective> objectives, RosterSnapshot roster)
    {
        var definitions = roster.AttributeDefinitions.ToDictionary(item => item.Id);
        return objectives.Select(objective =>
        {
            var costs = new Dictionary<PersonId, long>();
            var distinguished = new HashSet<PersonId>();
            RowBalanceTerm? balance = null;
            long[] weights = objective.Shape == ObjectiveShape.RowBalance
                ? Array.Empty<long>() : seatWeights(plan.Seats, objective);
            if (definitions.TryGetValue(objective.AttributeId, out var definition)
                && definition.Kind == PersonAttributeKind.Number)
            {
                var values = roster.Where(person => person.Attributes.ContainsKey(objective.AttributeId))
                    .Select(person => (Person: person.Id, Value: person.Attributes[objective.AttributeId].Number!.Value))
                    .ToArray();
                if (values.Length > 0)
                {
                    decimal min = values.Min(item => item.Value);
                    decimal max = values.Max(item => item.Value);
                    if (objective.Shape == ObjectiveShape.RowBalance)
                    {
                        var rows = plan.Seats.Select((seat, index) => (seat.Row, Index: index))
                            .GroupBy(item => item.Row)
                            .Select(group => new RowBalanceRow(group.Key, group.Select(item => item.Index).ToArray()))
                            .ToArray();
                        if (rows.Length > 1)
                        {
                            var scaled = values.ToDictionary(item => item.Person,
                                item => min == max ? 1L : scale(item.Value, min, max));
                            balance = new RowBalanceTerm(rows, plan.Seats.Count, scaled.Values.Sum(), scaled);
                            distinguished.UnionWith(scaled.Keys);
                        }
                    }
                    else if (min != max)
                        foreach (var item in values)
                        {
                            long scaled = scale(item.Value, min, max);
                            // 最小化 rank × 座位权重：较大 rank 优先占较小权重的座位。
                            long rank = objective.Shape switch
                            {
                                ObjectiveShape.AscendingToFront or ObjectiveShape.Bimodal => 1000 - scaled,
                                ObjectiveShape.DescendingToFront or ObjectiveShape.Centered => scaled,
                                ObjectiveShape.RegionPreference => objective.RegionPreferenceDirection == RegionPreferenceDirection.HighValues
                                    ? scaled : 1000 - scaled,
                                _ => throw new InvalidOperationException("无效目标形状。"),
                            };
                            costs[item.Person] = rank;
                            if (rank != 0) distinguished.Add(item.Person);
                        }
                }
            }
            return new ExpandedObjective(objective.Id, costs, weights, balance, distinguished);
        }).ToArray();
    }

    private static long scale(decimal value, decimal min, decimal max) =>
        (long)Math.Round((value - min) / (max - min) * 1000m);

    private static long[] seatWeights(IReadOnlyList<GridPos> seats, SeatObjective objective)
    {
        if (seats.Count == 0) return Array.Empty<long>();
        if (objective.Shape == ObjectiveShape.RegionPreference)
            return seats.Select(seat => objective.Region is { } region && region.Contains(seat) ? 0L : 1L).ToArray();
        long center = ((long)seats.Min(seat => seat.Col) + seats.Max(seat => seat.Col)) / 2;
        var weights = seats.Select(seat => objective.Dimension switch
        {
            SeatDimension.Row => (long)seat.Row,
            SeatDimension.Column => seat.Col,
            SeatDimension.DistanceFromCenter => Math.Abs(seat.Col - center),
            _ => throw new InvalidOperationException("无效座位维度。"),
        }).ToArray();
        if (objective.Region is not { } scope) return weights;
        long maximum = -1;
        for (int seat = 0; seat < seats.Count; seat++)
            if (scope.Contains(seats[seat])) maximum = Math.Max(maximum, weights[seat]);
        for (int seat = 0; seat < seats.Count; seat++)
            if (!scope.Contains(seats[seat])) weights[seat] = maximum + 1;
        return weights;
    }
}
