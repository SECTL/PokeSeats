using Google.OrTools.Sat;
using Microsoft.Z3;

namespace PokeSeats.Core.Solver;

/// <summary>
/// 空座软目标：先最小化空座距最后一排的总距离，再最小化横向累计分布偏差。
/// 每个列边界的理想左侧空座数为 emptyCount * 已经过的宽度 / 总宽度；
/// 用整数缩放后的绝对偏差之和衡量均匀程度，保留走道的实际宽度。
/// </summary>
internal sealed class EmptySeatOptimization
{
    private readonly RuleExpansionPlan plan;
    private readonly int emptyCount;
    private readonly int lastRow;
    private readonly int firstColumn;
    private readonly int width;
    private readonly PokeSeats.IR.Base.PersonId[] interchangeable;

    public EmptySeatOptimization(RuleExpansionPlan plan, IReadOnlySet<PokeSeats.IR.Base.PersonId> objectiveDistinguished)
    {
        this.plan = plan;
        emptyCount = plan.Seats.Count - plan.Variables.Count;
        lastRow = plan.Seats.Max(seat => seat.Row);
        firstColumn = plan.Seats.Min(seat => seat.Col);
        width = plan.Seats.Max(seat => seat.Col) - firstColumn + 1;
        // 仅对没有个人规则或非零目标代价的学生消除等价排列。
        var distinguished = plan.UnaryRules.Select(rule => rule.Person)
            .Concat(plan.RelationRules.SelectMany(rule => new[] { rule.A, rule.B }))
            .Concat(objectiveDistinguished).ToHashSet();
        interchangeable = plan.Variables.Keys.Where(person => !distinguished.Contains(person)).ToArray();
    }

    public Optimize CreateZ3(Z3Expansion expansion,
        IReadOnlyList<IReadOnlyList<(PersonObjectiveCost Person, SeatObjectiveCost Seat)>> objectiveLayers)
    {
        var context = expansion.Context;
        var optimizer = context.MkOptimize();
        optimizer.Add(expansion.Solver.Assertions);
        // AssertAndTrack 的断言以标记为前提；转入 Optimize 时必须同样启用全部规则。
        optimizer.Add(expansion.RuleMarkers.Keys.Select(context.MkBoolConst));
        for (var person = 0; person < interchangeable.Length; person++)
        {
            var variable = expansion.Variables[interchangeable[person]];
            optimizer.Add(context.MkGe(variable, context.MkInt(person)),
                context.MkLe(variable, context.MkInt(plan.Seats.Count - interchangeable.Length + person)));
            if (person > 0)
                optimizer.Add(context.MkLt(expansion.Variables[interchangeable[person - 1]], variable));
        }
        var empty = new BoolExpr[plan.Seats.Count];
        var zero = context.MkInt(0);
        var one = context.MkInt(1);
        var unitWeights = Enumerable.Repeat(1, expansion.Variables.Count + 1).ToArray();
        for (var seat = 0; seat < empty.Length; seat++)
        {
            empty[seat] = context.MkBoolConst($"empty_{seat}");
            var occupants = expansion.Variables.Values.Select(variable =>
                context.MkEq(variable, context.MkInt(seat))).Append(empty[seat]).ToArray();
            optimizer.Add(context.MkPBEq(unitWeights, occupants, 1));
        }
        optimizer.Add(context.MkPBEq(Enumerable.Repeat(1, empty.Length).ToArray(), empty, emptyCount));
        foreach (var layer in objectiveLayers)
        {
            var terms = new List<ArithExpr>();
            foreach (var (personCost, seatCost) in layer)
                foreach (var (person, cost) in personCost.Costs)
                {
                    if (cost == 0) continue;
                    var variable = expansion.Variables[person];
                    ArithExpr weight = zero;
                    for (int seat = plan.Seats.Count - 1; seat >= 0; seat--)
                        weight = (ArithExpr)context.MkITE(context.MkEq(variable, context.MkInt(seat)),
                            context.MkInt(seatCost.SeatWeights[seat]), weight);
                    terms.Add(context.MkMul(context.MkInt(cost), weight));
                }
            optimizer.MkMinimize(terms.Count == 0 ? zero : context.MkAdd(terms));
        }
        var rear = context.MkAdd(empty.Select((value, seat) =>
            (ArithExpr)context.MkITE(value, context.MkInt(lastRow - plan.Seats[seat].Row), zero)));
        var rearLowerBound = plan.Seats.Select(seat => lastRow - seat.Row).OrderBy(distance => distance).Take(emptyCount).Sum();
        optimizer.Add(context.MkGe(rear, context.MkInt(rearLowerBound)));
        optimizer.MkMinimize(rear);
        var deviations = new ArithExpr[width - 1];
        for (var offset = 1; offset < width; offset++)
        {
            var prefix = context.MkAdd(empty.Where((_, seat) => plan.Seats[seat].Col < firstColumn + offset)
                .Select(value => (ArithExpr)context.MkITE(value, one, zero)));
            var delta = context.MkSub(context.MkMul(context.MkInt(width), prefix), context.MkInt((long)emptyCount * offset));
            deviations[offset - 1] = (ArithExpr)context.MkITE(context.MkGe(delta, context.MkInt(0)), delta, context.MkUnaryMinus(delta));
        }
        // Z3 默认以声明顺序进行字典序优化：左右分散不能牺牲靠后目标。
        ArithExpr spread = deviations.Length == 0 ? zero : context.MkAdd(deviations);
        long spreadLowerBound = 0;
        for (var offset = 1; offset < width; offset++)
        {
            var remainder = (long)emptyCount * offset % width;
            spreadLowerBound += Math.Min(remainder, width - remainder);
        }
        // 这些与人员规则无关的下界始终成立，避免求解器枚举排列来证明不可能更好。
        optimizer.Add(context.MkGe(spread, context.MkInt(spreadLowerBound)));
        optimizer.MkMinimize(spread);
        return optimizer;
    }

