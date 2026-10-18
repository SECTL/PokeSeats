using System.Text.Json;
using System.Text.Json.Serialization;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class StudentRoster
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    public RosterSnapshot Snapshot { get; private set; } = RosterSnapshot.Empty;
    public IReadOnlyList<Person> Students => Snapshot.Students;

    /// <summary>Any roster or attribute definition change invalidates search and seating snapshots.</summary>
    public int Revision { get; private set; }

    public void Replace(RosterSnapshot roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        Snapshot = roster;
        Revision++;
    }

    public void Load(string path)
    {
        if (!File.Exists(path)) return;
        using Stream stream = File.OpenRead(path);
        Replace(ReadJson(stream));
    }

    public void Save(string path, RosterSnapshot roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temporaryPath = path + ".tmp";
        try
        {
            using (Stream stream = File.Create(temporaryPath)) WriteJson(stream, roster);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    internal static RosterSnapshot ReadJson(Stream stream)
    {
        using JsonDocument json = JsonDocument.Parse(stream);
        bool legacy = json.RootElement.ValueKind == JsonValueKind.Array;
        try
        {
            StoredDefinition[] definitions;
            StoredStudent[] students;
            if (legacy)
            {
                definitions = Array.Empty<StoredDefinition>();
                students = json.RootElement.Deserialize<StoredStudent[]>(SerializerOptions)
                    ?? throw new InvalidDataException("Student list is missing.");
            }
            else
            {
                var document = json.RootElement.Deserialize<StoredDocument>(SerializerOptions)
                    ?? throw new InvalidDataException("Roster document is missing.");
                if (document.Version != 1) throw new InvalidDataException($"Unsupported roster version: {document.Version}.");
                definitions = document.AttributeDefinitions ?? throw new InvalidDataException("Attribute definitions are missing.");
                students = document.Students ?? throw new InvalidDataException("Student list is missing.");
            }
            var schema = definitions.Select(item => item is null
                ? throw new InvalidDataException("Null attribute definition.")
                : new PersonAttributeDefinition(new PersonAttributeId(item.Id), item.Name,
                    item.Kind ?? throw new InvalidDataException("Attribute kind is missing."), item.Unit, item.Minimum, item.Maximum, item.Categories)).ToArray();
            var byId = schema.ToDictionary(item => item.Id.Value);
            var people = new List<Person>(students.Length);
            foreach (var item in students)
            {
                if (item is null) throw new InvalidDataException("Null student.");
                if (string.IsNullOrWhiteSpace(item.Name))
                {
                    if (legacy) continue;
                    throw new InvalidDataException("Student name must not be blank.");
                }
                var attributes = new Dictionary<PersonAttributeId, PersonAttributeValue>();
                if (item.Attributes is not null)
                {
                    foreach (var (id, value) in item.Attributes)
                    {
                        if (!byId.TryGetValue(id, out var definition)) throw new InvalidDataException($"Unknown attribute: {id}.");
                        var typed = definition.Kind switch
                        {
                            PersonAttributeKind.Number when value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _) => PersonAttributeValue.FromNumber(value.GetDecimal()),
                            PersonAttributeKind.Category when value.ValueKind == JsonValueKind.String => PersonAttributeValue.FromCategory(value.GetString()!),
                            PersonAttributeKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False => PersonAttributeValue.FromBoolean(value.GetBoolean()),
                            _ => throw new InvalidDataException($"Invalid value type for attribute: {definition.Name}.")
                        };
                        attributes.Add(definition.Id, typed);
                    }
                }
                var personId = legacy && item.Id == Guid.Empty ? Guid.NewGuid() : item.Id;
                people.Add(new Person(new PersonId(personId), item.Name.Trim(), Normalize(item.StudentNumber), Normalize(item.Group), attributes));
            }
            return new RosterSnapshot(schema, people);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Invalid roster: {exception.Message}", exception);
        }
    }

    internal static void WriteJson(Stream stream, RosterSnapshot roster)
    {
        var definitions = roster.AttributeDefinitions.Select(item => new StoredDefinition(item.Id.Value, item.Name,
            item.Kind, item.Unit, item.Minimum, item.Maximum, item.Categories.ToArray())).ToArray();
        var students = roster.Students.Select(student => new StoredStudent(student.Id.Value, student.Name,
            student.StudentNumber, student.Group, student.Attributes.ToDictionary(item => item.Key.Value,
                item => item.Value.Kind switch
                {
                    PersonAttributeKind.Number => JsonSerializer.SerializeToElement(item.Value.Number!.Value),
                    PersonAttributeKind.Category => JsonSerializer.SerializeToElement(item.Value.Category!),
                    PersonAttributeKind.Boolean => JsonSerializer.SerializeToElement(item.Value.Boolean!.Value),
                    _ => throw new InvalidDataException("Unknown attribute kind.")
                }))).ToArray();
        JsonSerializer.Serialize(stream, new StoredDocument(1, definitions, students), SerializerOptions);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record StoredDocument(int Version, StoredDefinition[]? AttributeDefinitions, StoredStudent[]? Students);
    private sealed record StoredDefinition(Guid Id, string Name, PersonAttributeKind? Kind, string? Unit,
        decimal? Minimum, decimal? Maximum, string[]? Categories);
    private sealed record StoredStudent(Guid Id, string Name, string? StudentNumber, string? Group,
        Dictionary<Guid, JsonElement>? Attributes);
}
