using Microsoft.Z3;
using PokeSeats.IR.Base;

namespace PokeSeats.Core.Solver;

/// <summary>
/// 一次 Z3 展开：上下文、求解器、人员变量，以及“规则标记名 → 规则 ID”的映射。
/// 每条规则的完整谓词都以独立标记跟踪断言（容量规则的 Min/Max 合取为同一条断言）；
/// 人员域约束与互异约束不跟踪，因此 UNSAT 核心只会包含规则本身。
/// </summary>
public sealed record Z3Expansion(
    Context Context,
    Microsoft.Z3.Solver Solver,
    IReadOnlyDictionary<PersonId, IntExpr> Variables,
    IReadOnlyDictionary<string, RuleId> RuleMarkers)
{
    /// <summary>
    /// 把最近一次求解检查得到的 UNSAT 核心映射为规则 ID。
    /// 必须在 <see cref="Context"/> 释放前调用；核心只含被跟踪的规则标记，
    /// 由全局原因（如人数超过座位数）导致的无解返回空集合，不归咎任何规则。
    /// </summary>
    public IReadOnlyList<RuleId> GetConflictingRuleIds()
    {
        var core = Solver.UnsatCore;
        if (core.Length == 0)
            return Array.Empty<RuleId>();

        var ids = new List<RuleId>(core.Length);
        var seen = new HashSet<RuleId>();
        foreach (var expression in core)
        {
            var declaration = expression.FuncDecl;
            if (declaration is null || !RuleMarkers.TryGetValue(declaration.Name.ToString(), out var id))
                continue;
            if (seen.Add(id))
                ids.Add(id);
        }
        return ids;
    }
}

/// <summary>Expands a shared rule plan into a Z3 integer model.</summary>
public sealed class Z3RuleExpander : IRuleExpander<Z3Expansion>
{
    public Z3Expansion Expand(RuleExpansionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var context = new Context();
        var solver = context.MkSolver();
        var variables = plan.Variables.ToDictionary(
            item => item.Key,
            item => context.MkIntConst(item.Value.Name));
        var ruleMarkers = new Dictionary<string, RuleId>();
        var upperBound = Math.Max(0, plan.Seats.Count - 1);

        foreach (var variable in variables.Values)
            solver.Add(context.MkAnd(context.MkLe(context.MkInt(0), variable), context.MkLe(variable, context.MkInt(upperBound))));
        if (plan.Seats.Count == 0)
            foreach (var variable in variables.Values)
                solver.Add(context.MkFalse());
        else if (variables.Count > 1)
            solver.Add(context.MkDistinct(variables.Values.Cast<Expr>().ToArray()));

        foreach (var rule in plan.UnaryRules)
            track(context, solver, ruleMarkers, rule.RuleId,
                unaryPredicate(context, variables[rule.Person], rule.MatchingSeats, rule.Polarity));
        foreach (var rule in plan.RelationRules)
            track(context, solver, ruleMarkers, rule.RuleId,
                relationPredicate(context, variables[rule.A], variables[rule.B], rule.MatchingPairs, rule.Polarity));
        foreach (var rule in plan.CapacityRules)
            track(context, solver, ruleMarkers, rule.RuleId,
                capacityPredicate(context, variables.Values, rule));

        return new Z3Expansion(context, solver, variables, ruleMarkers);
    }

    /// <summary>以“rule_规则ID_序号”标记跟踪一条完整规则谓词；序号保证同一规则多次展开时标记仍然唯一。</summary>
    private static void track(
        Context context,
        Microsoft.Z3.Solver solver,
        Dictionary<string, RuleId> ruleMarkers,
        RuleId ruleId,
        BoolExpr predicate)
    {
        var markerName = $"rule_{ruleId.value:N}_{ruleMarkers.Count}";
        ruleMarkers.Add(markerName, ruleId);
        solver.AssertAndTrack(predicate, context.MkBoolConst(markerName));
    }

    private static BoolExpr unaryPredicate(Context context, IntExpr variable, IReadOnlyList<int> matchingSeats, RulePolarity polarity)
    {
        var predicate = context.MkOr(matchingSeats.Select(seat => context.MkEq(variable, context.MkInt(seat))));
        return polarity == RulePolarity.Required ? predicate : context.MkNot(predicate);
    }

    private static BoolExpr relationPredicate(
        Context context,
        IntExpr a,
        IntExpr b,
        IReadOnlyList<AllowedSeatPair> matchingPairs,
        RulePolarity polarity)
    {
        var predicate = context.MkOr(matchingPairs.Select(pair => context.MkAnd(
            context.MkEq(a, context.MkInt(pair.A)),
            context.MkEq(b, context.MkInt(pair.B)))));
        return polarity == RulePolarity.Required ? predicate : context.MkNot(predicate);
    }

    /// <summary>容量规则的 Min/Max 合取为同一条断言后再跟踪，核心不会把一条规则拆成两半。</summary>
    private static BoolExpr capacityPredicate(Context context, IEnumerable<IntExpr> variables, CapacityRuleExpansion rule)
    {
        var terms = variables.Select(variable => context.MkAdd(rule.Seats.Select(seat =>
            (ArithExpr)context.MkITE(
                context.MkEq(variable, context.MkInt(seat)),
                context.MkInt(1),
                context.MkInt(0))))).ToArray();
        var count = context.MkAdd(terms);
        var bounds = new List<BoolExpr>(2);
        if (rule.Min is int min)
            bounds.Add(context.MkGe(count, context.MkInt(min)));
        if (rule.Max is int max)
            bounds.Add(context.MkLe(count, context.MkInt(max)));
        return bounds.Count switch
        {
            0 => context.MkTrue(),
            1 => bounds[0],
            _ => context.MkAnd(bounds)
        };
    }
}
