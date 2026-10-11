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
    private int displayedRun = -1;
    private float elapsed;

    public SeatingResultView(SeatingSession session) => this.session = session;
    public string Id => ViewId;
    public string DisplayName => "排座结果与座位表";
    public bool InitiallyVisible => false;
    public Vector2? MinimumSize => new(800, 560);

    public void DrawContent(float deltaSeconds)
    {
        session.Update();
        if (displayedRun != session.RunNumber)
        {
            displayedRun = session.RunNumber; elapsed = 0; occupants.Clear();
        }
        if (session.IsRunning)
        {
            elapsed += deltaSeconds;
            string dots = ((int)(elapsed * 2) % 4) switch { 0 => "", 1 => ".", 2 => "..", _ => "..." };
            ImGui.Text($"正在智能规划座位方案{dots}");
            ImGui.TextWrapped("求解引擎正在分析规则约束并生成最优指派方案，请稍候；在此期间可正常进行其他操作。");
            ImGui.TextDisabled($"已耗时 {elapsed:0.0} 秒");
            session.Launch();
            return;
        }
        if (session.Error is { } error) { ImGui.TextWrapped(error); return; }
        if (session.Result is not { } result || session.Request is not { } request)
        {
            ImGui.TextWrapped("点击顶部导航栏中的“开始自动排座”后，在此查看求解进度与可视化座位表。"); return;
        }
        if (!session.IsCurrent)
            ImGui.TextWrapped("提示：班级名单、教室布局或排座规则已发生变更；当前展示的是上一次的排座结果，请重新点击“开始自动排座”。");
        if (!result.IsSuccess)
        {
            ImGui.TextWrapped(result.Status switch
            {
                SolveStatus.Unsatisfiable => "【排座无解】当前设置的规则组合在逻辑上存在不可调和的冲突，无法排出有效方案。",
                SolveStatus.Rejected => "【预检查未通过】规则配置中存在基础逻辑错误（如越界、重复固定等），已阻止求解。",
                _ => "【求解未决】在当前时限或资源内未能确定是否存在可行座位分配方案。"
            });
            if (result.Detail is { } detail) ImGui.TextWrapped(detail);
            foreach (var report in result.PreCheck.Global.Where(item => item.Mark == PreCheckErrorMark.Error))
                ImGui.TextWrapped($"全局异常：{ErrorText(report.Error)}");
            foreach (var report in result.PreCheck.PerRule.Where(item => item.Mark == PreCheckErrorMark.Error))
                ImGui.TextWrapped($"规则异常：{ErrorText(report.Error)}");
            ImGui.TextWrapped(session.ConflictCount > 0
                ? $"已在“规则编辑”面板高亮标红 {session.ConflictCount} 条核心冲突规则。上述规则共同导致了无解，请检查并调整其约束。"
                : "未能收敛至特定的单条冲突规则；请重点核查班级总人数、教室可用课桌总数或单排容量上限。");
            return;
        }
        if (occupants.Count == 0 && result.Assignments.Count > 0)
        {
            var people = request.Roster.ToDictionary(person => person.Id);
            foreach (SeatAssignment assignment in result.Assignments)
                occupants.Add(assignment.Seat, people[assignment.Person]);
        }
        ImGui.Text($"成功安排学生：{result.Assignments.Count} 人 · 教室剩余空座：{request.Layout.GetDeskCount() - result.Assignments.Count} 个");
        ImGui.TextWrapped("方位说明：讲台及教室前方位于上方；行列编号从 1 起算。每个格子均完整显示学生姓名及精确座次，超出显示范围可滚动浏览。");
        DrawChart(request.Layout);
    }

    private void DrawChart(LayoutGrid layout)
    {
        if (!ImGui.BeginChild("##seating-chart", Vector2.Zero, ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        { ImGui.EndChild(); return; }
        Vector2 nameSize = ImGui.CalcTextSize("（空座）");
        foreach (Person person in occupants.Values)
            nameSize = Vector2.Max(nameSize, ImGui.CalcTextSize(person.Name));
        Vector2 available = ImGui.GetContentRegionAvail();
        float rowHeader = Math.Max(UiScale.Pixels(52), ImGui.CalcTextSize($"第{layout.Rows}排").X + UiScale.Pixels(12));
        float columnHeader = ImGui.GetTextLineHeightWithSpacing() + UiScale.Pixels(8);
        float width = Math.Max(nameSize.X + UiScale.Pixels(24), Math.Max(UiScale.Pixels(80), (available.X - rowHeader - 1) / layout.Cols));
        float height = Math.Max(UiScale.Pixels(60), nameSize.Y + ImGui.GetTextLineHeightWithSpacing() + UiScale.Pixels(20));
        Vector2 cell = new(width, height);
        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 gridOrigin = origin + new Vector2(rowHeader, columnHeader);
        ImGui.InvisibleButton("##seating-chart-surface", new Vector2(rowHeader + width * layout.Cols, columnHeader + height * layout.Rows));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        for (int col = 0; col < layout.Cols; col++)
            CenterText(draw, origin + new Vector2(rowHeader + col * width, 0), new Vector2(width, columnHeader), $"第{col + 1}列", TextColor);
        for (int row = 0; row < layout.Rows; row++)
        {
            CenterText(draw, origin + new Vector2(0, columnHeader + row * height), new Vector2(rowHeader, height), $"第{row + 1}排", TextColor);
            for (int col = 0; col < layout.Cols; col++)
            {
                GridPos seat = new(row, col);
                Vector2 min = gridOrigin + new Vector2(col * width, row * height);
                CellType type = layout.TypeAt(seat);
                bool occupied = occupants.TryGetValue(seat, out Person? person);
                uint background = type switch
                {
                    CellType.Desk => occupied ? Color(.82f, .92f, .85f) : Color(.93f, .96f, .98f),
                    CellType.Podium => Color(.88f, .83f, .94f),
                    CellType.Aisle => Color(.82f, .84f, .86f),
                    CellType.NarrowAisle => Color(.95f, .89f, .73f),
                    _ => Color(.94f, .94f, .94f)
                };
                draw.AddRectFilled(min, min + cell, background);
                draw.AddRect(min, min + cell, Color(.56f, .60f, .64f));
                if (occupied)
                {
                    CenterText(draw, min, new Vector2(width, height * .6f), person!.Name, TextColor);
                    CenterText(draw, min + new Vector2(0, height * .6f), new Vector2(width, height * .4f), $"第 {row + 1} 排 第 {col + 1} 列", Color(.35f, .4f, .44f));
                }
                else
                {
                    string label = type switch { CellType.Desk => "（空座）", CellType.Podium => "【讲台】", CellType.Aisle => "主走道", CellType.NarrowAisle => "行间走道", _ => "" };
                    CenterText(draw, min, cell, label, TextColor);
                }
            }
        }
        ImGui.EndChild();
    }

    private static string ErrorText(PreCheckError error) => error switch
    {
        PreCheckError.DanglingRef => "规则关联的学生不存在于当前班级花名册中。",
        PreCheckError.OverCapacity => "班级学生总人数超出了教室课桌容量，或某一行列人数超出限制。",
        PreCheckError.DuplicatePin => "多个学生被同时固定在同一个课桌座位上。",
        PreCheckError.PinnedSeatCollision => "固定座位的坐标并非学生课桌（例如被设置为讲台或走道）。",
        PreCheckError.OutOfBound => "座位的排号或列号已超出当前教室设定的网格范围。",
        PreCheckError.AlwaysFalse => "该规则在此教室布局下在数学上无法达成。",
        PreCheckError.AlwaysTrue => "该规则在当前教室布局下已自然满足。",
        _ => error.ToString()
    };
    private static uint TextColor => Color(.12f, .16f, .20f);
    private static uint Color(float r, float g, float b) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, 1));
    private static void CenterText(ImDrawListPtr draw, Vector2 min, Vector2 size, string text, uint color)
        => draw.AddText(min + (size - ImGui.CalcTextSize(text)) / 2, color, text);
}
