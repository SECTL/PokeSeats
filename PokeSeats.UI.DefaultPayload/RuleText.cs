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
            PinnedRule pinned => $"{Name(pinned.Person, people)} 必须固定在第 {pinned.Pos.Row + 1} 排、第 {pinned.Pos.Col + 1} 列",
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
                ? $"{person} 的就座区域不设限制"
                : $"{person} 禁止在教室内任何座位就座（此限制导致无解）";

        return rule.Polarity == RulePolarity.Required
            ? $"{person} 必须坐在 {region} 区域内的课桌"
            : $"{person} 禁止坐在 {region} 区域内的课桌";
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
                return $"第 {lower + 1} {unit}至第 {upper + 1} {unit}（空区间，不可达成）";
            return lower == upper ? $"第 {lower + 1} {unit}" : $"第 {lower + 1} 至 {upper + 1} {unit}";
        }
        if (minimum is int from)
            return $"第 {from + 1} {unit}及以后排列";
        if (maximum is int to)
            return $"第 {to + 1} {unit}及以前排列";
        return string.Empty;
    }

    private static string DescribeCapacity(CapacityRule rule)
    {
        string target = rule.Axis.Type == AxisType.Row
            ? $"第 {rule.Axis.Index + 1} 排（横向）"
            : $"第 {rule.Axis.Index + 1} 列（纵向）";

        string limit = (rule.Min, rule.Max) switch
        {
            (null, null) => "不限人数",
            (int atLeast, null) => $"至少安排 {atLeast} 人",
            (null, int atMost) => $"最多允许 {atMost} 人",
            (int atLeast, int atMost) when atLeast == atMost => $"精确安排 {atLeast} 人",
            (int atLeast, int atMost) => $"安排 {atLeast} 至 {atMost} 人",
        };

        return $"{target}容纳人数：{limit}";
    }

    private static string DescribeRelation(RelationRule rule, StudentSearchIndex people)
    {
        string a = Name(rule.A, people);
        string b = Name(rule.B, people);
        bool required = rule.Polarity == RulePolarity.Required;

        if (rule.Kind == RelationKind.ExactOffset)
        {
            if (rule.OffsetDx == 0 && rule.OffsetDy == 0)
                return required ? $"{a} 必须与 {b} 同座（偏移量为 0）" : $"{a} 不得与 {b} 坐在同一张课桌";

            return required
                ? $"{a} 必须坐在 {b} {OffsetText(rule.OffsetDx, rule.OffsetDy)}的课桌"
                : $"{a} 禁止坐在 {b} {OffsetText(rule.OffsetDx, rule.OffsetDy)}的课桌";
        }

        if (rule.Kind == RelationKind.Proximity)
            return ProximityText(a, b, rule, required);

        string place = DirectionText(rule);
        return required
            ? $"{a} 必须坐在 {b} 的{place}"
            : $"{a} 禁止坐在 {b} 的{place}";
    }

    private static string ProximityText(string a, string b, RelationRule rule, bool required)
    {
        switch (rule.Distance)
        {
            case null:
                return required
                    ? $"{a} 与 {b} 的相对距离不限（无约束效果）"
                    : $"{a} 与 {b} 不限距离（与排座逻辑冲突，无法满足）";
            case < 0:
                return $"{a} 与 {b} 的最大距离上限为负数（不可达成）";
            case 0:
                return required ? $"{a} 必须与 {b} 安排在同一张课桌" : $"{a} 禁止与 {b} 坐在同一张课桌";
            case 1:
                string adjacency = AdjacencyHint(rule.Adjacency);
                return required
                    ? $"{a} 必须与 {b} 相邻就座（{adjacency}）"
                    : $"{a} 禁止与 {b} 相邻就座（{adjacency}）";
            case int steps:
                return required
                    ? $"{a} 与 {b} 之间的座位间距不得超过 {steps} 步"
                    : $"{a} 与 {b} 之间的座位间距必须大于 {steps} 步";
            default:
                throw new NotSupportedException($"不支持的距离取值：{rule.Distance}");
        }
    }

    private static string DirectionText(RelationRule rule)
    {
        string side = rule.Kind switch
        {
            RelationKind.Behind => "正后方",
            RelationKind.InFrontOf => "正前方",
            RelationKind.LeftOf => "正左侧",
            RelationKind.RightOf => "正右侧",
            RelationKind.SameRow => "同一排座位",
            RelationKind.SameColumn => "同一列座位",
            _ => throw new NotSupportedException($"不支持的关系类型：{rule.Kind}"),
        };

        switch (rule.Distance)
        {
            case 1:
                return $"{side}相邻课桌{ChebyshevSuffix(rule.Metric)}";
            case null:
                return $"{side}课桌";
            case < 0:
                return $"{side}课桌（距离上限为负数，无法达成）";
            case 0:
                return $"{side}同一课桌（距离为 0）";
            case int steps:
                return $"{side} {steps} 步以内的课桌";
            default:
                throw new NotSupportedException($"不支持的距离取值：{rule.Distance}");
        }
    }

    private static string AdjacencyHint(AdjacencyMode mode) => mode switch
    {
        AdjacencyMode.Horizontal => "横向：同排左右，含跨窄过道",
        AdjacencyMode.FourWay => "四向：前后左右，含跨窄过道",
        AdjacencyMode.EightWay => "八向：前后左右及斜角，含正交跨窄过道",
        _ => throw new NotSupportedException($"不支持的相邻模式：{mode}")
    };

    private static string ChebyshevSuffix(DistanceMetric metric)
        => metric == DistanceMetric.Chebyshev ? "（含斜角）" : string.Empty;

    private static string OffsetText(int dx, int dy)
    {
        var parts = new List<string>(2);
        if (dx > 0)
            parts.Add($"向窗侧（右）偏 {dx} 列");
        else if (dx < 0)
            parts.Add($"向门侧（左）偏 {-(long)dx} 列");
        if (dy > 0)
            parts.Add($"向教室后偏 {dy} 排");
        else if (dy < 0)
            parts.Add($"向讲台前偏 {-(long)dy} 排");
        return string.Join("、", parts);
    }

    private static string Name(PersonId id, StudentSearchIndex people)
        => people.Get(id)?.Name ?? $"[未知学生:{id}]";
}
