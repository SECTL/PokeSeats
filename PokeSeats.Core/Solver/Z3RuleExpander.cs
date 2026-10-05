using Microsoft.Z3;
using PokeSeats.IR.Base;

namespace PokeSeats.Core.Solver;

public sealed record Z3Expansion(
    Context Context,
    Microsoft.Z3.Solver Solver,
    IReadOnlyDictionary<PersonId, IntExpr> Variables);

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
        var upperBound = Math.Max(0, plan.Seats.Count - 1);

        foreach (var variable in variables.Values)
            solver.Add(context.MkAnd(context.MkLe(context.MkInt(0), variable), context.MkLe(variable, context.MkInt(upperBound))));
        if (plan.Seats.Count == 0)
            foreach (var variable in variables.Values)
                solver.Add(context.MkFalse());
        else if (variables.Count > 1)
            solver.Add(context.MkDistinct(variables.Values.Cast<Expr>().ToArray()));

        foreach (var rule in plan.UnaryRules)
            addUnary(context, solver, variables[rule.Person], rule.MatchingSeats, rule.Polarity);
        foreach (var rule in plan.RelationRules)
            addRelation(context, solver, variables[rule.A], variables[rule.B], rule.MatchingPairs, rule.Polarity);
        foreach (var rule in plan.CapacityRules)
            addCapacity(context, solver, variables.Values, rule);

        return new Z3Expansion(context, solver, variables);
    }

    private static void addUnary(Context context, Microsoft.Z3.Solver solver, IntExpr variable, IReadOnlyList<int> matchingSeats, RulePolarity polarity)
    {
        var predicate = context.MkOr(matchingSeats.Select(seat => context.MkEq(variable, context.MkInt(seat))));
        solver.Add(polarity == RulePolarity.Required ? predicate : context.MkNot(predicate));
    }

    private static void addRelation(
        Context context,
        Microsoft.Z3.Solver solver,
        IntExpr a,
        IntExpr b,
        IReadOnlyList<AllowedSeatPair> matchingPairs,
        RulePolarity polarity)
    {
        var predicate = context.MkOr(matchingPairs.Select(pair => context.MkAnd(
            context.MkEq(a, context.MkInt(pair.A)),
            context.MkEq(b, context.MkInt(pair.B)))));
        solver.Add(polarity == RulePolarity.Required ? predicate : context.MkNot(predicate));
    }

    private static void addCapacity(
        Context context,
        Microsoft.Z3.Solver solver,
        IEnumerable<IntExpr> variables,
        CapacityRuleExpansion rule)
    {
        var terms = variables.Select(variable => context.MkAdd(rule.Seats.Select(seat =>
            (ArithExpr)context.MkITE(
                context.MkEq(variable, context.MkInt(seat)),
                context.MkInt(1),
                context.MkInt(0))))).ToArray();
        var count = context.MkAdd(terms);
        if (rule.Min is int min)
            solver.Add(context.MkGe(count, context.MkInt(min)));
        if (rule.Max is int max)
            solver.Add(context.MkLe(count, context.MkInt(max)));
    }
}