    public IReadOnlyList<LinearExpr> AddObjectiveLayersOrTools(OrToolsExpansion expansion,
        IReadOnlyList<IReadOnlyList<(PersonObjectiveCost Person, SeatObjectiveCost Seat)>> objectiveLayers)
    {
        var layers = new List<LinearExpr>(objectiveLayers.Count);
        foreach (var layer in objectiveLayers)
        {
            var terms = new List<LinearExpr>();
            foreach (var (personCost, seatCost) in layer)
                foreach (var (person, cost) in personCost.Costs)
                {
                    if (cost == 0) continue;
                    var weight = expansion.Model.NewIntVar(seatCost.SeatWeights.Min(),
                        seatCost.SeatWeights.Max(), $"objective_{personCost.ObjectiveId.Value:N}_{person.Value:N}");
                    expansion.Model.AddElement(expansion.Variables[person], seatCost.SeatWeights, weight);
                    terms.Add(weight * cost);
                }
            layers.Add(LinearExpr.Sum(terms));
        }
        return layers;
    }

    public (LinearExpr Rear, LinearExpr Spread) AddOrTools(OrToolsExpansion expansion)
    {
        var model = expansion.Model;
        for (var person = 1; person < interchangeable.Length; person++)
            model.Add(expansion.Variables[interchangeable[person - 1]] < expansion.Variables[interchangeable[person]]);
        var empty = new BoolVar[plan.Seats.Count];
        for (var seat = 0; seat < empty.Length; seat++)
        {
            empty[seat] = model.NewBoolVar($"empty_{seat}");
            var occupants = new List<BoolVar>(expansion.Variables.Count);
            foreach (var variable in expansion.Variables.Values)
            {
                var indicator = model.NewBoolVar($"{variable.Name}_occupies_{seat}");
                model.Add(variable == seat).OnlyEnforceIf(indicator);
                model.Add(variable != seat).OnlyEnforceIf(indicator.Not());
                occupants.Add(indicator);
            }
            model.Add(LinearExpr.Sum(occupants) + empty[seat] == 1);
        }
        model.Add(LinearExpr.Sum(empty) == emptyCount);
        var rear = LinearExpr.WeightedSum(empty, plan.Seats.Select(seat => lastRow - seat.Row));
        var deviations = new IntVar[width - 1];
        for (var offset = 1; offset < width; offset++)
        {
            var prefix = LinearExpr.Sum(empty.Where((_, seat) => plan.Seats[seat].Col < firstColumn + offset));
            var deviation = model.NewIntVar(0, (long)emptyCount * width, $"empty_prefix_deviation_{offset}");
            model.AddAbsEquality(deviation, width * prefix - (long)emptyCount * offset);
            deviations[offset - 1] = deviation;
        }
        return (rear, LinearExpr.Sum(deviations));
    }
}
