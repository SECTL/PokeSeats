namespace PokeSeats.IR.Base;

public readonly record struct PersonAttributeId(Guid Value)
{
    public static PersonAttributeId New() => new(Guid.NewGuid());
}

public enum PersonAttributeKind
{
    Number,
    Category,
    Boolean
}

/// <summary>Typed attribute value. Absence from a person's attributes means unknown, not zero/false.</summary>
public sealed record PersonAttributeValue
{
    public PersonAttributeKind Kind { get; }
    public decimal? Number { get; }
    public string? Category { get; }
    public bool? Boolean { get; }

    private PersonAttributeValue(PersonAttributeKind kind, decimal? number, string? category, bool? boolean)
    {
        Kind = kind; Number = number; Category = category; Boolean = boolean;
    }

    public static PersonAttributeValue FromNumber(decimal value) => new(PersonAttributeKind.Number, value, null, null);
    public static PersonAttributeValue FromBoolean(bool value) => new(PersonAttributeKind.Boolean, null, null, value);
    public static PersonAttributeValue FromCategory(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Category must not be blank.", nameof(value));
        return new(PersonAttributeKind.Category, null, value, null);
    }
}

/// <summary>Stable identity is independent of the display name. Numeric bounds are inclusive.</summary>
public sealed class PersonAttributeDefinition
{
    public PersonAttributeId Id { get; }
    public string Name { get; }
    public PersonAttributeKind Kind { get; }
    public string? Unit { get; }
    public decimal? Minimum { get; }
    public decimal? Maximum { get; }
    public IReadOnlyList<string> Categories { get; }

    public PersonAttributeDefinition(PersonAttributeId id, string name, PersonAttributeKind kind,
        string? unit = null, decimal? minimum = null, decimal? maximum = null, IEnumerable<string>? categories = null)
    {
        if (id.Value == Guid.Empty) throw new ArgumentException("Attribute ID must not be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Attribute name must not be blank.", nameof(name));
        if (!Enum.IsDefined(typeof(PersonAttributeKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        var choices = categories?.ToArray() ?? Array.Empty<string>();
        if (kind != PersonAttributeKind.Number && (unit is not null || minimum is not null || maximum is not null))
            throw new ArgumentException("Only numeric attributes have units and bounds.");
        if (minimum > maximum) throw new ArgumentException("Minimum must not exceed maximum.");
        if (kind == PersonAttributeKind.Category)
        {
            if (choices.Length == 0 || choices.Any(string.IsNullOrWhiteSpace) || choices.Distinct(StringComparer.Ordinal).Count() != choices.Length)
                throw new ArgumentException("Categories must be nonempty, nonblank and unique.", nameof(categories));
        }
        else if (choices.Length != 0) throw new ArgumentException("Only categorical attributes have categories.", nameof(categories));
        Id = id; Name = name; Kind = kind; Unit = unit; Minimum = minimum; Maximum = maximum;
        Categories = Array.AsReadOnly(choices);
    }

    public bool Accepts(PersonAttributeValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Kind == Kind && (Kind switch
        {
            PersonAttributeKind.Number => (!Minimum.HasValue || value.Number >= Minimum) && (!Maximum.HasValue || value.Number <= Maximum),
            PersonAttributeKind.Category => Categories.Contains(value.Category!, StringComparer.Ordinal),
            PersonAttributeKind.Boolean => true,
            _ => false
        });
    }
}

/// <summary>Validated immutable roster, shared by persistence, solver requests and result snapshots.</summary>
public sealed class RosterSnapshot : IReadOnlyCollection<Person>
{
    public static RosterSnapshot Empty { get; } = new(Array.Empty<PersonAttributeDefinition>(), Array.Empty<Person>());
    public IReadOnlyList<PersonAttributeDefinition> AttributeDefinitions { get; }
    public IReadOnlyList<Person> Students { get; }
    public int Count => Students.Count;

    public RosterSnapshot(IEnumerable<PersonAttributeDefinition> attributeDefinitions, IEnumerable<Person> students)
    {
        ArgumentNullException.ThrowIfNull(attributeDefinitions);
        ArgumentNullException.ThrowIfNull(students);
        var definitions = attributeDefinitions.ToArray();
        var people = students.ToArray();
        var byId = new Dictionary<PersonAttributeId, PersonAttributeDefinition>();
        foreach (var definition in definitions)
        {
            ArgumentNullException.ThrowIfNull(definition);
            if (!byId.TryAdd(definition.Id, definition)) throw new ArgumentException("Duplicate attribute ID.", nameof(attributeDefinitions));
        }
        var ids = new HashSet<PersonId>();
        foreach (var person in people)
        {
            ArgumentNullException.ThrowIfNull(person);
            if (person.Id.Value == Guid.Empty || !ids.Add(person.Id)) throw new ArgumentException("Student IDs must be nonempty and unique.", nameof(students));
            foreach (var (id, value) in person.Attributes)
                if (!byId.TryGetValue(id, out var definition) || !definition.Accepts(value))
                    throw new ArgumentException($"Invalid attribute {id.Value} for student {person.Id.Value}.", nameof(students));
        }
        AttributeDefinitions = Array.AsReadOnly(definitions);
        Students = Array.AsReadOnly(people);
    }

    public IEnumerator<Person> GetEnumerator() => Students.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
