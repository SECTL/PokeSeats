namespace PokeSeats.IR.Base;

public readonly record struct PersonId(Guid Value)
{
    public static   PersonId New()           => new(Guid.NewGuid());
    public static   PersonId Parse(string s) => new(Guid.Parse(s));
    public override string   ToString()      => Convert.ToHexString(Value.ToByteArray())[..8];
}

public sealed record Person
{
    public PersonId Id { get; }
    public string Name { get; }
    public string? StudentNumber { get; }
    public string? Group { get; }
    public IReadOnlyDictionary<PersonAttributeId, PersonAttributeValue> Attributes { get; }

    public Person(PersonId id, string name, string? studentNumber, string? group,
        IEnumerable<KeyValuePair<PersonAttributeId, PersonAttributeValue>> attributes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(attributes);
        var values = new Dictionary<PersonAttributeId, PersonAttributeValue>();
        foreach (var (key, value) in attributes)
        {
            ArgumentNullException.ThrowIfNull(value);
            values.Add(key, value);
        }
        Id = id; Name = name; StudentNumber = studentNumber; Group = group;
        Attributes = new System.Collections.ObjectModel.ReadOnlyDictionary<PersonAttributeId, PersonAttributeValue>(values);
    }
}
