using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;

namespace PokeSeats.Core.Analyzer;

public enum PreCheckError
{
    DanglingRef,
    AlwaysTrue,
    AlwaysFalse,
    OutOfBound,
    PinnedSeatCollision,
    DuplicatePin,
    OverCapacity
}

public enum PreCheckErrorMark
{
    Warning,
    Error
}

public sealed record PreCheckReport(
    RuleId            Id,
    PreCheckError     Error,
    PreCheckErrorMark Mark
);

/// <summary>不归属于某条规则的全局错误。</summary>
public sealed record PreCheckGlobalReport(PreCheckError Error, PreCheckErrorMark Mark);

/// <summary>调用方应同时检查 Global 和 PerRule 中的 Error；只有 Warning 时不阻止求解。</summary>
/// <param name="Global">不依附规则的全局报告，目前为人员超出座位容量。</param>
/// <param name="PerRule">规则报告；同一规则可有多个独立结构错误，恒真恒假分类最多一条。</param>
public sealed record PreCheckResult(
    IReadOnlyList<PreCheckGlobalReport> Global,
    IReadOnlyList<PreCheckReport> PerRule
);

/// <summary>
/// 只做单规则检查及全局超员检查；除两人固定同格外，不推导规则之间的矛盾。
/// AlwaysTrue 为 Warning，AlwaysFalse 和结构错误为 Error。
/// </summary>
public static class PreChecker
{
    /// <summary>一次运行全部检查，返回独立的全局报告和按规则报告。</summary>
    /// <remarks>
    /// rules 仅从调用方枚举一次。报告按引用、边界、非座位、重复固定、支持集、容量的顺序输出。
    /// 已有引用/边界/非座位错误的规则不再做恒真恒假分类；全局超员不阻止其他检查。
    /// 不结合固定位置、禁区或其他关系来缩小某条规则的支持集。
    /// </remarks>
    public static PreCheckResult Run(IEnumerable<Rule> rules, IReadOnlyCollection<Person> roster, LayoutGrid layout)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(layout);

        var allRules = rules as IReadOnlyCollection<Rule> ?? rules.ToArray();
        var global = CheckOverCapacity(roster, layout);
        var reports = new List<PreCheckReport>();
        reports.AddRange(CheckDanglingRef(allRules, roster));
        reports.AddRange(CheckOutOfBound(allRules, layout));
        reports.AddRange(CheckPinnedSeatCollision(allRules, layout));

        var invalid = new HashSet<RuleId>();
        foreach (var report in reports)
            invalid.Add(report.Id);

        reports.AddRange(CheckDuplicatePin(allRules));
        var validRules = allRules.Where(rule => !invalid.Contains(rule.Id)).ToArray();
        if (validRules.Any(rule => rule is not CapacityRule))
            reports.AddRange(CheckAlways(validRules, new RuleSupport(layout)));
        reports.AddRange(CheckCapacity(validRules, layout));

