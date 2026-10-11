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
        // 预检查错误直接返回对应规则 ID，不冒充 Z3 核心；也不展开规则集，避免悬空引用在展开时抛错。
        // 仅全局错误（如人数超过座位数）时列表为空，不归咎任何规则。
        var rejectedRuleIds = preCheck.PerRule
            .Where(report => report.Mark == PreCheckErrorMark.Error)
            .Select(report => report.Id)
            .Distinct()
            .ToArray();
        if (preCheck.Global.Any(report => report.Mark == PreCheckErrorMark.Error)
            || rejectedRuleIds.Length > 0)
        {
            return new SolverResult(
                SolveStatus.Rejected,
                Array.Empty<SeatAssignment>(),
                preCheck,
                "排座规则预检查未通过，存在逻辑冲突或无效参数，已中止求解。")
            {
                ConflictingRuleIds = rejectedRuleIds
            };
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
                    $"不支持的排座求解器后端：{request.Backend}")
            };
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return new SolverResult(
                SolveStatus.Rejected,
                Array.Empty<SeatAssignment>(),
                preCheck,
                $"排座规则解析与模型构建失败：{exception.Message}");
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
                    "Z3 求解器已证明当前规则组合在逻辑上无解。")
                {
                    ConflictingRuleIds = expansion.GetConflictingRuleIds()
                },
                _ => new SolverResult(
                    SolveStatus.Unknown,
                    Array.Empty<SeatAssignment>(),
                    preCheck,
                    "Z3 求解器在规定限度内未能确定是否存在可行解。")
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
                "CP-SAT 求解器已证明当前规则组合在逻辑上无解。")
            {
                ConflictingRuleIds = diagnoseZ3Conflicts(plan)
            },
            _ => new SolverResult(
                SolveStatus.Unknown,
                Array.Empty<SeatAssignment>(),
                preCheck,
                $"CP-SAT 求解器未能确定可行性：{status}。")
        };
    }

    /// <summary>
    /// CP-SAT 判无解后，用同一份与后端无关的展开计划补跑一次 Z3 诊断，把 UNSAT 核心映射成规则 ID。
    /// Z3 无法确认无解时返回空集合，不归咎任何规则。
    /// </summary>
    private static IReadOnlyList<RuleId> diagnoseZ3Conflicts(RuleExpansionPlan plan)
    {
        var expansion = new Z3RuleExpander().Expand(plan);
        try
        {
            return expansion.Solver.Check() == Status.UNSATISFIABLE
                ? expansion.GetConflictingRuleIds()
                : Array.Empty<RuleId>();
        }
        finally
        {
            expansion.Context.Dispose();
        }
    }

    private static IReadOnlyList<SeatAssignment> decodeZ3(Z3Expansion expansion, RuleExpansionPlan plan)
    {
        var result = new List<SeatAssignment>(expansion.Variables.Count);
        foreach (var (person, variable) in expansion.Variables)
        {
            if (expansion.Solver.Model.Eval(variable, true) is not IntNum value)
                throw new InvalidOperationException($"Z3 求解器未返回学生 {person} 的有效座位序号。");
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
