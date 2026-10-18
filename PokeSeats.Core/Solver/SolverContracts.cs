using PokeSeats.Core.Analyzer;
using PokeSeats.IR.Base;

namespace PokeSeats.Core.Solver;

public enum SolverBackend
{
    Z3,
    OrToolsCpSat
}

public enum SolveStatus
{
    Satisfiable,
    Unsatisfiable,
    Unknown,
    Rejected
}

public sealed record SolverRequest(
    RosterSnapshot Roster,
    LayoutGrid Layout,
    IReadOnlyCollection<PokeSeats.IR.Rules.Rule> Rules,
    SolverBackend Backend,
    IReadOnlyList<SeatObjective> Objectives);

public sealed record SeatAssignment(PersonId Person, GridPos Seat);

public sealed record SolverResult(
    SolveStatus Status,
    IReadOnlyList<SeatAssignment> Assignments,
    PreCheckResult PreCheck,
    string? Detail = null)
{
    /// <summary>
    /// 参与失败的规则 ID。求解无解时为 Z3 UNSAT 核心映射（CP-SAT 判无解后同样用 Z3 诊断）；
    /// 预检查拒绝时为带 Error 的按规则报告 ID（不是 Z3 核心）；仅全局原因（如人数超过座位数）时为空。
    /// </summary>
    public IReadOnlyList<RuleId> ConflictingRuleIds { get; init; } = Array.Empty<RuleId>();

    public bool IsSuccess => Status == SolveStatus.Satisfiable;
}