        return new PreCheckResult(global, reports);
    }

    /// <summary>人数超过全部 Desk 数量时产生全局错误，不伪造所属 RuleId。</summary>
    public static IReadOnlyList<PreCheckGlobalReport> CheckOverCapacity(IReadOnlyCollection<Person> roster, LayoutGrid layout)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(layout);

        return roster.Count > layout.GetDeskCount()
            ? new[] { new PreCheckGlobalReport(PreCheckError.OverCapacity, PreCheckErrorMark.Error) }
            : Array.Empty<PreCheckGlobalReport>();
    }

    /// <summary>检查所有携带 PersonId 的规则；同一规则即使两端均缺失，也只报告一次。</summary>
    public static IReadOnlyList<PreCheckReport> CheckDanglingRef(IEnumerable<Rule> rules, IReadOnlyCollection<Person> roster)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(roster);

        var people = new HashSet<PersonId>();
        foreach (var person in roster)
            people.Add(person.Id);

        var reports = new List<PreCheckReport>();
        foreach (var rule in rules)
        {
            bool dangling = rule switch
            {
                PinnedRule pin => !people.Contains(pin.Person),
                RegionRule region => !people.Contains(region.Person),
                RelationRule relation => !people.Contains(relation.A) || !people.Contains(relation.B),
                CapacityRule => false,
                _ => throw new NotSupportedException($"Unsupported rule type: {rule.GetType().Name}")
            };
            if (dangling)
                reports.Add(new PreCheckReport(rule.Id, PreCheckError.DanglingRef, PreCheckErrorMark.Error));
        }

        return reports;
    }

    /// <summary>检查固定坐标及区域显式边界是否超出布局矩形；不从其他规则推导位置。</summary>
    /// <remarks>区域边界包含端点；null 不限。反向但未越界的区间由支持集判为空集。</remarks>
    public static IReadOnlyList<PreCheckReport> CheckOutOfBound(IEnumerable<Rule> rules, LayoutGrid layout)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(layout);

        var reports = new List<PreCheckReport>();
        foreach (var rule in rules)
        {
            bool outside = rule switch
            {
                PinnedRule pin => !isInside(pin.Pos, layout),
                RegionRule region => isOutside(region.RowMin, layout.Rows)
                                     || isOutside(region.RowMax, layout.Rows)
                                     || isOutside(region.ColMin, layout.Cols)
                                     || isOutside(region.ColMax, layout.Cols),
                _ => false
            };
            if (outside)
                reports.Add(new PreCheckReport(rule.Id, PreCheckError.OutOfBound, PreCheckErrorMark.Error));
        }

        return reports;
    }

    /// <summary>检查布局内钉在非 Desk 格的规则；布局外坐标仅由 OutOfBound 报告。</summary>
    public static IReadOnlyList<PreCheckReport> CheckPinnedSeatCollision(IEnumerable<Rule> rules, LayoutGrid layout)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(layout);

        var reports = new List<PreCheckReport>();
        foreach (var rule in rules)
            if (rule is PinnedRule pin && isInside(pin.Pos, layout) && !layout.IsDesk(pin.Pos))
                reports.Add(new PreCheckReport(pin.Id, PreCheckError.PinnedSeatCollision, PreCheckErrorMark.Error));

        return reports;
    }

    /// <summary>两名不同人员钉在同一格时，报告所有参与的固定规则；同一人的重复固定不算此错误。</summary>
    public static IReadOnlyList<PreCheckReport> CheckDuplicatePin(IEnumerable<Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        var pins = new List<PinnedRule>();
        var firstPerson = new Dictionary<GridPos, PersonId>();
        var collisions = new HashSet<GridPos>();
        foreach (var rule in rules)
        {
            if (rule is not PinnedRule pin)
                continue;

            pins.Add(pin);
            if (firstPerson.TryGetValue(pin.Pos, out var person))
            {
                if (person != pin.Person)
                    collisions.Add(pin.Pos);
            }
            else
                firstPerson.Add(pin.Pos, pin.Person);
        }

        var reports = new List<PreCheckReport>();
        var reported = new HashSet<RuleId>();
        foreach (var pin in pins)
            if (collisions.Contains(pin.Pos) && reported.Add(pin.Id))
                reports.Add(new PreCheckReport(pin.Id, PreCheckError.DuplicatePin, PreCheckErrorMark.Error));

        return reports;
    }

    /// <summary>统一按基础支持集及规则极性判断恒真/恒假；CapacityRule 由 CheckCapacity 单独处理。</summary>
    /// <remarks>没有合法指派时，无论极性都为 AlwaysFalse；PinnedRule 隐含 Required。</remarks>
    public static IReadOnlyList<PreCheckReport> CheckAlways(IEnumerable<Rule> rules, RuleSupport support)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(support);

        var reports = new List<PreCheckReport>();
        foreach (var rule in rules)
        {
            if (rule is CapacityRule)
                continue;

            var summary = support.Summarize(rule);
            var polarity = rule switch
            {
                RegionRule region => region.Polarity,
                RelationRule relation => relation.Polarity,
                _ => RulePolarity.Required
            };
            long accepted = polarity == RulePolarity.Required
                ? summary.MatchingCount
                : summary.UniverseCount - summary.MatchingCount;

            if (accepted == 0)
                reports.Add(new PreCheckReport(rule.Id, PreCheckError.AlwaysFalse, PreCheckErrorMark.Error));
            else if (accepted == summary.UniverseCount)
                reports.Add(new PreCheckReport(rule.Id, PreCheckError.AlwaysTrue, PreCheckErrorMark.Warning));
        }

        return reports;
    }

    /// <summary>将 Min/Max 的合取约束与实际行/列桌数允许的计数区间 [0, capacity] 比较。</summary>
    /// <remarks>Max 冗余而 Min 仍有限制时，不将整条规则标记为 AlwaysTrue；不存在的行/列容量为 0。</remarks>
    public static IReadOnlyList<PreCheckReport> CheckCapacity(IEnumerable<Rule> rules, LayoutGrid layout)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(layout);

        var reports = new List<PreCheckReport>();
        foreach (var rule in rules)
        {
            if (rule is not CapacityRule capacity)
                continue;

            int deskCount = axisDeskCount(layout, capacity.Axis);
            int min = capacity.Min ?? 0;
            int max = capacity.Max ?? deskCount;
            if (Math.Max(0, min) > Math.Min(deskCount, max))
                reports.Add(new PreCheckReport(capacity.Id, PreCheckError.AlwaysFalse, PreCheckErrorMark.Error));
            else if (min <= 0 && max >= deskCount)
                reports.Add(new PreCheckReport(capacity.Id, PreCheckError.AlwaysTrue, PreCheckErrorMark.Warning));
        }

        return reports;
    }

    private static int axisDeskCount(LayoutGrid layout, Axis axis)
    {
        int count = 0;
        switch (axis.Type)
        {
            case AxisType.Row:
                if (axis.Index >= 0 && axis.Index < layout.Rows)
                    for (int col = 0; col < layout.Cols; col++)
                        if (layout.IsDesk(new GridPos(axis.Index, col)))
                            count++;
                break;
            case AxisType.Column:
                if (axis.Index >= 0 && axis.Index < layout.Cols)
                    for (int row = 0; row < layout.Rows; row++)
                        if (layout.IsDesk(new GridPos(row, axis.Index)))
                            count++;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(axis), axis.Type, "Unknown axis type.");
        }

        return count;
    }

    private static bool isInside(GridPos pos, LayoutGrid layout)
        => pos.Row >= 0 && pos.Row < layout.Rows && pos.Col >= 0 && pos.Col < layout.Cols;

    private static bool isOutside(int? bound, int size)
        => bound.HasValue && (bound.Value < 0 || bound.Value >= size);
}
