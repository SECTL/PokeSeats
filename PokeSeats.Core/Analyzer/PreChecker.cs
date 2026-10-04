using PokeSeats.IR.Base;

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

}
