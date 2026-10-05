namespace PokeSeats.Core.Solver;

/// <summary>Expands one backend-neutral plan into a concrete solver model.</summary>
public interface IRuleExpander<out TExpansion>
{
    TExpansion Expand(RuleExpansionPlan plan);
}
