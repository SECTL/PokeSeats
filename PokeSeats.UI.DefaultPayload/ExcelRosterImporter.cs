using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

internal sealed record SpreadsheetPreview(string FilePath, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

internal sealed record ImportResult(IReadOnlyList<Person> Students, int SkippedRows);

internal static class ExcelRosterImporter
{
    public static SpreadsheetPreview Preview(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("未找到指定的 Excel 表格文件。", path);
        if (!string.Equals(Path.GetExtension(path), ".xls", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("仅支持导入 Excel 工作簿格式（.xlsx 或 .xls）。");

        using FileStream stream = File.OpenRead(path);
        IWorkbook workbook = IsXlsx(path) ? new XSSFWorkbook(stream) : new HSSFWorkbook(stream);
        try
        {
            if (workbook.NumberOfSheets == 0)
            throw new InvalidDataException("所选 Excel 工作簿中没有任何工作表。");

            ISheet sheet = workbook.GetSheetAt(0);
            IRow? headerRow = sheet.GetRow(sheet.FirstRowNum);
            if (headerRow is null || headerRow.LastCellNum <= 0)
            throw new InvalidDataException("工作表中缺少表头列标题行。");

            var headers = new List<string>();
            for (int column = 0; column < headerRow.LastCellNum; column++)
            {
                string value = ReadCell(headerRow.GetCell(column));
                headers.Add(string.IsNullOrWhiteSpace(value) ? $"第 {column + 1} 列" : value.Trim());
            }

            var rows = new List<IReadOnlyList<string>>();
            for (int rowIndex = sheet.FirstRowNum + 1; rowIndex <= sheet.LastRowNum && rows.Count < 8; rowIndex++)
            {
                IRow? row = sheet.GetRow(rowIndex);
                if (row is null) continue;
                rows.Add(Enumerable.Range(0, headers.Count).Select(column => ReadCell(row.GetCell(column))).ToArray());
            }

            return new SpreadsheetPreview(path, headers, rows);
        }
        finally
        {
            workbook.Close();
        }
    }

    public static ImportResult Import(SpreadsheetPreview preview, int nameColumn, int studentNumberColumn)
    {
        if (nameColumn < 0 || nameColumn >= preview.Headers.Count)
            throw new ArgumentOutOfRangeException(nameof(nameColumn));
        if (studentNumberColumn < -1 || studentNumberColumn >= preview.Headers.Count)
            throw new ArgumentOutOfRangeException(nameof(studentNumberColumn));

        using FileStream stream = File.OpenRead(preview.FilePath);
        IWorkbook workbook = IsXlsx(preview.FilePath) ? new XSSFWorkbook(stream) : new HSSFWorkbook(stream);
        try
        {
            ISheet sheet = workbook.GetSheetAt(0);
            IRow? headerRow = sheet.GetRow(sheet.FirstRowNum);
            if (headerRow is null)
            throw new InvalidDataException("工作表中缺少有效的数据表头行。");

            var students = new List<Person>();
            var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int skippedRows = 0;
            for (int rowIndex = sheet.FirstRowNum + 1; rowIndex <= sheet.LastRowNum; rowIndex++)
            {
                IRow? row = sheet.GetRow(rowIndex);
                string name = ReadCell(row?.GetCell(nameColumn)).Trim();
                string number = studentNumberColumn >= 0
                    ? ReadCell(row?.GetCell(studentNumberColumn)).Trim()
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(name))
                {
                    skippedRows++;
                    continue;
                }
                if (!string.IsNullOrWhiteSpace(number) && !numbers.Add(number))
                    throw new InvalidDataException($"检测到重复学号“{number}”，请核对表格后重试。");

                students.Add(new Person(PersonId.New(), name, string.IsNullOrWhiteSpace(number) ? null : number, null));
            }

            return new ImportResult(students, skippedRows);
        }
        finally
        {
            workbook.Close();
        }
    }


    private static bool IsXlsx(string path) => string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);

    private static string ReadCell(ICell? cell)
    {
        if (cell is null) return string.Empty;
        if (cell.CellType == NPOI.SS.UserModel.CellType.Numeric)
            return cell.NumericCellValue.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture);

        return cell.ToString() ?? string.Empty;
    }

}
