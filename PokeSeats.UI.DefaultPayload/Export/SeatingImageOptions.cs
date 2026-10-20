using System.Numerics;
using PokeSeats.IR.Base;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>Per-cell text and fill override used when exporting a seating chart.</summary>
internal sealed class SeatingCellStyle
{
    /// <summary>Explicit cell text; <see langword="null"/> keeps the generated default, an empty string leaves the cell blank.</summary>
    public string? Text;

    /// <summary>Explicit cell fill; <see langword="null"/> keeps the fill for the cell type.</summary>
    public Vector4? Color;
}

/// <summary>Controls the appearance and content of an exported seating-chart PNG.</summary>
internal sealed class SeatingImageOptions
{
    /// <summary>Rotate the chart by 180 degrees so the podium is drawn at the bottom.</summary>
    public bool PodiumAtBottom = false;

    public bool ShowName = true;
    public bool ShowStudentNumber = false;
    public bool ShowGroup = false;
    public bool ShowCoordinates = false;
    public bool ShowRowNumbers = true;
    public bool ShowColumnNumbers = true;

    /// <summary>Optional chart heading; empty omits the heading entirely.</summary>
    public string Title = "";

    public Vector4 Background = new(1f, 1f, 1f, 1f);
    public Vector4 TextColor = new(.1f, .15f, .2f, 1f);
    public Vector4 BorderColor = new(.65f, .7f, .75f, 1f);
    public Vector4 OccupiedColor = new(.85f, .95f, .88f, 1f);
    public Vector4 VacantColor = new(.92f, .95f, 1f, 1f);
    public Vector4 PodiumColor = new(.85f, .88f, .93f, 1f);
    public Vector4 AisleColor = new(1f, 1f, 1f, 1f);
    public Vector4 NarrowAisleColor = new(1f, .96f, .84f, 1f);
    public Vector4 EmptyColor = new(1f, 1f, 1f, 1f);

    /// <summary>Overrides keyed by the layout's original seat position.</summary>
    public Dictionary<GridPos, SeatingCellStyle> Cells = new();

    /// <summary>
    /// Text for one cell. A <see cref="SeatingCellStyle.Text"/> override wins even when empty.
    /// Occupied desks join the enabled person fields with newlines; other cells use the
    /// existing panel labels. Coordinates always use the original grid position.
    /// </summary>
    public string GetText(GridPos position, CellType type, Person? person)
    {
        if (Cells.TryGetValue(position, out SeatingCellStyle? style) && style.Text is not null)
            return style.Text;

        if (type == CellType.Desk)
        {
            if (person is null)
            {
                string vacant = PayloadStrings.SeatingEmptySeatLabel;
                return ShowCoordinates ? vacant + "\n" + CoordinateText(position) : vacant;
            }

            var lines = new List<string>(4);
            if (ShowName && !string.IsNullOrWhiteSpace(person.Name))
                lines.Add(person.Name);
            if (ShowStudentNumber && !string.IsNullOrWhiteSpace(person.StudentNumber))
                lines.Add(person.StudentNumber);
            if (ShowGroup && !string.IsNullOrWhiteSpace(person.Group))
                lines.Add(person.Group);
            if (ShowCoordinates)
                lines.Add(CoordinateText(position));
            return string.Join("\n", lines);
        }

        return type switch
        {
            CellType.Podium => PayloadStrings.SeatingPodiumLabel,
            CellType.Aisle => PayloadStrings.SeatingMainAisleLabel,
            CellType.NarrowAisle => PayloadStrings.SeatingRowAisleLabel,
            _ => string.Empty
        };
    }

    /// <summary>Fill for one cell; a <see cref="SeatingCellStyle.Color"/> override wins.</summary>
    public Vector4 GetColor(GridPos position, CellType type, Person? person)
    {
        if (Cells.TryGetValue(position, out SeatingCellStyle? style) && style.Color is { } overrideColor)
            return overrideColor;

        return type switch
        {
            CellType.Desk => person is null ? VacantColor : OccupiedColor,
            CellType.Podium => PodiumColor,
            CellType.Aisle => AisleColor,
            CellType.NarrowAisle => NarrowAisleColor,
            _ => EmptyColor
        };
    }

    /// <summary>
    /// Maps a displayed grid coordinate back to the layout. With the podium at the bottom the
    /// chart is rotated 180 degrees, so both the row and the column are reversed.
    /// </summary>
    public GridPos SourcePosition(LayoutGrid layout, int displayRow, int displayCol)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if ((uint)displayRow >= (uint)layout.Rows)
            throw new ArgumentOutOfRangeException(nameof(displayRow));
        if ((uint)displayCol >= (uint)layout.Cols)
            throw new ArgumentOutOfRangeException(nameof(displayCol));

        return PodiumAtBottom
            ? new GridPos(layout.Rows - 1 - displayRow, layout.Cols - 1 - displayCol)
            : new GridPos(displayRow, displayCol);
    }

    private static string CoordinateText(GridPos position)
        => string.Format(PayloadStrings.SeatingSeatCoordinateFormat, position.Row + 1, position.Col + 1);
}
