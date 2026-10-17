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
            throw new FileNotFoundException("未找到指定的名单文件。", path);

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".xls" or ".xlsx" => ExcelRosterImporter.Preview(path),
            ".csv" => PreviewCsv(path),
            ".json" => PreviewSecRandom(path),
            _ => throw new InvalidDataException("支持 Excel（.xlsx、.xls）、CSV 和 SecRandom JSON 名单。")
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
                throw new InvalidDataException($"检测到重复学号“{number}”，请核对名单后重试。");
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
                throw new InvalidDataException("CSV 文件中缺少表头列标题行。");
            string[] headers = header.Select((value, index) => string.IsNullOrWhiteSpace(value) ? $"第 {index + 1} 列" : value.Trim()).ToArray();
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
            throw new InvalidDataException($"CSV 第 {parser.ErrorLineNumber} 行格式无效：{exception.Message}", exception);
        }
    }

    private static SpreadsheetPreview PreviewSecRandom(string path)
    {
        using var reader = new StreamReader(path, System.Text.Encoding.UTF8, true);
        using JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("SecRandom 名单必须是包含 students 或 rows 的 JSON 对象。");

        bool transfer = root.TryGetProperty("rows", out JsonElement entries);
        if (transfer)
        {
            if (!root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int value) || value != 1)
                throw new InvalidDataException("仅支持 SecRandom version 为 1 的名单传输文档。");
            if (!root.TryGetProperty("kind", out JsonElement kind) || kind.ValueKind != JsonValueKind.Number || !kind.TryGetInt32(out int kindValue) || kindValue != 0)
                throw new InvalidDataException("所选 SecRandom 传输文档不是学生名单（kind 必须为 0）。");
        }
        else if (!root.TryGetProperty("students", out entries))
            throw new InvalidDataException("未识别到 SecRandom students 名单或 rows 传输文档。");
        if (entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("SecRandom 名单记录必须是数组。");

        var rows = new List<IReadOnlyList<string>>();
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"SecRandom 第 {rows.Count + 1} 条记录必须是对象。");
            rows.Add(new[] { ReadText(entry, "name"), ReadText(entry, "id"), ReadText(entry, transfer ? "detailTwo" : "group") });
        }
        return new SpreadsheetPreview(path, new[] { "姓名", "学号", "小组" }, rows);
    }

    private static string ReadText(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
            return string.Empty;
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"SecRandom 字段 {name} 必须为字符串或 null。");
        return value.GetString()!;
    }
}
