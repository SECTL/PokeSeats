namespace PokeSeats.IR.Base;

public readonly record struct RuleId(Guid value)
{
    public static RuleId New() => new(Guid.NewGuid());
}

public enum RulePolarity
{
    Required,
    Forbidden
}

