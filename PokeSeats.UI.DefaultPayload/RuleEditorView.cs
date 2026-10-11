using System.Numerics;
using ImGuiNET;
using PokeSeats.Core.Analyzer;
using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class RuleEditorView : IUiView
{
    private static readonly string[] Categories = { "固定特定座位", "限定就座区域", "学生相对位置", "单排行列人数" };
    private static readonly (RelationKind Kind, string Text)[] Relations =
    {
        (RelationKind.ExactOffset, "指定相对偏移"), (RelationKind.InFrontOf, "坐在正前方"),
        (RelationKind.Behind, "坐在正后方"), (RelationKind.LeftOf, "坐在正左侧"),
        (RelationKind.RightOf, "坐在正右侧"), (RelationKind.SameRow, "安排在同一排"),
        (RelationKind.SameColumn, "安排在同一列"), (RelationKind.Proximity, "相邻就座")
    };
    private static readonly string[] AdjacencyLabels = { "横向（左右）", "四向（前后左右）", "八向（含斜角）" };
    private readonly StudentRoster roster;
    private readonly StudentSearchIndex people;
    private readonly LayoutDocument layout;
    private readonly RuleDocument document;
    private readonly string storagePath;
    private readonly SeatingSession seating;
    private RuleEditorState state = new();
    private string personSearch = "";
    private string referenceSearch = "";
    private string status;
    private Vector2 pan;
    private float zoom = 1;
    private bool regionDragging;
    private bool relationDragging;
    private RelationKind? hoveredRelation;
    private PreCheckResult? checks;
    private Rule? checkedDraft;
    private int checkedLayout = -1;
    private int checkedRoster = -1;
    private bool checksDirty = true;

    public RuleEditorView(StudentRoster roster, LayoutDocument layout, RuleDocument document, string storagePath, string? loadMessage, SeatingSession seating)
    {
        this.roster = roster; this.layout = layout; this.document = document; this.storagePath = storagePath;
        this.seating = seating;
        people = new StudentSearchIndex(roster);
        status = loadMessage ?? "请在左侧选择规则类型并配置约束条件，点击“保存规则”即可生效。";
    }

    public string Id => "pokeseats.defaultpayload.rules";
    public string DisplayName => "规则编辑";
    public Vector2? MinimumSize => new(920, 620);

    public void DrawContent(float deltaSeconds)
    {
        seating.Update();
        if (ImGui.Button("新建规则")) NewDraft();
        ImGui.SameLine();
        ImGui.TextDisabled($"当前生效：{document.Rules.Count} 条排座规则");
        ImGui.SameLine();
        Rule? draft = state.Build();
        UpdateChecks(draft);
        ImGui.BeginDisabled(draft is null || !CanSave(draft));
        if (ImGui.Button(state.Editing.HasValue ? "保存修改" : "保存规则")) Save(draft!);
        ImGui.EndDisabled();
        ImGui.Separator();
        bool bodyVisible = ImGui.BeginChild("##rule-editor-body", new Vector2(0, UiScale.Pixels(-42)));
        if (bodyVisible && ImGui.BeginTable("##rule-editor-columns", 2, ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("规则配置", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("已设规则清单", ImGuiTableColumnFlags.WidthStretch, 1.3f);
            ImGui.TableNextRow(); ImGui.TableNextColumn();
            bool editorVisible = ImGui.BeginChild("##rule-editor-form", new Vector2(0, 0));
            if (editorVisible) DrawEditor();
            ImGui.EndChild();
            ImGui.TableNextColumn(); DrawList();
            ImGui.EndTable();
        }
        ImGui.EndChild();
        ImGui.TextWrapped(status);
    }

    private void DrawEditor()
    {
        int category = state.Category;
        ImGui.SetNextItemWidth(UiScale.Pixels(160));
        if (ImGui.Combo("规则类型", ref category, Categories, Categories.Length))
        {
            NewDraft(); state.Category = category;
        }
        if (state.Category != 3)
        {
            DrawPersonPicker(state.Category == 2 ? "基准学生 A" : "目标学生", ref state.Person, ref personSearch, state.Category == 2 ? state.Reference : null);
            if (state.Category == 2)
                DrawPersonPicker("参照学生 B", ref state.Reference, ref referenceSearch, state.Person);
        }
        if (state.Category is 1 or 2)
        {
            ImGui.Checkbox(state.Category == 2 ? "反向限制（如：“相邻”改为“不可相邻”）" : "禁止就座（禁止安排在所选区域内）", ref state.Negated);
        }
        if (state.Category == 2)
        {
            DrawRelationControls();
            DrawRelativeCanvas();
        }
        else if (state.Category == 3)
            DrawCapacityControls();

        Rule? draft = state.Build();
        state.UpdatePreview(layout, state.BuildPreview(hoveredRelation));
        ImGui.TextWrapped(draft is null
            ? state.Category == 2 ? "未选定学生时可先在下方画布预览相对空间关系；选齐两名学生后方可保存。" : "请在下方配置具体的排座条件。"
            : RuleText.Describe(draft, people));
        UpdateChecks(draft);
        DrawChecks(draft?.Id);
        if (state.Category == 2)
            ImGui.TextWrapped("说明：下图用于直观预览规则。点击任意课桌放置基准学生 A，高亮绿色课桌即为允许学生 B 就座的位置。");
        else if (state.Category == 1)
            ImGui.TextWrapped("说明：请在教室网格中按住鼠标左键框选区域；高亮绿色课桌为该规则允许就座的位置。");
        else if (state.Category == 0)
            ImGui.TextWrapped("说明：请在教室网格中点击目标课桌，将该学生固定在该座位。");
        else
            ImGui.TextWrapped("说明：请点击教室中的任意课桌，选定要设置容量上限的对应排或列。");
        DrawClassroom();
        if (state.Category == 2)
            ImGui.TextWrapped($"当前教室共 {state.ReferenceSeats.Count} 处有效课桌；当前临时基准 A 下，有 {state.Allowed.Count} 处课桌符合条件。");
    }

    private void DrawPersonPicker(string label, ref PersonId? selected, ref string query, PersonId? excluded)
    {
        Person? person = selected.HasValue ? people.Get(selected.Value) : null;
        string preview = person is null ? "（点击选取学生）" : $"{person.Name} · {person.StudentNumber ?? "无学号"}";
        ImGui.SetNextItemWidth(UiScale.Pixels(320));
        if (!ImGui.BeginCombo(label, preview)) return;
        ImGui.SetNextItemWidth(UiScale.Pixels(300));
        ImGui.InputTextWithHint($"搜索##{label}", "输入姓名 / 学号 / 拼音 / 首字母检索", ref query, 256);
        if (ImGui.BeginChild($"人员结果##{label}", new Vector2(0, UiScale.Pixels(170))))
        {
            foreach (Person candidate in people.Find(query))
            {
                if (candidate.Id == excluded) continue;
                string text = $"{candidate.Name} · {candidate.StudentNumber ?? "无学号"}##{candidate.Id.Value}";
                if (ImGui.Selectable(text, selected == candidate.Id))
                {
                    selected = candidate.Id; ImGui.CloseCurrentPopup();
                }
            }
        }
        ImGui.EndChild();
        ImGui.EndCombo();
    }

    private void DrawRelationControls()
    {
        string current = Relations.First(item => item.Kind == state.Relation).Text;
        ImGui.SetNextItemWidth(UiScale.Pixels(160));
        if (ImGui.BeginCombo("相对关系", current))
        {
            foreach (var item in Relations)
                if (ImGui.Selectable(item.Text, item.Kind == state.Relation)) state.Relation = item.Kind;
            ImGui.EndCombo();
        }
        if (state.Relation == RelationKind.Proximity)
        {
            string label = AdjacencyLabels[(int)state.Adjacency];
            ImGui.SetNextItemWidth(UiScale.Pixels(200));
            if (ImGui.BeginCombo("相邻范围", label))
            {
                for (int index = 0; index < AdjacencyLabels.Length; index++)
                    if (ImGui.Selectable(AdjacencyLabels[index], (int)state.Adjacency == index)) state.Adjacency = (AdjacencyMode)index;
                ImGui.EndCombo();
            }
        }
        if (ImGui.Button("视口复位至 A")) { pan = Vector2.Zero; zoom = 1; }
        ImGui.SameLine(); ImGui.TextWrapped("操作：鼠标左键拖拽指定相对方位，松开选取关系；中键平移；滚轮缩放画布。上方朝向教室讲台。");
    }
    private void DrawRelativeCanvas()
    {
        Vector2 size = new(Math.Max(UiScale.Pixels(180), ImGui.GetContentRegionAvail().X), UiScale.Pixels(200));
        Vector2 origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##relative-surface", size);
        bool hovered = ImGui.IsItemHovered();
        var io = ImGui.GetIO();
        if (hovered && ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            pan += io.MouseDelta / UiScale.Current;
        if (hovered && io.MouseWheel != 0)
        {
            float nextZoom = Math.Clamp(zoom * MathF.Pow(1.15f, io.MouseWheel), .45f, 2.5f);
            Vector2 local = (io.MousePos - origin - size / 2) / UiScale.Current;
            pan = local - (local - pan) * (nextZoom / zoom); zoom = nextZoom;
        }
        float cell = UiScale.Pixels(30) * zoom;
        Vector2 center = origin + size / 2 + pan * UiScale.Current;
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            relationDragging = true;
        if (relationDragging)
        {
            if (ImGui.IsKeyPressed(ImGuiKey.Escape)) relationDragging = false;
            else
            {
                state.Dx = (int)MathF.Floor((io.MousePos.X - center.X) / cell + .5f);
                state.Dy = (int)MathF.Floor((io.MousePos.Y - center.Y) / cell + .5f);
                if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                {
                    relationDragging = false;
                    if (state.Dx != 0 || state.Dy != 0) ImGui.OpenPopup("##relative-kind-menu");
                }
                else if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) relationDragging = false;
            }
        }
        hoveredRelation = null;
        if (ImGui.BeginPopup("##relative-kind-menu"))
        {
            ImGui.TextDisabled($"B 相对 A 方位：横向偏 {state.Dx:+0;-0;0} 列，纵向偏 {state.Dy:+0;-0;0} 排");
            foreach (var item in Relations)
            {
                if (item.Kind == RelationKind.Behind && state.Dy <= 0 || item.Kind == RelationKind.InFrontOf && state.Dy >= 0
                    || item.Kind == RelationKind.LeftOf && state.Dx >= 0 || item.Kind == RelationKind.RightOf && state.Dx <= 0) continue;
                if (item.Kind == RelationKind.Proximity && !state.MatchesBaseOffset(state.Dy, state.Dx, item.Kind)) continue;
                string text = item.Kind == RelationKind.ExactOffset ? $"固定偏移位置：{OffsetText()}" : item.Text;
                if (ImGui.MenuItem(text)) state.Relation = item.Kind;
                if (ImGui.IsItemHovered()) hoveredRelation = item.Kind;
            }
            ImGui.EndPopup();
        }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRectFilled(origin, origin + size, Color(.12f, .14f, .17f));
        int firstCol = (int)MathF.Floor((origin.X - center.X) / cell - .5f);
        int lastCol = (int)MathF.Ceiling((origin.X + size.X - center.X) / cell + .5f);
        int firstRow = (int)MathF.Floor((origin.Y - center.Y) / cell - .5f);
        int lastRow = (int)MathF.Ceiling((origin.Y + size.Y - center.Y) / cell + .5f);
        for (int row = firstRow; row <= lastRow; row++)
            for (int col = firstCol; col <= lastCol; col++)
            {
                Vector2 min = center + new Vector2(col - .5f, row - .5f) * cell;
                if (state.MatchesOffset(row, col, hoveredRelation))
                    draw.AddRectFilled(min, min + new Vector2(cell), Color(.15f, .43f, .27f));
                draw.AddRect(min, min + new Vector2(cell), Color(.34f, .39f, .44f));
                if (row == 0 && col == 0)
                {
                    draw.AddRectFilled(min, min + new Vector2(cell), Color(.75f, .48f, .12f));
                    DrawCentered(draw, min, cell, "A");
                }
                if (row == state.Dy && col == state.Dx && (row != 0 || col != 0))
                {
                    draw.AddRect(min + Vector2.One, min + new Vector2(cell - 1), Color(1, .85f, .25f), 0, ImDrawFlags.None, UiScale.Pixels(2));
                    DrawCentered(draw, min, cell, "B");
                }
            }
        draw.PopClipRect();
    }

    private string OffsetText()
        => $"朝{(state.Dy < 0 ? "讲台前" : "教室后")} {Math.Abs(state.Dy)} 排、朝{(state.Dx < 0 ? "门左" : "窗右")} {Math.Abs(state.Dx)} 列";

    private void DrawCapacityControls()
    {
        bool rows = state.AxisType == AxisType.Row;
        if (ImGui.RadioButton("限制排人数（横向）", rows)) state.AxisType = AxisType.Row;
        ImGui.SameLine();
        if (ImGui.RadioButton("限制列人数（纵向）", !rows)) state.AxisType = AxisType.Column;
        int index = state.AxisIndex + 1;
        ImGui.SetNextItemWidth(UiScale.Pixels(150));
        if (ImGui.InputInt(rows ? "目标排号（从1起）" : "目标列号（从1起）", ref index)) state.AxisIndex = index - 1;
        ImGui.Checkbox("设定最少就座人数", ref state.UseMin); ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(120)); ImGui.InputInt("##capacity-min", ref state.Min);
        ImGui.Checkbox("设定最多就座人数", ref state.UseMax); ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(120)); ImGui.InputInt("##capacity-max", ref state.Max);
        if (ImGui.Button("固定为精确人数（最少=最多）")) { state.UseMin = state.UseMax = true; state.Max = state.Min; }
    }

    private void DrawClassroom()
    {
        float footer = state.Category == 2 ? ImGui.GetTextLineHeightWithSpacing() * 2 : 0;
        float height = Math.Max(UiScale.Pixels(120), ImGui.GetContentRegionAvail().Y - footer);
        if (!ImGui.BeginChild("##rules-classroom", new Vector2(0, height), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.AlwaysVerticalScrollbar))
        { ImGui.EndChild(); return; }
        Vector2 available = ImGui.GetContentRegionAvail();
        Vector2 labelSize = ImGui.CalcTextSize("讲");
        float minimumCell = Math.Max(labelSize.X, labelSize.Y) + UiScale.Pixels(8);
        float cell = Math.Max(minimumCell, Math.Max((available.X - 1) / layout.Cols, (available.Y - 1) / layout.Rows)) * .375f;
        Vector2 gridSize = new Vector2(layout.Cols, layout.Rows) * cell;
        Vector2 offset = Vector2.Max(Vector2.Zero, (available - gridSize) / 2);
        ImGui.SetCursorPos(ImGui.GetCursorPos() + offset);
        Vector2 origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##rules-classroom-surface", gridSize);
        bool hovered = ImGui.IsItemHovered();
        Vector2 mouse = ImGui.GetIO().MousePos;
        GridPos pos = new(Math.Clamp((int)((mouse.Y - origin.Y) / cell), 0, layout.Rows - 1), Math.Clamp((int)((mouse.X - origin.X) / cell), 0, layout.Cols - 1));
        if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            switch (state.Category)
            {
                case 0 when layout.TypeAt(pos.Row, pos.Col) == CellType.Desk: state.First = state.Last = pos; break;
                case 1: state.First = state.Last = pos; regionDragging = true; break;
                case 2 when layout.TypeAt(pos.Row, pos.Col) == CellType.Desk: state.PreviewCenter = pos; break;
                case 3: state.AxisIndex = state.AxisType == AxisType.Row ? pos.Row : pos.Col; break;
            }
        }
        if (regionDragging)
        {
            state.Last = pos;
            if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) regionDragging = false;
        }
        state.UpdatePreview(layout, state.BuildPreview(hoveredRelation));
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        for (int row = 0; row < layout.Rows; row++)
            for (int col = 0; col < layout.Cols; col++)
            {
                GridPos p = new(row, col);
                Vector2 min = origin + new Vector2(col, row) * cell;
                CellType type = layout.TypeAt(row, col);
                uint color = type switch
                {
                    CellType.Desk => Color(.18f, .24f, .31f), CellType.Podium => Color(.46f, .23f, .48f),
                    CellType.NarrowAisle => Color(.45f, .34f, .15f), CellType.Aisle => Color(.35f, .37f, .40f),
                    _ => Color(.12f, .14f, .17f)
                };
                if (state.Allowed.Contains(p)) color = Color(.16f, .52f, .30f);
                bool selected = state.Category == 3 ? (state.AxisType == AxisType.Row ? row : col) == state.AxisIndex
                    : state.Category == 1 && state.First is { } start && state.Last is { } end && row >= Math.Min(start.Row, end.Row)
                        && row <= Math.Max(start.Row, end.Row) && col >= Math.Min(start.Col, end.Col) && col <= Math.Max(start.Col, end.Col);
                draw.AddRectFilled(min, min + new Vector2(cell), color);
                draw.AddRect(min, min + new Vector2(cell), Color(.5f, .54f, .58f));
                if (selected) draw.AddRect(min + Vector2.One, min + new Vector2(cell - 1), Color(1, .8f, .2f));
                if (state.Category == 2 && state.PreviewAnchor == p)
                {
                    draw.AddRectFilled(min, min + new Vector2(cell), Color(.75f, .48f, .12f));
                    DrawCentered(draw, min, cell, "A");
                }
                else if (type == CellType.Podium) DrawCentered(draw, min, cell, "讲");
            }
        ImGui.EndChild();
    }

    private void DrawList()
    {
        ImGui.Text("已生效规则列表（点击可调入修改）：");
        if (ImGui.BeginChild("##saved-rule-list", new Vector2(0, UiScale.Pixels(440)), ImGuiChildFlags.Borders))
        {
            foreach (Rule rule in document.Rules)
            {
                ImGui.PushID(rule.Id.value.ToString());
                string text = RuleText.Describe(rule, people);
                bool conflicting = seating.IsConflicting(rule.Id);
                if (conflicting)
                {
                    text = $"[冲突规则] {text}";
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(.80f, .08f, .08f, 1));
                    ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(1, .74f, .74f, 1));
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(1, .82f, .82f, 1));
                }
                if (ImGui.Selectable(text, state.Editing == rule.Id))
                {
                    state.Load(rule);
                    if (state.Last is { } last) state.Last = new GridPos(Math.Min(last.Row, layout.Rows - 1), Math.Min(last.Col, layout.Cols - 1));
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
                if (conflicting) ImGui.PopStyleColor(3);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.BeginDisabled(!state.Editing.HasValue);
        if (ImGui.Button("删除选中的规则")) ImGui.OpenPopup("删除规则确认");
        ImGui.EndDisabled();
        ImGui.BeginDisabled(document.Rules.Count == 0);
        if (ImGui.Button("清空所有规则")) ImGui.OpenPopup("清空所有规则确认");
        ImGui.EndDisabled();
        if (ImGui.BeginPopupModal("清空所有规则确认", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text($"确认清空全部 {document.Rules.Count} 条排座规则？此操作无法撤销。");
            if (ImGui.Button("确认清空"))
            {
                ClearAllRules();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopupModal("删除规则确认", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("确认移除当前选中的这条排座规则？");
            if (ImGui.Button("确认删除") && state.Editing is { } id)
            {
                Rule? previous = document.Rules.FirstOrDefault(rule => rule.Id == id);
                document.Remove(id);
                try { document.Save(storagePath); NewDraft(); status = "排座规则已成功移除。"; checksDirty = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { if (previous is not null) document.Upsert(previous); status = $"规则移除未写入磁盘：{ex.Message}"; }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button("取消")) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
    }

    private void UpdateChecks(Rule? draft)
    {
        if (!checksDirty && Equals(draft, checkedDraft) && checkedLayout == layout.Revision && checkedRoster == roster.Revision) return;
        var rules = document.Rules.Where(rule => rule.Id != state.Editing).ToList();
        if (draft is not null) rules.Add(draft);
        checks = PreChecker.Run(rules, roster.Students, layout.ToGrid());
        checksDirty = false; checkedDraft = draft; checkedLayout = layout.Revision; checkedRoster = roster.Revision;
    }

    private void DrawChecks(RuleId? draftId)
    {
        if (checks is null) return;
        foreach (var report in checks.Global) ImGui.TextWrapped($"{(report.Mark == PreCheckErrorMark.Error ? "【规则错误】" : "【排座提示】")}：{ErrorText(report.Error)}");
        foreach (var report in checks.PerRule)
            ImGui.TextWrapped($"{(report.Id == draftId ? "【当前编辑规则】" : "【已存冲突规则】")}：{ErrorText(report.Error)}");
    }

    private static string ErrorText(PreCheckError error) => error switch
    {
        PreCheckError.DanglingRef => "规则中关联的学生已不在当前班级名单中。",
        PreCheckError.AlwaysTrue => "该规则在当前教室布局下已自然满足（无实际约束作用）。",
        PreCheckError.AlwaysFalse => "该规则在当前教室布局下在数学上不可达成（无任何合法座位）。",
        PreCheckError.OutOfBound => "指定的行列编号或坐标已超出当前教室的实际尺寸边界。",
        PreCheckError.PinnedSeatCollision => "固定座位的目标格当前不是有效学生课桌（例如被设为走道或讲台）。",
        PreCheckError.DuplicatePin => "多位不同的学生被指定在同一个座位上，造成位置冲突。",
        PreCheckError.OverCapacity => "班级学生总数超过了教室可用的课桌总容量，或单排行列人数上限不足。",
        _ => error.ToString()
    };
    private bool CanSave(Rule draft)
        => (draft is not CapacityRule capacity || capacity.Axis.Index >= 0
            && capacity.Axis.Index < (capacity.Axis.Type == AxisType.Row ? layout.Rows : layout.Cols))
            && !checks!.PerRule.Any(report => report.Id == draft.Id && report.Mark == PreCheckErrorMark.Error);

    private void Save(Rule draft)
    {
        RuleId id = state.Editing ?? RuleId.New();
        Rule saved = draft with { Id = id, DisplayName = RuleText.Describe(draft, people) };
        Rule? previous = document.Rules.FirstOrDefault(rule => rule.Id == id);
        document.Upsert(saved);
        try { document.Save(storagePath); state.Editing = id; checksDirty = true; status = "排座规则已保存生效。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (previous is null) document.Remove(id); else document.Upsert(previous);
            status = $"保存规则失败：{ex.Message}";
        }
    }

    private void ClearAllRules()
    {
        Rule[] previous = document.Rules.ToArray();
        document.Clear();
        try
        {
            document.Save(storagePath);
            state = new RuleEditorState();
            personSearch = referenceSearch = "";
            hoveredRelation = null;
            NewDraft();
            status = $"已清空全部 {previous.Length} 条排座规则。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (Rule rule in previous) document.Upsert(rule);
            status = $"清空规则未写入磁盘，原规则已保留：{ex.Message}";
        }
    }

    private void NewDraft()
    {
        state.Editing = null; state.First = state.Last = null; state.Negated = false;
        state.PreviewCenter = null; regionDragging = relationDragging = false; checksDirty = true;
    }

    private static uint Color(float r, float g, float b) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, 1));
    private static void DrawCentered(ImDrawListPtr draw, Vector2 min, float cell, string text)
    {
        Vector2 size = ImGui.CalcTextSize(text);
        float scale = Math.Min(1, Math.Max(1, cell - UiScale.Pixels(4)) / Math.Max(size.X, size.Y));
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
            min + (new Vector2(cell) - size * scale) / 2, Color(1, 1, 1), text);
    }
}
