using System.Numerics;
using ImGuiNET;
using PokeSeats.Core.Analyzer;
using PokeSeats.Core.Solver;
using PokeSeats.IR.Base;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class SeatingResultView : IUiView
{
    public const string ViewId = "pokeseats.defaultpayload.seating-result";
    private readonly SeatingSession session;
    private readonly Dictionary<GridPos, Person> occupants = new();
    private readonly SeatingImageExportDialog imageExport;
    private readonly SeatingArchiveExportDialog archiveExport;
    private UiDesignTokens _designTokens = UiDesignTokens.Light;
    private int displayedRun = -1;
    private float elapsed;

    public SeatingResultView(SeatingSession session, string dataDirectory)
    {
        this.session = session;
        imageExport = new SeatingImageExportDialog(dataDirectory);
        archiveExport = new SeatingArchiveExportDialog(dataDirectory);
    }
    public string Id => ViewId;
    public string DisplayName => PayloadStrings.SeatingResultViewTitle;
    public bool InitiallyVisible => false;
    public Vector2? MinimumSize => new(800, 560);

    public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
    {
        _designTokens = designTokens;
        session.Update();
        if (displayedRun != session.RunNumber)
        {
            displayedRun = session.RunNumber; elapsed = 0; occupants.Clear();
            imageExport.ResetResult();
            archiveExport.ResetResult();
        }
        if (session.IsRunning)
        {
            elapsed += deltaSeconds;
            string dots = ((int)(elapsed * 2) % 4) switch { 0 => "", 1 => ".", 2 => "..", _ => "..." };
            ImGui.Text(string.Format(PayloadStrings.SeatingPlanningFormat, dots));
            ImGui.TextWrapped(PayloadStrings.SeatingPlanningHint);
            ImGui.TextDisabled(string.Format(PayloadStrings.SeatingElapsedFormat, elapsed));
            session.Launch();
            return;
        }
        if (session.Error is { } error) { ImGui.TextWrapped(error); return; }
        if (session.Result is not { } result || session.Request is not { } request)
        {
            ImGui.TextWrapped(PayloadStrings.SeatingIdleHint); return;
        }
        if (!session.IsCurrent)
            ImGui.TextWrapped(PayloadStrings.SeatingStaleHint);
        if (!result.IsSuccess)
        {
            ImGui.TextWrapped(result.Status switch
            {
                SolveStatus.Unsatisfiable => PayloadStrings.SeatingStatusUnsatisfiable,
                SolveStatus.Rejected => PayloadStrings.SeatingStatusRejected,
                _ => PayloadStrings.SeatingStatusUndetermined
            });
            if (result.Detail is { } detail) ImGui.TextWrapped(detail);
            foreach (var report in result.PreCheck.Global.Where(item => item.Mark == PreCheckErrorMark.Error))
                ImGui.TextWrapped(string.Format(PayloadStrings.SeatingGlobalErrorFormat, ErrorText(report.Error)));
            foreach (var report in result.PreCheck.PerRule.Where(item => item.Mark == PreCheckErrorMark.Error))
                ImGui.TextWrapped(string.Format(PayloadStrings.SeatingRuleErrorFormat, ErrorText(report.Error)));
            ImGui.TextWrapped(session.ConflictCount > 0
                ? string.Format(PayloadStrings.SeatingConflictRulesFormat, session.ConflictCount)
                : PayloadStrings.SeatingConflictUnresolvedHint);
            return;
        }
        if (occupants.Count == 0 && result.Assignments.Count > 0)
        {
            var people = request.Roster.ToDictionary(person => person.Id);
            foreach (SeatAssignment assignment in result.Assignments)
                occupants.Add(assignment.Seat, people[assignment.Person]);
        }
        ImGui.Text(string.Format(PayloadStrings.SeatingAssignedFormat, result.Assignments.Count, request.Layout.GetDeskCount() - result.Assignments.Count));
        ImGui.TextWrapped(PayloadStrings.SeatingChartHint);
        if (ImGui.Button(ExportStrings.Open)) imageExport.Open();
        ImGui.SameLine();
        if (ImGui.Button(ExportStrings.ArchiveOpen)) archiveExport.Open();
        archiveExport.Draw(request, result);
        imageExport.Draw(request.Layout, occupants);
        DrawChart(request.Layout);
    }

    private void DrawChart(LayoutGrid layout)
    {
        if (!ImGui.BeginChild("##seating-chart", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        { ImGui.EndChild(); return; }
        Vector2 nameSize = ImGui.CalcTextSize(PayloadStrings.SeatingEmptySeatLabel);
        foreach (Person person in occupants.Values)
            nameSize = Vector2.Max(nameSize, ImGui.CalcTextSize(person.Name));
        Vector2 available = ImGui.GetContentRegionAvail();
        float rowHeader = Math.Max(UiScale.Pixels(52), ImGui.CalcTextSize(string.Format(PayloadStrings.SeatingRowHeaderFormat, layout.Rows)).X + UiScale.Pixels(12));
        float columnHeader = ImGui.GetTextLineHeightWithSpacing() + UiScale.Pixels(_designTokens.Metrics.SpaceMedium);
        float width = Math.Max(nameSize.X + UiScale.Pixels(24), Math.Max(UiScale.Pixels(80), (available.X - rowHeader - 1) / layout.Cols));
        float height = Math.Max(UiScale.Pixels(60), nameSize.Y + ImGui.GetTextLineHeightWithSpacing() + UiScale.Pixels(20));
        Vector2 cell = new(width, height);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 gridOrigin = origin + new Vector2(rowHeader, columnHeader);
        ImGui.InvisibleButton("##seating-chart-surface", new Vector2(rowHeader + width * layout.Cols, columnHeader + height * layout.Rows));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        for (int col = 0; col < layout.Cols; col++)
            CenterText(draw, origin + new Vector2(rowHeader + col * width, 0), new Vector2(width, columnHeader), string.Format(PayloadStrings.SeatingColumnHeaderFormat, col + 1), Color(_designTokens.Colors.Text));
        for (int row = 0; row < layout.Rows; row++)
        {
            CenterText(draw, origin + new Vector2(0, columnHeader + row * height), new Vector2(rowHeader, height), string.Format(PayloadStrings.SeatingRowHeaderFormat, row + 1), Color(_designTokens.Colors.Text));
            for (int col = 0; col < layout.Cols; col++)
            {
                GridPos seat = new(row, col);
                Vector2 min = gridOrigin + new Vector2(col * width, row * height);
                CellType type = layout.TypeAt(seat);
                bool occupied = occupants.TryGetValue(seat, out Person? person);
                uint background = type switch
                {
                    CellType.Desk => Color(occupied ? _designTokens.Colors.SuccessSurface : _designTokens.Colors.AccentSurface),
                    CellType.Podium => Color(_designTokens.Colors.SecondarySurface),
                    CellType.Aisle => Color(_designTokens.Colors.Surface),
                    CellType.NarrowAisle => Color(_designTokens.Colors.WarningSurface),
                    _ => Color(_designTokens.Colors.Canvas)
                };
                draw.AddRectFilled(min, min + cell, background);
                draw.AddRect(min, min + cell, Color(_designTokens.Colors.Border), 0, ImDrawFlags.None, UiScale.Pixels(_designTokens.Metrics.BorderWidth));
                if (occupied)
                {
                    CenterText(draw, min, new Vector2(width, height * .6f), person!.Name, Color(_designTokens.Colors.Text));
                    CenterText(draw, min + new Vector2(0, height * .6f), new Vector2(width, height * .4f), string.Format(PayloadStrings.SeatingSeatCoordinateFormat, row + 1, col + 1), Color(_designTokens.Colors.TextMuted));
                }
                else
                {
                    string label = type switch { CellType.Desk => PayloadStrings.SeatingEmptySeatLabel, CellType.Podium => PayloadStrings.SeatingPodiumLabel, CellType.Aisle => PayloadStrings.SeatingMainAisleLabel, CellType.NarrowAisle => PayloadStrings.SeatingRowAisleLabel, _ => "" };
                    CenterText(draw, min, cell, label, Color(_designTokens.Colors.Text));
                }
            }
        }
        ImGui.EndChild();
    }

    private static string ErrorText(PreCheckError error) => error switch
    {
        PreCheckError.DanglingRef => PayloadStrings.SeatingErrorDanglingRef,
        PreCheckError.OverCapacity => PayloadStrings.SeatingErrorOverCapacity,
        PreCheckError.DuplicatePin => PayloadStrings.SeatingErrorDuplicatePin,
        PreCheckError.PinnedSeatCollision => PayloadStrings.SeatingErrorPinnedSeatCollision,
        PreCheckError.OutOfBound => PayloadStrings.SeatingErrorOutOfBound,
        PreCheckError.AlwaysFalse => PayloadStrings.SeatingErrorAlwaysFalse,
        PreCheckError.AlwaysTrue => PayloadStrings.SeatingErrorAlwaysTrue,
        _ => error.ToString()
    };
    private static uint Color(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);
    private static void CenterText(ImDrawListPtr draw, Vector2 min, Vector2 size, string text, uint color)
        => draw.AddText(min + (size - ImGui.CalcTextSize(text)) / 2, color, text);
}
