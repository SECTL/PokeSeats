using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PokeSeats.Core.Solver;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

internal static class SeatingArchiveExporter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void SaveZip(string path, SolverRequest request, SolverResult result)
    {
        if (!result.IsSuccess)
            throw new InvalidOperationException(ExportStrings.ArchiveResultRequired);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (Stream stream = archive.CreateEntry("students.json").Open())
                    StudentRoster.WriteJson(stream, request.Roster);
                using (Stream stream = archive.CreateEntry("rules.json").Open())
                    RuleDocument.WriteJson(stream, request.Rules);
                using (Stream stream = archive.CreateEntry("seating.json").Open())
                    WriteSeating(stream, request.Layout, result.Assignments);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void WriteSeating(Stream stream, LayoutGrid layout, IReadOnlyList<SeatAssignment> assignments)
    {
        var cells = new CellType[layout.Rows][];
        for (int row = 0; row < layout.Rows; row++)
        {
            cells[row] = new CellType[layout.Cols];
            for (int col = 0; col < layout.Cols; col++)
                cells[row][col] = layout.TypeAt(new GridPos(row, col));
        }
        var seats = assignments.Select(assignment => new StoredAssignment(
            assignment.Person.Value, assignment.Seat.Row, assignment.Seat.Col));
        JsonSerializer.Serialize(stream, new StoredSeating(1, layout.Rows, layout.Cols, cells, seats), SerializerOptions);
    }

    private sealed record StoredSeating(int Version, int Rows, int Cols, CellType[][] Cells,
        IEnumerable<StoredAssignment> Assignments);
    private sealed record StoredAssignment(Guid PersonId, int Row, int Col);
}
