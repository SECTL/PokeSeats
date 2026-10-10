using System.Text.Json;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class StudentRoster
{
    private readonly List<Person> _students = new();

    public IReadOnlyList<Person> Students => _students;

    /// <summary>名单内容的版本号；每次 <see cref="Replace"/> 后自增，供搜索索引判断是否需要重建。</summary>
    public int Revision { get; private set; }

    public void Replace(IEnumerable<Person> students)
    {
        _students.Clear();
        _students.AddRange(students);
        Revision++;
    }

    public void Load(string path)
    {
        if (!File.Exists(path))
            return;

        var saved = JsonSerializer.Deserialize<List<StoredStudent>>(File.ReadAllText(path));
        if (saved is null)
            return;

        Replace(saved
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .Select(item => new Person(
                new PersonId(item.Id == Guid.Empty ? Guid.NewGuid() : item.Id),
                item.Name.Trim(),
                Normalize(item.StudentNumber),
                null)));
    }

    public void Save(string path, IEnumerable<Person> students)
    {
        ArgumentNullException.ThrowIfNull(students);
        var saved = students.Select(student => new StoredStudent(
            student.Id.Value,
            student.Name,
            student.StudentNumber)).ToList();
        File.WriteAllText(path, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record StoredStudent(Guid Id, string Name, string? StudentNumber);
}
