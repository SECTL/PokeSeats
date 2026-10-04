using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;

namespace PokeSeats.Core.Analyzer;

public enum PreCheckError
{
    DanglingRef,
    AlwaysTrue,
    AlwaysFalse,
    OutOfBound,
    PinnedCollision,
    DuplicatePin
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

public static class PreChecker
{
    /// <summary>
    /// 检查 Required ExactOffset 与直接固定的端点是否推出布局外的位置。
    /// 偏移约定为 A = B + (OffsetDy, OffsetDx)，固定任一端均可推导另一端。
    /// </summary>
    /// <remarks>
    /// 每条越界关系返回一条 Error 报告，Id 为关系规则的 Id；无错误返回空列表。
    /// 这里只检查矩形边界，不检查格子是否为座位、固定位置冲突或关系链传播。
    /// Forbidden 关系不要求另一端处于偏移位置，因此不参与检查。
    /// </remarks>
    public static IReadOnlyList<PreCheckReport> CheckOutOfBound(IEnumerable<Rule> rules, LayoutGrid layout)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(layout);

        // 输入可能只能枚举一次；保留同一人的全部固定规则，不任意挑选其中一条。
        var allRules = rules as IReadOnlyCollection<Rule> ?? rules.ToArray();
        var pins = allRules.OfType<PinnedRule>().ToLookup(r => r.Person);
        var reports = new List<PreCheckReport>();

        foreach (var rule in allRules)
        {
            if (rule is not RelationRule relation
                || relation.Kind != RelationKind.ExactOffset
                || relation.Polarity != RulePolarity.Required)
                continue;

            // 固定 B 时加偏移求 A；固定 A 时减偏移求 B。
            // 先提升为 long，避免加减及 int.MinValue 取负溢出后落回布局内。
            if (impliesOutOfBound(pins[relation.B], relation.OffsetDx, relation.OffsetDy, layout)
                || impliesOutOfBound(pins[relation.A], -(long)relation.OffsetDx, -(long)relation.OffsetDy, layout))
                reports.Add(new PreCheckReport(relation.Id, PreCheckError.OutOfBound, PreCheckErrorMark.Error));
        }

        return reports;
    }

    private static bool impliesOutOfBound(IEnumerable<PinnedRule> pins, long dx, long dy, LayoutGrid layout)
    {
        foreach (var pin in pins)
        {
            long row = pin.Pos.Row + dy;
            long col = pin.Pos.Col + dx;
            if (row < 0 || row >= layout.Rows || col < 0 || col >= layout.Cols)
                return true;
        }

        return false;
    }
}
