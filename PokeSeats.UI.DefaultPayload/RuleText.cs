using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>把规则改写成给老师看的中文说明；座位坐标按 1 起数显示。</summary>
/// <remarks>
/// <para>
/// 方向（前/后/左/右）与轴（同一排/同一列）只描述对应轴上的约束；距离是座位邻接图上的加权步数
/// （相邻座位 1 步、跨窄过道 2 步）。距离为 1 时统一说"相邻"，不显示步数。
/// <see cref="RelationKind.Proximity"/> 只描述距离，不描述方向。
/// </para>
/// <para>
/// 名单里已经找不到的人员显示为"未知学生(短标识)"；不支持的规则类型抛 <see cref="NotSupportedException"/>。
/// </para>
/// </remarks>
internal static class RuleText
{
    public static string Describe(Rule rule, StudentSearchIndex people)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(people);

        return rule switch
        {
            PinnedRule pinned => string.Format(RulesStrings.PinnedRuleFormat, Name(pinned.Person, people), pinned.Pos.Row + 1, pinned.Pos.Col + 1),
            RegionRule region => DescribeRegion(region, people),
            RelationRule relation => DescribeRelation(relation, people),
            CapacityRule capacity => DescribeCapacity(capacity),
            _ => throw new NotSupportedException(string.Format(RulesStrings.UnsupportedRuleTypeFormat, rule.GetType().Name)),
        };
    }

    private static string DescribeRegion(RegionRule rule, StudentSearchIndex people)
    {
        string person = Name(rule.Person, people);
        string region = RegionText(rule);

        if (region.Length == 0)
            return rule.Polarity == RulePolarity.Required
                ? string.Format(RulesStrings.RegionUnrestrictedFormat, person)
                : string.Format(RulesStrings.RegionForbiddenAllFormat, person);

        return rule.Polarity == RulePolarity.Required
            ? string.Format(RulesStrings.RegionRequiredFormat, person, region)
            : string.Format(RulesStrings.RegionForbiddenFormat, person, region);
    }

    private static string RegionText(RegionRule rule)
    {
        var parts = new List<string>(2);
        string rows = RangeText(rule.RowMin, rule.RowMax, RulesStrings.RowUnit);
        if (rows.Length > 0)
            parts.Add(rows);
        string cols = RangeText(rule.ColMin, rule.ColMax, RulesStrings.ColumnUnit);
        if (cols.Length > 0)
            parts.Add(cols);
        return string.Join(RulesStrings.RegionPartSeparator, parts);
    }

    private static string RangeText(int? minimum, int? maximum, string unit)
    {
        if (minimum is int lower && maximum is int upper)
        {
            if (lower > upper)
                return string.Format(RulesStrings.RangeEmptyFormat, lower + 1, unit, upper + 1);
            return lower == upper
                ? string.Format(RulesStrings.RangeSingleFormat, lower + 1, unit)
                : string.Format(RulesStrings.RangeSpanFormat, lower + 1, upper + 1, unit);
        }
        if (minimum is int from)
            return string.Format(RulesStrings.RangeMinimumFormat, from + 1, unit);
        if (maximum is int to)
            return string.Format(RulesStrings.RangeMaximumFormat, to + 1, unit);
        return string.Empty;
    }

    private static string DescribeCapacity(CapacityRule rule)
    {
        string target = rule.Axis.Type == AxisType.Row
            ? string.Format(RulesStrings.CapacityRowTargetFormat, rule.Axis.Index + 1)
            : string.Format(RulesStrings.CapacityColumnTargetFormat, rule.Axis.Index + 1);

        string limit = (rule.Min, rule.Max) switch
        {
            (null, null) => RulesStrings.CapacityUnlimited,
            (int atLeast, null) => string.Format(RulesStrings.CapacityAtLeastFormat, atLeast),
            (null, int atMost) => string.Format(RulesStrings.CapacityAtMostFormat, atMost),
            (int atLeast, int atMost) when atLeast == atMost => string.Format(RulesStrings.CapacityExactFormat, atLeast),
            (int atLeast, int atMost) => string.Format(RulesStrings.CapacityRangeFormat, atLeast, atMost),
        };

        return string.Format(RulesStrings.CapacitySummaryFormat, target, limit);
    }

    private static string DescribeRelation(RelationRule rule, StudentSearchIndex people)
    {
        string a = Name(rule.A, people);
        string b = Name(rule.B, people);
        bool required = rule.Polarity == RulePolarity.Required;

        if (rule.Kind == RelationKind.ExactOffset)
        {
            if (rule.OffsetDx == 0 && rule.OffsetDy == 0)
                return required
                    ? string.Format(RulesStrings.SameSeatRequiredFormat, a, b)
                    : string.Format(RulesStrings.SameSeatForbiddenFormat, a, b);

            return required
                ? string.Format(RulesStrings.OffsetSeatRequiredFormat, a, b, OffsetText(rule.OffsetDx, rule.OffsetDy))
                : string.Format(RulesStrings.OffsetSeatForbiddenFormat, a, b, OffsetText(rule.OffsetDx, rule.OffsetDy));
        }

        if (rule.Kind == RelationKind.Proximity)
            return ProximityText(a, b, rule, required);

        string place = DirectionText(rule);
        return required
            ? string.Format(RulesStrings.DirectionRequiredFormat, a, b, place)
            : string.Format(RulesStrings.DirectionForbiddenFormat, a, b, place);
    }

    private static string ProximityText(string a, string b, RelationRule rule, bool required)
    {
        switch (rule.Distance)
        {
            case null:
                return required
                    ? string.Format(RulesStrings.ProximityDistanceUnlimitedFormat, a, b)
                    : string.Format(RulesStrings.ProximityNoLimitConflictFormat, a, b);
            case < 0:
                return string.Format(RulesStrings.ProximityNegativeFormat, a, b);
            case 0:
                return required
                    ? string.Format(RulesStrings.ProximitySameSeatRequiredFormat, a, b)
                    : string.Format(RulesStrings.ProximitySameSeatForbiddenFormat, a, b);
            case 1:
                string adjacency = AdjacencyHint(rule);
                return required
                    ? string.Format(RulesStrings.ProximityAdjacentRequiredFormat, a, b, adjacency)
                    : string.Format(RulesStrings.ProximityAdjacentForbiddenFormat, a, b, adjacency);
            case int steps:
                return required
                    ? string.Format(RulesStrings.ProximityMaxDistanceFormat, a, b, steps)
                    : string.Format(RulesStrings.ProximityMinDistanceFormat, a, b, steps);
            default:
                throw new NotSupportedException(string.Format(RulesStrings.UnsupportedDistanceFormat, rule.Distance));
        }
    }

    private static string DirectionText(RelationRule rule)
    {
        string side = rule.Kind switch
        {
            RelationKind.Behind => RulesStrings.DirectionBehind,
            RelationKind.InFrontOf => RulesStrings.DirectionInFront,
            RelationKind.LeftOf => RulesStrings.DirectionLeft,
            RelationKind.RightOf => RulesStrings.DirectionRight,
            RelationKind.SameRow => RulesStrings.DirectionSameRow,
            RelationKind.SameColumn => RulesStrings.DirectionSameColumn,
            _ => throw new NotSupportedException(string.Format(RulesStrings.UnsupportedRelationKindFormat, rule.Kind)),
        };

        switch (rule.Distance)
        {
            case 1:
                return string.Format(RulesStrings.AdjacentDeskFormat, side, ChebyshevSuffix(rule.Metric));
            case null:
                return string.Format(RulesStrings.DirectionDeskFormat, side);
            case < 0:
                return string.Format(RulesStrings.DirectionNegativeDistanceFormat, side);
            case 0:
                return string.Format(RulesStrings.DirectionSameDeskFormat, side);
            case int steps:
                return string.Format(RulesStrings.DirectionWithinStepsFormat, side, steps);
            default:
                throw new NotSupportedException(string.Format(RulesStrings.UnsupportedDistanceFormat, rule.Distance));
        }
    }

    private static string AdjacencyHint(RelationRule rule) => rule.Adjacency switch
    {
        AdjacencyMode.Horizontal => rule.AllowNarrowAisle
            ? RulesStrings.AdjacencyHorizontalHint
            : RulesStrings.AdjacencyHorizontalStrictHint,
        AdjacencyMode.FourWay => RulesStrings.AdjacencyFourWayHint,
        AdjacencyMode.EightWay => RulesStrings.AdjacencyEightWayHint,
        _ => throw new NotSupportedException(string.Format(RulesStrings.UnsupportedAdjacencyFormat, rule.Adjacency))
    };

    private static string ChebyshevSuffix(DistanceMetric metric)
        => metric == DistanceMetric.Chebyshev ? RulesStrings.ChebyshevSuffix : string.Empty;

    private static string OffsetText(int dx, int dy)
    {
        var parts = new List<string>(2);
        if (dx > 0)
            parts.Add(string.Format(RulesStrings.OffsetWindowFormat, dx));
        else if (dx < 0)
            parts.Add(string.Format(RulesStrings.OffsetDoorFormat, -(long)dx));
        if (dy > 0)
            parts.Add(string.Format(RulesStrings.OffsetBackFormat, dy));
        else if (dy < 0)
            parts.Add(string.Format(RulesStrings.OffsetFrontFormat, -(long)dy));
        return string.Join(RulesStrings.RegionPartSeparator, parts);
    }

    private static string Name(PersonId id, StudentSearchIndex people)
        => people.Get(id)?.Name ?? string.Format(RulesStrings.UnknownStudentFormat, id);
}
