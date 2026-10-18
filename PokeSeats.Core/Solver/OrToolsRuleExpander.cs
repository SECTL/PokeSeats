using Google.OrTools.Sat;
using PokeSeats.IR.Base;

namespace PokeSeats.Core.Solver;

public sealed record OrToolsExpansion(
    CpModel Model,
    IReadOnlyDictionary<PersonId, IntVar> Variables);

/// <summary>Expands a shared rule plan into a Google OR-Tools CP-SAT model.</summary>
public sealed class OrToolsRuleExpander : IRuleExpander<OrToolsExpansion>
{
    public OrToolsExpansion Expand(RuleExpansionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var model = new CpModel();
        var upperBound = Math.Max(0, plan.Seats.Count - 1);
        var variables = plan.Variables.ToDictionary(
            item => item.Key,
            item => model.NewIntVar(0, upperBound, item.Value.Name));

        if (plan.Seats.Count == 0 && variables.Count > 0)
        {
            model.AddBoolOr(Array.Empty<ILiteral>());
        }
        else if (variables.Count > 1)
            model.AddAllDifferent(variables.Values);

        foreach (var rule in plan.UnaryRules)
            addUnary(model, variables[rule.Person], rule.MatchingSeats, rule.Polarity);

        foreach (var rule in plan.RelationRules)
            addRelation(model, variables[rule.A], variables[rule.B], rule.MatchingPairs, rule.Polarity);

        foreach (var rule in plan.CapacityRules)
            addCapacity(model, variables.Values, rule);

        return new OrToolsExpansion(model, variables);
    }
    private static void addUnary(CpModel model, IntVar variable, IReadOnlyList<int> matchingSeats, RulePolarity polarity)
    {
        var table = polarity == RulePolarity.Required
            ? model.AddAllowedAssignments(new[] { variable })
            : model.AddForbiddenAssignments(new[] { variable });
        foreach (var seat in matchingSeats)
            table.AddTuple(new[] { seat });
    }

    private static void addRelation(
        CpModel model,
        IntVar a,
        IntVar b,
        IReadOnlyList<AllowedSeatPair> matchingPairs,
        RulePolarity polarity)
    {
        var table = polarity == RulePolarity.Required
            ? model.AddAllowedAssignments(new[] { a, b })
            : model.AddForbiddenAssignments(new[] { a, b });
        foreach (var pair in matchingPairs)
            table.AddTuple(new[] { pair.A, pair.B });
    }

    private static void addCapacity(
        CpModel model,
        IEnumerable<IntVar> variables,
        CapacityRuleExpansion rule)
    {
        var indicators = new List<BoolVar>();
        foreach (var variable in variables)
        {
            foreach (var seat in rule.Seats)
            {
                var indicator = model.NewBoolVar($"{variable.Name}_at_{seat}");
                model.Add(variable == seat).OnlyEnforceIf(indicator);
                model.Add(variable != seat).OnlyEnforceIf(indicator.Not());
                indicators.Add(indicator);
            }
        }

        var count = LinearExpr.Sum(indicators);
        if (rule.Min is int min)
            model.Add(count >= min);
        if (rule.Max is int max)
            model.Add(count <= max);
    }

}
