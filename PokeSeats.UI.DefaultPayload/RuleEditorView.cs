using System.Numerics;
using ImGuiNET;
using PokeSeats.Core.Analyzer;
using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class RuleEditorView : IUiView
{
    private static readonly string[] Categories = { "固定座位", "区域限制", "相对位置", "行列人数" };
    private static readonly (RelationKind Kind, string Text)[] Relations =
    {
        (RelationKind.ExactOffset, "固定偏移"), (RelationKind.InFrontOf, "在前面"),
        (RelationKind.Behind, "在后面"), (RelationKind.LeftOf, "在左侧"),
        (RelationKind.RightOf, "在右侧"), (RelationKind.SameRow, "同排"),
        (RelationKind.SameColumn, "同列"), (RelationKind.Proximity, "相邻")
    };
    private readonly StudentRoster roster;
    private readonly StudentSearchIndex people;
    private readonly LayoutDocument layout;
    private readonly RuleDocument document;
    private readonly string storagePath;
    private readonly RuleEditorState state = new();
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

    public RuleEditorView(StudentRoster roster, LayoutDocument layout, RuleDocument document, string storagePath, string? loadMessage)
    {
        this.roster = roster; this.layout = layout; this.document = document; this.storagePath = storagePath;
        people = new StudentSearchIndex(roster);
        status = loadMessage ?? "选择规则类型，设置条件后保存。";
    }

    public string Id => "pokeseats.defaultpayload.rules";
    public string DisplayName => "规则编辑";
    public Vector2? MinimumSize => new(920, 620);

    public void DrawContent(float deltaSeconds)
    {
        if (ImGui.Button("新建规则")) NewDraft();
        ImGui.SameLine();
        ImGui.TextDisabled($"已保存 {document.Rules.Count} 条规则");
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
            ImGui.TableSetupColumn("编辑", ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn("规则列表", ImGuiTableColumnFlags.WidthStretch, 1.3f);
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
            DrawPersonPicker(state.Category == 2 ? "中心对象 A" : "编辑对象 A", ref state.Person, ref personSearch, state.Category == 2 ? state.Reference : null);
            if (state.Category == 2)
                DrawPersonPicker("相对对象 B", ref state.Reference, ref referenceSearch, state.Person);
        }
        if (state.Category is 1 or 2)
        {
            ImGui.Checkbox(state.Category == 2 ? "取反（例如：相邻改为不相邻）" : "取反（禁止坐在所选区域内）", ref state.Negated);
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
            ? state.Category == 2 ? "未选择人员时仍可预览 B 相对于 A 的关系；选择两人后可保存。" : "请完成规则条件。"
            : RuleText.Describe(draft, people));
        UpdateChecks(draft);
        DrawChecks(draft?.Id);
        if (state.Category == 2)
            ImGui.TextWrapped("下图仅预览当前规则。点击桌子临时放置 A，绿色为 B 可坐的桌子。");
        else if (state.Category == 1)
            ImGui.TextWrapped("在教室中拖动框选区域；绿色为这条规则允许的座位。");
        else if (state.Category == 0)
            ImGui.TextWrapped("点击教室中的桌子，固定该人员的座位。");
        else
            ImGui.TextWrapped("点击教室格子选择对应行 / 列。");
        DrawClassroom();
        if (state.Category == 2)
            ImGui.TextWrapped($"当前布局有 {state.ReferenceSeats.Count} 个可用 A 中心座位；临时 A 对应 {state.Allowed.Count} 个 B 候选座位。");
    }

    private void DrawPersonPicker(string label, ref PersonId? selected, ref string query, PersonId? excluded)
    {
        Person? person = selected.HasValue ? people.Get(selected.Value) : null;
        string preview = person is null ? "请选择人员" : $"{person.Name} · {person.StudentNumber ?? "无学号"}";
        ImGui.SetNextItemWidth(UiScale.Pixels(320));
        if (!ImGui.BeginCombo(label, preview)) return;
        ImGui.SetNextItemWidth(UiScale.Pixels(300));
        ImGui.InputTextWithHint($"搜索##{label}", "姓名 / 学号 / 拼音 / 首字母", ref query, 256);
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
        if (ImGui.BeginCombo("关系", current))
        {
            foreach (var item in Relations)
                if (ImGui.Selectable(item.Text, item.Kind == state.Relation)) state.Relation = item.Kind;
            ImGui.EndCombo();
        }
        if (state.Relation == RelationKind.Proximity)
            ImGui.Checkbox("斜角也算相邻", ref state.DiagonalAdjacent);
        if (ImGui.Button("回到 A 中心")) { pan = Vector2.Zero; zoom = 1; }
        ImGui.SameLine(); ImGui.TextWrapped("左键拖动定位，松开选关系；中键平移；滚轮缩放。上方是教室前方。");
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
            ImGui.TextDisabled($"B 相对于 A：左右 {state.Dx:+0;-0;0} 列，前后 {state.Dy:+0;-0;0} 排");
            foreach (var item in Relations)
            {
                if (item.Kind == RelationKind.Behind && state.Dy <= 0 || item.Kind == RelationKind.InFrontOf && state.Dy >= 0
                    || item.Kind == RelationKind.LeftOf && state.Dx >= 0 || item.Kind == RelationKind.RightOf && state.Dx <= 0) continue;
                string text = item.Kind == RelationKind.ExactOffset ? $"固定偏移：{OffsetText()}" : item.Text;
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
        => $"{(state.Dy < 0 ? "前" : "后")} {Math.Abs(state.Dy)} 排，{(state.Dx < 0 ? "左" : "右")} {Math.Abs(state.Dx)} 列";

    private void DrawCapacityControls()
    {
        bool rows = state.AxisType == AxisType.Row;
        if (ImGui.RadioButton("行人数", rows)) state.AxisType = AxisType.Row;
        ImGui.SameLine();
        if (ImGui.RadioButton("列人数", !rows)) state.AxisType = AxisType.Column;
        int index = state.AxisIndex + 1;
        ImGui.SetNextItemWidth(UiScale.Pixels(150));
        if (ImGui.InputInt(rows ? "第几行" : "第几列", ref index)) state.AxisIndex = index - 1;
        ImGui.Checkbox("最少人数", ref state.UseMin); ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(120)); ImGui.InputInt("##capacity-min", ref state.Min);
        ImGui.Checkbox("最多人数", ref state.UseMax); ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(120)); ImGui.InputInt("##capacity-max", ref state.Max);
        if (ImGui.Button("设为恰好人数")) { state.UseMin = state.UseMax = true; state.Max = state.Min; }
    }

    private void DrawClassroom()
    {
        float footer = state.Category == 2 ? ImGui.GetTextLineHeightWithSpacing() * 2 : 0;
        float height = Math.Max(UiScale.Pixels(120), ImGui.GetContentRegionAvail().Y - footer);
        if (!ImGui.BeginChild("##rules-classroom", new Vector2(0, height), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
        { ImGui.EndChild(); return; }
        Vector2 available = ImGui.GetContentRegionAvail();
        float cell = Math.Max(1, Math.Min((available.X - 1) / layout.Cols, (available.Y - 1) / layout.Rows));
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
        ImGui.Text("已保存规则（点击编辑）");
        if (ImGui.BeginChild("##saved-rule-list", new Vector2(0, UiScale.Pixels(440)), ImGuiChildFlags.Borders))
        {
            foreach (Rule rule in document.Rules)
            {
                ImGui.PushID(rule.Id.value.ToString());
                string text = RuleText.Describe(rule, people);
                if (ImGui.Selectable(text, state.Editing == rule.Id))
                {
                    state.Load(rule);
                    if (state.Last is { } last) state.Last = new GridPos(Math.Min(last.Row, layout.Rows - 1), Math.Min(last.Col, layout.Cols - 1));
                }
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
                ImGui.PopID();
            }
        }
        ImGui.EndChild();
        ImGui.BeginDisabled(!state.Editing.HasValue);
        if (ImGui.Button("删除选中规则")) ImGui.OpenPopup("删除规则确认");
        ImGui.EndDisabled();
        if (ImGui.BeginPopupModal("删除规则确认", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text("删除当前选中的规则？");
            if (ImGui.Button("删除") && state.Editing is { } id)
            {
                Rule? previous = document.Rules.FirstOrDefault(rule => rule.Id == id);
                document.Remove(id);
                try { document.Save(storagePath); NewDraft(); status = "规则已删除。"; checksDirty = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { if (previous is not null) document.Upsert(previous); status = $"删除未保存：{ex.Message}"; }
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
        foreach (var report in checks.Global) ImGui.TextWrapped($"{(report.Mark == PreCheckErrorMark.Error ? "错误" : "提示")}：{ErrorText(report.Error)}");
        foreach (var report in checks.PerRule)
            ImGui.TextWrapped($"{(report.Id == draftId ? "当前规则" : "已保存规则")}：{ErrorText(report.Error)}");
    }

    private static string ErrorText(PreCheckError error) => error switch
    {
        PreCheckError.DanglingRef => "引用的人员已不在名单中。", PreCheckError.AlwaysTrue => "这条规则在当前布局中总是成立。",
        PreCheckError.AlwaysFalse => "这条规则在当前布局中不可能成立。", PreCheckError.OutOfBound => "位置或行列超出教室范围。",
        PreCheckError.PinnedSeatCollision => "固定位置不是桌子。", PreCheckError.DuplicatePin => "不同人员固定在同一个座位。",
        PreCheckError.OverCapacity => "人数或要求超过可用座位容量。", _ => error.ToString()
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
        try { document.Save(storagePath); state.Editing = id; checksDirty = true; status = "规则已保存。"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (previous is null) document.Remove(id); else document.Upsert(previous);
            status = $"规则未保存：{ex.Message}";
        }
    }

    private void NewDraft()
    {
        state.Editing = null; state.First = state.Last = null; state.Negated = false;
        state.PreviewCenter = null; regionDragging = relationDragging = false; checksDirty = true;
    }

    private static uint Color(float r, float g, float b) => ImGui.ColorConvertFloat4ToU32(new Vector4(r, g, b, 1));
    private static void DrawCentered(ImDrawListPtr draw, Vector2 min, float cell, string text)
        => draw.AddText(min + (new Vector2(cell) - ImGui.CalcTextSize(text)) / 2, Color(1, 1, 1), text);
}
