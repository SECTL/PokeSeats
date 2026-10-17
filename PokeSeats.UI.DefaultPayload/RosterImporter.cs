using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

internal sealed record ImportResult(IReadOnlyList<Person> Students, int SkippedRows);

internal static class RosterImporter
{
    public static SpreadsheetPreview Preview(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(PayloadStrings.RosterFileNotFound, path);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xls" or ".xlsx" => ExcelRosterImporter.Preview(path),
            ".csv" => PreviewCsv(path),
            ".json" => PreviewSecRandom(path),
            _ => throw new InvalidDataException(PayloadStrings.RosterUnsupportedFormat)
        };
    }

    public static ImportResult Import(SpreadsheetPreview preview, int nameColumn, int studentNumberColumn, int groupColumn)
    {
        if (nameColumn < 0 || nameColumn >= preview.Headers.Count)
            throw new ArgumentOutOfRangeException(nameof(nameColumn));
        if (studentNumberColumn < -1 || studentNumberColumn >= preview.Headers.Count)
            throw new ArgumentOutOfRangeException(nameof(studentNumberColumn));
        if (groupColumn < -1 || groupColumn >= preview.Headers.Count)
            throw new ArgumentOutOfRangeException(nameof(groupColumn));

        var students = new List<Person>();
        var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skippedRows = 0;
        foreach (IReadOnlyList<string> row in preview.Rows)
        {
            string name = ReadColumn(row, nameColumn);
            if (name.Length == 0)
            {
                skippedRows++;
                continue;
            }
            string number = ReadColumn(row, studentNumberColumn);
            string group = ReadColumn(row, groupColumn);
            if (number.Length > 0 && !numbers.Add(number))
                throw new InvalidDataException(string.Format(PayloadStrings.RosterDuplicateStudentNumberFormat, number));
            students.Add(new Person(PersonId.New(), name, number.Length == 0 ? null : number, group.Length == 0 ? null : group));
        }
        return new ImportResult(students, skippedRows);
    }

    private static string ReadColumn(IReadOnlyList<string> row, int column)
        => column >= 0 && column < row.Count ? row[column].Trim() : string.Empty;

    private static SpreadsheetPreview PreviewCsv(string path)
    {
        using var parser = new TextFieldParser(path, System.Text.Encoding.UTF8, true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        try
        {
            string[]? header = parser.ReadFields();
            if (header is null || header.Length == 0)
                throw new InvalidDataException(PayloadStrings.CsvMissingHeader);
            string[] headers = header.Select((value, index) => string.IsNullOrWhiteSpace(value) ? string.Format(PayloadStrings.SpreadsheetColumnFallbackHeaderFormat, index + 1) : value.Trim()).ToArray();
            var rows = new List<IReadOnlyList<string>>();
            while (!parser.EndOfData)
            {
                string[]? fields = parser.ReadFields();
                if (fields is not null) rows.Add(fields);
            }
            return new SpreadsheetPreview(path, headers, rows);
        }
        catch (MalformedLineException exception)
        {
            throw new InvalidDataException(string.Format(PayloadStrings.CsvMalformedLineFormat, parser.ErrorLineNumber, exception.Message), exception);
        }
    }

    private static SpreadsheetPreview PreviewSecRandom(string path)
    {
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8, true);
        using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(PayloadStrings.SecRandomRootObjectRequired);

        bool transfer = root.TryGetProperty("rows", out JsonElement entries);
        if (transfer)
        {
            if (!root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int value) || value != 1)
                throw new InvalidDataException(PayloadStrings.SecRandomUnsupportedVersion);
            if (!root.TryGetProperty("kind", out JsonElement kind) || kind.ValueKind != JsonValueKind.Number || !kind.TryGetInt32(out int kindValue) || kindValue != 0)
                throw new InvalidDataException(PayloadStrings.SecRandomWrongKind);
        }
        else if (!root.TryGetProperty("students", out entries))
            throw new InvalidDataException(PayloadStrings.SecRandomMissingEntries);
        if (entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(PayloadStrings.SecRandomEntriesArrayRequired);

        var rows = new List<IReadOnlyList<string>>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(string.Format(PayloadStrings.SecRandomEntryObjectFormat, rows.Count + 1));
            rows.Add(new[] { ReadText(entry, "name"), ReadText(entry, "id"), ReadText(entry, transfer ? "detailTwo" : "group") });
        }
        return new SpreadsheetPreview(path, new[] { "姓名", "学号", "小组" }, rows);
    }

    private static string ReadText(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(string.Format(PayloadStrings.SecRandomFieldTypeFormat, name));
        return value.GetString()!;
    }
}
