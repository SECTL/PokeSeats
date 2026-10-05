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
    IReadOnlyCollection<Person> Roster,
    LayoutGrid Layout,
    IReadOnlyCollection<PokeSeats.IR.Rules.Rule> Rules,
    SolverBackend Backend);

public sealed record SeatAssignment(PersonId Person, GridPos Seat);

public sealed record SolverResult(
    SolveStatus Status,
    IReadOnlyList<SeatAssignment> Assignments,
    PreCheckResult PreCheck,
    string? Detail = null)
{
    public bool IsSuccess => Status == SolveStatus.Satisfiable;
}
