namespace PokeSeats.IR.Base;

public readonly record struct PersonId(Guid Value)
{
    public static   PersonId New()           => new(Guid.NewGuid());
    public static   PersonId Parse(string s) => new(Guid.Parse(s));
    public override string   ToString()      => Convert.ToHexString(Value.ToByteArray())[..8];
}

public sealed record Person(
    PersonId Id,
    string   Name,
    string?  StudentNumber,
    string?  Group
);
