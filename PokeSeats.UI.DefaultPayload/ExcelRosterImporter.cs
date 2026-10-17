using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace PokeSeats.UI.DefaultPayload;

internal sealed record SpreadsheetPreview(string FilePath, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

internal static class ExcelRosterImporter
{
    public static SpreadsheetPreview Preview(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(PayloadStrings.ExcelFileNotFound, path);
        if (!string.Equals(Path.GetExtension(path), ".xls", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(PayloadStrings.ExcelUnsupportedFormat);

        using FileStream stream = File.OpenRead(path);
        IWorkbook workbook = IsXlsx(path) ? new XSSFWorkbook(stream) : new HSSFWorkbook(stream);
        try
        {
            if (workbook.NumberOfSheets == 0)
            throw new InvalidDataException(PayloadStrings.ExcelNoWorksheets);

            ISheet sheet = workbook.GetSheetAt(0);
            IRow? headerRow = sheet.GetRow(sheet.FirstRowNum);
            if (headerRow is null || headerRow.LastCellNum <= 0)
            throw new InvalidDataException(PayloadStrings.ExcelMissingHeader);

            var headers = new List<string>();
            for (int column = 0; column < headerRow.LastCellNum; column++)
            {
                string value = ReadCell(headerRow.GetCell(column));
                headers.Add(string.IsNullOrWhiteSpace(value) ? string.Format(PayloadStrings.SpreadsheetColumnFallbackHeaderFormat, column + 1) : value.Trim());
            }

            var rows = new List<IReadOnlyList<string>>();
            for (int rowIndex = sheet.FirstRowNum + 1; rowIndex <= sheet.LastRowNum; rowIndex++)
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



    private static bool IsXlsx(string path) => string.Equals(Path.GetExtension(path), ".xlsx", StringComparison.OrdinalIgnoreCase);

    private static string ReadCell(ICell? cell)
    {
        if (cell is null) return string.Empty;
        if (cell.CellType == NPOI.SS.UserModel.CellType.Numeric)
            return cell.NumericCellValue.ToString("0.################", System.Globalization.CultureInfo.InvariantCulture);

        return cell.ToString() ?? string.Empty;
    }

}
