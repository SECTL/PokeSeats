using System.Numerics;
using ImGuiNET;
using PokeSeats.Core.Analyzer;
using PokeSeats.IR.Base;
using PokeSeats.IR.Rules;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class RuleEditorView : IUiView
{
    private const string DeleteRuleConfirmPopupId = "删除规则确认";
    private const string ClearAllRulesConfirmPopupId = "清空所有规则确认";
    private static string DeleteRulePopupLabel => $"{RulesStrings.DeleteConfirmTitle}###{DeleteRuleConfirmPopupId}";
    private static string ClearAllRulesPopupLabel => $"{RulesStrings.ClearAllConfirmTitle}###{ClearAllRulesConfirmPopupId}";
    private static readonly string[] Categories =
    {
        RulesStrings.CategoryPinnedSeat, RulesStrings.CategoryRegion,
        RulesStrings.CategoryRelation, RulesStrings.CategoryCapacity
    };
    private static readonly (RelationKind Kind, string Text)[] Relations =
    {
        (RelationKind.ExactOffset, RulesStrings.RelationExactOffset), (RelationKind.InFrontOf, RulesStrings.RelationInFrontOf),
        (RelationKind.Behind, RulesStrings.RelationBehind), (RelationKind.LeftOf, RulesStrings.RelationLeftOf),
        (RelationKind.RightOf, RulesStrings.RelationRightOf), (RelationKind.SameRow, RulesStrings.RelationSameRow),
        (RelationKind.SameColumn, RulesStrings.RelationSameColumn), (RelationKind.Proximity, RulesStrings.RelationProximity)
    };
    private static readonly string[] AdjacencyLabels =
    {
        RulesStrings.AdjacencyHorizontal, RulesStrings.AdjacencyFourWay, RulesStrings.AdjacencyEightWay
    };
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
    private UiDesignTokens _designTokens = UiDesignTokens.Light;

    public RuleEditorView(StudentRoster roster, LayoutDocument layout, RuleDocument document, string storagePath, string? loadMessage, SeatingSession seating)
    {
        this.roster = roster; this.layout = layout; this.document = document; this.storagePath = storagePath;
        this.seating = seating;
        people = new StudentSearchIndex(roster);
        status = loadMessage ?? RulesStrings.DefaultStatus;
    }

    public string Id => "pokeseats.defaultpayload.rules";
    public string DisplayName => RulesStrings.RuleEditorDisplayName;
    public Vector2? MinimumSize => new(920, 620);

    public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
    {
        _designTokens = designTokens;
        seating.Update();
        if (ImGui.Button(RulesStrings.NewRuleButton)) NewDraft();
        ImGui.SameLine();
        ImGui.TextDisabled(string.Format(RulesStrings.ActiveRuleCountFormat, document.Rules.Count));
        ImGui.SameLine();
        Rule? draft = state.Build();
        UpdateChecks(draft);
        ImGui.BeginDisabled(draft is null || !CanSave(draft));
        if (ImGui.Button(state.Editing.HasValue ? RulesStrings.SaveChangesButton : RulesStrings.SaveRuleButton)) Save(draft!);
        ImGui.EndDisabled();
        ImGui.Separator();
        bool bodyVisible = ImGui.BeginChild("##rule-editor-body", new Vector2(0, UiScale.Pixels(-42)));
        if (bodyVisible && ImGui.BeginTable("##rule-editor-columns", 2, ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn(RulesStrings.RulesColumnHeader, ImGuiTableColumnFlags.WidthStretch, 3);
            ImGui.TableSetupColumn(RulesStrings.SavedRulesColumnHeader, ImGuiTableColumnFlags.WidthStretch, 1.3f);
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
        if (ImGui.Combo(RulesStrings.RuleTypeLabel, ref category, Categories, Categories.Length))
        {
            NewDraft(); state.Category = category;
        }
        if (state.Category != 3)
        {
            DrawPersonPicker(state.Category == 2 ? RulesStrings.BaseStudentLabel : RulesStrings.TargetStudentLabel, ref state.Person, ref personSearch, state.Category == 2 ? state.Reference : null);
            if (state.Category == 2)
                DrawPersonPicker(RulesStrings.ReferenceStudentLabel, ref state.Reference, ref referenceSearch, state.Person);
        }
        if (state.Category is 1 or 2)
        {
            ImGui.Checkbox(state.Category == 2 ? RulesStrings.NegateRelationCheckbox : RulesStrings.NegateRegionCheckbox, ref state.Negated);
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
            ? state.Category == 2 ? RulesStrings.DraftRelationPreviewStatus : RulesStrings.DraftIncompleteStatus
            : RuleText.Describe(draft, people));
        UpdateChecks(draft);
        DrawChecks(draft?.Id);
        if (state.Category == 2)
            ImGui.TextWrapped(RulesStrings.RelationCanvasHint);
        else if (state.Category == 1)
            ImGui.TextWrapped(RulesStrings.RegionCanvasHint);
        else if (state.Category == 0)
            ImGui.TextWrapped(RulesStrings.PinnedCanvasHint);
        else
            ImGui.TextWrapped(RulesStrings.CapacityCanvasHint);
        DrawClassroom();
        if (state.Category == 2)
            ImGui.TextWrapped(string.Format(RulesStrings.CanvasSeatSummaryFormat, state.ReferenceSeats.Count, state.Allowed.Count));
    }

    private void DrawPersonPicker(string label, ref PersonId? selected, ref string query, PersonId? excluded)
    {
        Person? person = selected.HasValue ? people.Get(selected.Value) : null;
        string preview = person is null
            ? RulesStrings.SelectStudentPrompt
            : string.Format(RulesStrings.PersonSummaryFormat, person.Name, person.StudentNumber ?? RulesStrings.NoStudentNumber);
        ImGui.SetNextItemWidth(UiScale.Pixels(320));
        if (!ImGui.BeginCombo(label, preview)) return;
        ImGui.SetNextItemWidth(UiScale.Pixels(300));
        ImGui.InputTextWithHint($"{RulesStrings.PersonSearchLabel}##{label}", RulesStrings.PersonSearchHint, ref query, 256);
        if (ImGui.BeginChild($"{RulesStrings.PersonResultsChildLabel}##{label}", new Vector2(0, UiScale.Pixels(170))))
        {
            foreach (Person candidate in people.Find(query))
            {
                if (candidate.Id == excluded) continue;
                string text = string.Format(RulesStrings.PersonSummaryFormat, candidate.Name, candidate.StudentNumber ?? RulesStrings.NoStudentNumber) + $"##{candidate.Id.Value}";
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
        if (ImGui.BeginCombo(RulesStrings.RelativeRelationLabel, current))
        {
            foreach (var item in Relations)
                if (ImGui.Selectable(item.Text, item.Kind == state.Relation)) state.Relation = item.Kind;
            ImGui.EndCombo();
        }
        if (state.Relation == RelationKind.Proximity)
        {
            string label = AdjacencyLabels[(int)state.Adjacency];
            ImGui.SetNextItemWidth(UiScale.Pixels(200));
            if (ImGui.BeginCombo(RulesStrings.AdjacencyRangeLabel, label))
            {
                for (int index = 0; index < AdjacencyLabels.Length; index++)
                    if (ImGui.Selectable(AdjacencyLabels[index], (int)state.Adjacency == index)) state.Adjacency = (AdjacencyMode)index;
                ImGui.EndCombo();
            }
        }
        if (ImGui.Button(RulesStrings.ResetViewportButton)) { pan = Vector2.Zero; zoom = 1; }
        ImGui.SameLine(); ImGui.TextWrapped(RulesStrings.RelationCanvasHelp);
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
            ImGui.TextDisabled(string.Format(RulesStrings.RelativeDirectionFormat, state.Dx, state.Dy));
            foreach (var item in Relations)
            {
                if (item.Kind == RelationKind.Behind && state.Dy <= 0 || item.Kind == RelationKind.InFrontOf && state.Dy >= 0
                    || item.Kind == RelationKind.LeftOf && state.Dx >= 0 || item.Kind == RelationKind.RightOf && state.Dx <= 0) continue;
                if (item.Kind == RelationKind.Proximity && !state.MatchesBaseOffset(state.Dy, state.Dx, item.Kind)) continue;
                string text = item.Kind == RelationKind.ExactOffset ? string.Format(RulesStrings.FixedOffsetPositionFormat, OffsetText()) : item.Text;
                if (ImGui.MenuItem(text)) state.Relation = item.Kind;
                if (ImGui.IsItemHovered()) hoveredRelation = item.Kind;
            }
            ImGui.EndPopup();
        }
        ImDrawListPtr draw = ImGui.GetWindowDrawList();
        draw.PushClipRect(origin, origin + size, true);
        draw.AddRectFilled(origin, origin + size, Color(_designTokens.Colors.Canvas));
        int firstCol = (int)MathF.Floor((origin.X - center.X) / cell - .5f);
        int lastCol = (int)MathF.Ceiling((origin.X + size.X - center.X) / cell + .5f);
        int firstRow = (int)MathF.Floor((origin.Y - center.Y) / cell - .5f);
        int lastRow = (int)MathF.Ceiling((origin.Y + size.Y - center.Y) / cell + .5f);
        for (int row = firstRow; row <= lastRow; row++)
            for (int col = firstCol; col <= lastCol; col++)
            {
                Vector2 min = center + new Vector2(col - .5f, row - .5f) * cell;
                if (state.MatchesOffset(row, col, hoveredRelation))
                    draw.AddRectFilled(min, min + new Vector2(cell), Color(_designTokens.Colors.SuccessSurface));
                draw.AddRect(min, min + new Vector2(cell), Color(_designTokens.Colors.Border), 0, ImDrawFlags.None, UiScale.Pixels(_designTokens.Metrics.BorderWidth));
                if (row == 0 && col == 0)
                {
                    draw.AddRectFilled(min, min + new Vector2(cell), Color(_designTokens.Colors.WarningSurface));
                    DrawCentered(draw, min, cell, RulesStrings.AnchorA);
                }
                if (row == state.Dy && col == state.Dx && (row != 0 || col != 0))
                {
                    draw.AddRect(min + Vector2.One, min + new Vector2(cell - 1), Color(_designTokens.Colors.Warning), 0, ImDrawFlags.None, UiScale.Pixels(_designTokens.Metrics.BorderWidth * 2f));
                    DrawCentered(draw, min, cell, RulesStrings.AnchorB);
                }
            }
        draw.PopClipRect();
    }

    private string OffsetText()
        => string.Format(
            RulesStrings.OffsetSummaryFormat,
            state.Dy < 0 ? RulesStrings.OffsetTowardPodium : RulesStrings.OffsetTowardBack,
            Math.Abs(state.Dy),
            state.Dx < 0 ? RulesStrings.OffsetTowardDoor : RulesStrings.OffsetTowardWindow,
            Math.Abs(state.Dx));

    private void DrawCapacityControls()
    {
        bool rows = state.AxisType == AxisType.Row;
        if (ImGui.RadioButton(RulesStrings.LimitRowCountRadio, rows)) state.AxisType = AxisType.Row;
        ImGui.SameLine();
        if (ImGui.RadioButton(RulesStrings.LimitColumnCountRadio, !rows)) state.AxisType = AxisType.Column;
        int index = state.AxisIndex + 1;
        ImGui.SetNextItemWidth(UiScale.Pixels(150));
        if (ImGui.InputInt(rows ? RulesStrings.TargetRowNumberLabel : RulesStrings.TargetColumnNumberLabel, ref index)) state.AxisIndex = index - 1;
        ImGui.Checkbox(RulesStrings.UseMinimumSeatsCheckbox, ref state.UseMin); ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(120)); ImGui.InputInt("##capacity-min", ref state.Min);
        ImGui.Checkbox(RulesStrings.UseMaximumSeatsCheckbox, ref state.UseMax); ImGui.SameLine();
        ImGui.SetNextItemWidth(UiScale.Pixels(120)); ImGui.InputInt("##capacity-max", ref state.Max);
        if (ImGui.Button(RulesStrings.ExactSeatCountButton)) { state.UseMin = state.UseMax = true; state.Max = state.Min; }
    }

    private void DrawClassroom()
    {
        float footer = state.Category == 2 ? ImGui.GetTextLineHeightWithSpacing() * 2 : 0;
        float height = Math.Max(UiScale.Pixels(120), ImGui.GetContentRegionAvail().Y - footer);
        if (!ImGui.BeginChild("##rules-classroom", new Vector2(0, height), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.AlwaysVerticalScrollbar))
        { ImGui.EndChild(); return; }
        Vector2 available = ImGui.GetContentRegionAvail();
        Vector2 labelSize = ImGui.CalcTextSize(RulesStrings.PodiumMarker);
        float minimumCell = Math.Max(labelSize.X, labelSize.Y) + UiScale.Pixels(_designTokens.Metrics.SpaceMedium);
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
                    CellType.Desk => Color(_designTokens.Colors.AccentSurface), CellType.Podium => Color(_designTokens.Colors.SecondarySurface),
                    CellType.NarrowAisle => Color(_designTokens.Colors.WarningSurface), CellType.Aisle => Color(_designTokens.Colors.Surface),
                    _ => Color(_designTokens.Colors.Canvas)
                };
                if (state.Allowed.Contains(p)) color = Color(_designTokens.Colors.SuccessSurface);
                bool selected = state.Category == 3 ? (state.AxisType == AxisType.Row ? row : col) == state.AxisIndex
                    : state.Category == 1 && state.First is { } start && state.Last is { } end && row >= Math.Min(start.Row, end.Row)
                        && row <= Math.Max(start.Row, end.Row) && col >= Math.Min(start.Col, end.Col) && col <= Math.Max(start.Col, end.Col);
                draw.AddRectFilled(min, min + new Vector2(cell), color);
                draw.AddRect(min, min + new Vector2(cell), Color(_designTokens.Colors.Border), 0, ImDrawFlags.None, UiScale.Pixels(_designTokens.Metrics.BorderWidth));
                if (selected) draw.AddRect(min + Vector2.One, min + new Vector2(cell - 1), Color(_designTokens.Colors.SelectionBorder), 0, ImDrawFlags.None, UiScale.Pixels(_designTokens.Metrics.BorderWidth));
                if (state.Category == 2 && state.PreviewAnchor == p)
                {
                    draw.AddRectFilled(min, min + new Vector2(cell), Color(_designTokens.Colors.WarningSurface));
                    DrawCentered(draw, min, cell, RulesStrings.AnchorA);
                }
                else if (type == CellType.Podium) DrawCentered(draw, min, cell, RulesStrings.PodiumMarker);
            }
        ImGui.EndChild();
    }

    private void DrawList()
    {
        ImGui.Text(RulesStrings.SavedRulesListLabel);
        if (ImGui.BeginChild("##saved-rule-list", new Vector2(0, UiScale.Pixels(440)), ImGuiChildFlags.Borders))
        {
            foreach (Rule rule in document.Rules)
            {
                ImGui.PushID(rule.Id.value.ToString());
                string text = RuleText.Describe(rule, people);
                bool conflicting = seating.IsConflicting(rule.Id);
                if (conflicting)
                {
                    text = string.Format(RulesStrings.ConflictRulePrefixFormat, text);
                    ImGui.PushStyleColor(ImGuiCol.Text, _designTokens.Colors.Danger);
                    ImGui.PushStyleColor(ImGuiCol.Header, _designTokens.Colors.DangerSurface);
                    ImGui.PushStyleColor(ImGuiCol.HeaderHovered, _designTokens.Colors.DangerHovered);
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
        if (ImGui.Button(RulesStrings.DeleteSelectedRuleButton)) ImGui.OpenPopup(DeleteRulePopupLabel);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(document.Rules.Count == 0);
        if (ImGui.Button(RulesStrings.ClearAllRulesButton)) ImGui.OpenPopup(ClearAllRulesPopupLabel);
        ImGui.EndDisabled();
        if (ImGui.BeginPopupModal(ClearAllRulesPopupLabel, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text(string.Format(RulesStrings.ClearAllConfirmMessageFormat, document.Rules.Count));
            if (ImGui.Button(RulesStrings.ConfirmClearButton))
            {
                ClearAllRules();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button(RulesStrings.CancelButton)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        if (ImGui.BeginPopupModal(DeleteRulePopupLabel, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Text(RulesStrings.DeleteConfirmMessage);
            if (ImGui.Button(RulesStrings.ConfirmDeleteButton) && state.Editing is { } id)
            {
                Rule? previous = document.Rules.FirstOrDefault(rule => rule.Id == id);
                document.Remove(id);
                try { document.Save(storagePath); NewDraft(); status = RulesStrings.RuleRemovedStatus; checksDirty = true; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { if (previous is not null) document.Upsert(previous); status = string.Format(RulesStrings.RuleRemoveFailedFormat, ex.Message); }
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(); if (ImGui.Button(RulesStrings.CancelButton)) ImGui.CloseCurrentPopup();
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
        foreach (var report in checks.Global) ImGui.TextWrapped(string.Format(RulesStrings.CheckReportFormat, report.Mark == PreCheckErrorMark.Error ? RulesStrings.CheckMarkError : RulesStrings.CheckMarkHint, ErrorText(report.Error)));
        foreach (var report in checks.PerRule)
            ImGui.TextWrapped(string.Format(RulesStrings.CheckReportFormat, report.Id == draftId ? RulesStrings.CheckMarkCurrentDraft : RulesStrings.CheckMarkSavedConflict, ErrorText(report.Error)));
    }

    private static string ErrorText(PreCheckError error) => error switch
    {
        PreCheckError.DanglingRef => RulesStrings.PreCheckDanglingReference,
        PreCheckError.AlwaysTrue => RulesStrings.PreCheckAlwaysTrue,
        PreCheckError.AlwaysFalse => RulesStrings.PreCheckAlwaysFalse,
        PreCheckError.OutOfBound => RulesStrings.PreCheckOutOfBound,
        PreCheckError.PinnedSeatCollision => RulesStrings.PreCheckPinnedSeatCollision,
        PreCheckError.DuplicatePin => RulesStrings.PreCheckDuplicatePin,
        PreCheckError.OverCapacity => RulesStrings.PreCheckOverCapacity,
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
        try { document.Save(storagePath); state.Editing = id; checksDirty = true; status = RulesStrings.RuleSavedStatus; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (previous is null) document.Remove(id); else document.Upsert(previous);
            status = string.Format(RulesStrings.RuleSaveFailedFormat, ex.Message);
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
            status = string.Format(RulesStrings.RulesClearedStatusFormat, previous.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (Rule rule in previous) document.Upsert(rule);
            status = string.Format(RulesStrings.ClearRulesFailedFormat, ex.Message);
        }
    }

    private void NewDraft()
    {
        state.Editing = null; state.First = state.Last = null; state.Negated = false;
        state.PreviewCenter = null; regionDragging = relationDragging = false; checksDirty = true;
    }

    private static uint Color(Vector4 color) => ImGui.ColorConvertFloat4ToU32(color);
    private void DrawCentered(ImDrawListPtr draw, Vector2 min, float cell, string text)
    {
        Vector2 size = ImGui.CalcTextSize(text);
        float scale = Math.Min(1, Math.Max(1, cell - UiScale.Pixels(_designTokens.Metrics.SpaceSmall)) / Math.Max(size.X, size.Y));
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale,
            min + (new Vector2(cell) - size * scale) / 2, Color(_designTokens.Colors.Text), text);
    }
}
