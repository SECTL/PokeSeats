using Google.OrTools.Sat;
using Microsoft.Z3;
using PokeSeats.Core.Analyzer;
using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;

namespace PokeSeats.Core.Solver;

/// <summary>
/// Runs prechecks, expands the rules, invokes the selected backend, and decodes a solution.
/// </summary>
public sealed class SolverService
{
    public SolverResult Solve(SolverRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Roster);
        ArgumentNullException.ThrowIfNull(request.Layout);
        ArgumentNullException.ThrowIfNull(request.Rules);

        var preCheck = PreChecker.Run(request.Rules, request.Roster, request.Layout);
        if (preCheck.Global.Any(report => report.Mark == PreCheckErrorMark.Error)
            || preCheck.PerRule.Any(report => report.Mark == PreCheckErrorMark.Error))
        {
            return new SolverResult(
                SolveStatus.Rejected,
                Array.Empty<SeatAssignment>(),
                preCheck,
                "预检查发现错误，未创建求解模型。");
        }

        try
        {
            var plan = RuleExpansionPlan.Build(request.Layout, request.Roster, request.Rules);
            return request.Backend switch
            {
                SolverBackend.Z3 => solveZ3(plan, preCheck),
                SolverBackend.OrToolsCpSat => solveOrTools(plan, preCheck),
                _ => new SolverResult(
                    SolveStatus.Rejected,
                    Array.Empty<SeatAssignment>(),
                    preCheck,
                    $"不支持的求解后端：{request.Backend}")
            };
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return new SolverResult(
                SolveStatus.Rejected,
                Array.Empty<SeatAssignment>(),
                preCheck,
                $"规则展开失败：{exception.Message}");
        }
    }

    private static SolverResult solveZ3(RuleExpansionPlan plan, PreCheckResult preCheck)
    {
        var expansion = new Z3RuleExpander().Expand(plan);
        try
        {
            var status = expansion.Solver.Check();
            return status switch
            {
                Status.SATISFIABLE => new SolverResult(
                    SolveStatus.Satisfiable,
                    decodeZ3(expansion, plan),
                    preCheck),
                Status.UNSATISFIABLE => new SolverResult(
                    SolveStatus.Unsatisfiable,
                    Array.Empty<SeatAssignment>(),
                    preCheck,
                    "Z3 证明规则集合无解。"),
                _ => new SolverResult(
                    SolveStatus.Unknown,
                    Array.Empty<SeatAssignment>(),
                    preCheck,
                    "Z3 未能确定可满足性。")
            };
        }
        finally
        {
            expansion.Context.Dispose();
        }
    }

    private static SolverResult solveOrTools(RuleExpansionPlan plan, PreCheckResult preCheck)
    {
        var expansion = new OrToolsRuleExpander().Expand(plan);
        using var solver = new CpSolver();
        var status = solver.Solve(expansion.Model);
        return status switch
        {
            CpSolverStatus.Optimal or CpSolverStatus.Feasible => new SolverResult(
                SolveStatus.Satisfiable,
                decodeOrTools(expansion, solver, plan),
                preCheck),
            CpSolverStatus.Infeasible => new SolverResult(
                SolveStatus.Unsatisfiable,
                Array.Empty<SeatAssignment>(),
                preCheck,
                "CP-SAT 证明规则集合无解。"),
            _ => new SolverResult(
                SolveStatus.Unknown,
                Array.Empty<SeatAssignment>(),
                preCheck,
                $"CP-SAT 未能确定可满足性：{status}。")
        };
    }

    private static IReadOnlyList<SeatAssignment> decodeZ3(Z3Expansion expansion, RuleExpansionPlan plan)
    {
        var result = new List<SeatAssignment>(expansion.Variables.Count);
        foreach (var (person, variable) in expansion.Variables)
        {
            if (expansion.Solver.Model.Eval(variable, true) is not IntNum value)
                throw new InvalidOperationException($"Z3 未返回人员 {person} 的整数座位值。");
            result.Add(new SeatAssignment(person, plan.Seats[checked((int)value.Int64)]));
        }
        return result;
    }

    private static IReadOnlyList<SeatAssignment> decodeOrTools(
        OrToolsExpansion expansion,
        CpSolver solver,
        RuleExpansionPlan plan)
    {
        return expansion.Variables
            .Select(item => new SeatAssignment(
                item.Key,
                plan.Seats[checked((int)solver.Value(item.Value))]))
            .ToArray();
    }
}
