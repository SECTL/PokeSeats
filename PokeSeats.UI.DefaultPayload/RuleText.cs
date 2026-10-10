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
            PinnedRule pinned => $"{Name(pinned.Person, people)} 必须坐在第{pinned.Pos.Row + 1}排第{pinned.Pos.Col + 1}列",
            RegionRule region => DescribeRegion(region, people),
            RelationRule relation => DescribeRelation(relation, people),
            CapacityRule capacity => DescribeCapacity(capacity),
            _ => throw new NotSupportedException($"不支持的规则类型：{rule.GetType().Name}"),
        };
    }

    private static string DescribeRegion(RegionRule rule, StudentSearchIndex people)
    {
        string person = Name(rule.Person, people);
        string region = RegionText(rule);

        if (region.Length == 0)
            return rule.Polarity == RulePolarity.Required
                ? $"{person} 的座位不限区域"
                : $"{person} 不能坐在任何座位（该限制无解）";

        return rule.Polarity == RulePolarity.Required
            ? $"{person} 必须坐在{region}内的座位"
            : $"{person} 不能坐在{region}内的座位";
    }

    private static string RegionText(RegionRule rule)
    {
        var parts = new List<string>(2);
        string rows = RangeText(rule.RowMin, rule.RowMax, "排");
        if (rows.Length > 0)
            parts.Add(rows);
        string cols = RangeText(rule.ColMin, rule.ColMax, "列");
        if (cols.Length > 0)
            parts.Add(cols);
        return string.Join("、", parts);
    }

    private static string RangeText(int? minimum, int? maximum, string unit)
    {
        if (minimum is int lower && maximum is int upper)
        {
            if (lower > upper)
                return $"第{lower + 1}{unit}至第{upper + 1}{unit}（空区间）";
            return lower == upper ? $"第{lower + 1}{unit}" : $"第{lower + 1}至{upper + 1}{unit}";
        }
        if (minimum is int from)
            return $"第{from + 1}{unit}及以后";
        if (maximum is int to)
            return $"第{to + 1}{unit}及以前";
        return string.Empty;
    }

    private static string DescribeCapacity(CapacityRule rule)
    {
        string target = rule.Axis.Type == AxisType.Row
            ? $"第{rule.Axis.Index + 1}排"
            : $"第{rule.Axis.Index + 1}列";

        string limit = (rule.Min, rule.Max) switch
        {
            (null, null) => "不限",
            (int atLeast, null) => $"至少 {atLeast} 人",
            (null, int atMost) => $"至多 {atMost} 人",
            (int atLeast, int atMost) when atLeast == atMost => $"恰好 {atLeast} 人",
            (int atLeast, int atMost) => $"至少 {atLeast} 人且至多 {atMost} 人",
        };

        return $"{target}的占座人数：{limit}";
    }

    private static string DescribeRelation(RelationRule rule, StudentSearchIndex people)
    {
        string a = Name(rule.A, people);
        string b = Name(rule.B, people);
        bool required = rule.Polarity == RulePolarity.Required;

        if (rule.Kind == RelationKind.ExactOffset)
        {
            if (rule.OffsetDx == 0 && rule.OffsetDy == 0)
                return required ? $"{a} 必须坐在 {b} 的同一座位（偏移为 0）" : $"{a} 不能坐在 {b} 的同一座位";

            return required
                ? $"{a} 必须坐在 {b} {OffsetText(rule.OffsetDx, rule.OffsetDy)}的座位"
                : $"{a} 不能坐在 {b} {OffsetText(rule.OffsetDx, rule.OffsetDy)}的座位";
        }

        if (rule.Kind == RelationKind.Proximity)
            return ProximityText(a, b, rule, required);

        string place = DirectionText(rule);
        return required
            ? $"{a} 必须坐在 {b} {place}"
            : $"{a} 不能坐在 {b} {place}";
    }

    private static string ProximityText(string a, string b, RelationRule rule, bool required)
    {
        switch (rule.Distance)
        {
            case null:
                return required
                    ? $"{a} 与 {b} 的距离不限（该规则无实际约束）"
                    : $"{a} 与 {b} 的距离不限（该禁止无法满足）";
            case < 0:
                return $"{a} 与 {b} 的距离上限为负（无解）";
            case 0:
                return required ? $"{a} 必须与 {b} 坐在同一座位" : $"{a} 不能与 {b} 坐在同一座位";
            case 1:
                string adjacency = AdjacencyHint(rule.Metric);
                return required
                    ? $"{a} 必须与 {b} 相邻（{adjacency}）"
                    : $"{a} 不能与 {b} 相邻（{adjacency}）";
            case int steps:
                return required
                    ? $"{a} 与 {b} 的距离必须不超过 {steps} 步"
                    : $"{a} 与 {b} 的距离必须大于 {steps} 步";
            default:
                throw new NotSupportedException($"不支持的距离取值：{rule.Distance}");
        }
    }

    private static string DirectionText(RelationRule rule)
    {
        string side = rule.Kind switch
        {
            RelationKind.Behind => "后面",
            RelationKind.InFrontOf => "前面",
            RelationKind.LeftOf => "左边",
            RelationKind.RightOf => "右边",
            RelationKind.SameRow => "同一排",
            RelationKind.SameColumn => "同一列",
            _ => throw new NotSupportedException($"不支持的关系类型：{rule.Kind}"),
        };

        switch (rule.Distance)
        {
            case 1:
                return $"{side}相邻的座位{ChebyshevSuffix(rule.Metric)}";
            case null:
                return $"{side}的座位";
            case < 0:
                return $"{side}的座位（距离上限为负，无解）";
            case 0:
                return $"{side}的座位（距离为 0，即同一座位）";
            case int steps:
                return $"{side} {steps} 步以内的座位";
            default:
                throw new NotSupportedException($"不支持的距离取值：{rule.Distance}");
        }
    }

    /// <summary>Proximity 相邻时的范围说明：曼哈顿只算上下左右，切比雪夫含斜角。</summary>
    private static string AdjacencyHint(DistanceMetric metric)
        => metric == DistanceMetric.Chebyshev ? "含斜角相邻" : "上下左右紧挨";

    private static string ChebyshevSuffix(DistanceMetric metric)
        => metric == DistanceMetric.Chebyshev ? "（含斜角）" : string.Empty;

    private static string OffsetText(int dx, int dy)
    {
        var parts = new List<string>(2);
        if (dx > 0)
            parts.Add($"向右 {dx} 列");
        else if (dx < 0)
            parts.Add($"向左 {-(long)dx} 列");
        if (dy > 0)
            parts.Add($"向下 {dy} 排");
        else if (dy < 0)
            parts.Add($"向上 {-(long)dy} 排");
        return string.Join("、", parts);
    }

    private static string Name(PersonId id, StudentSearchIndex people)
        => people.Get(id)?.Name ?? $"未知学生({id})";
}
