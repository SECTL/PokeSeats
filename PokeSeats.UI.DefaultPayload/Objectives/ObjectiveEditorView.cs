using System.Numerics;
using ImGuiNET;
using PokeSeats.IR.Base;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

/// <summary>「排座目标」标签页：编辑并排序写入 objectives.json 的软目标列表。</summary>
internal sealed class ObjectiveEditorView
{
    private static readonly string[] DimensionLabels =
    {
        PayloadStrings.ObjectiveDimensionRow,
        PayloadStrings.ObjectiveDimensionColumn,
        PayloadStrings.ObjectiveDimensionCenter,
    };

    private static readonly string[] ShapeLabels =
    {
        PayloadStrings.ObjectiveShapeAscendingToFront,
        PayloadStrings.ObjectiveShapeDescendingToFront,
        PayloadStrings.ObjectiveCentered,
        PayloadStrings.ObjectiveBimodal,
        PayloadStrings.ObjectiveRowBalance,
        PayloadStrings.ObjectiveRegionPreference,
    };

    private static readonly string[] DirectionLabels = { PayloadStrings.ObjectiveHighValues, PayloadStrings.ObjectiveLowValues };

    private static readonly string DeletePopupLabel =
        $"{PayloadStrings.ObjectiveDeleteConfirmTitle}###objective-delete-confirm";

    private readonly StudentRoster _roster;
    private readonly ClassroomRegionPicker _picker;
    private readonly ObjectiveDocument _document;
    private readonly string _storagePath;
    private SeatObjective? _draft;
    private string _draftName = string.Empty;
    private string _status;

    public ObjectiveEditorView(StudentRoster roster, LayoutDocument layout, ObjectiveDocument document, string storagePath, string? loadMessage)
    {
        _roster = roster;
        _picker = new ClassroomRegionPicker(layout);
        _document = document;
        _storagePath = storagePath;
        _status = loadMessage ?? string.Empty;
    }

    public void DrawContent(UiDesignTokens designTokens)
    {
        ImGui.PushTextWrapPos(ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - ImGui.GetStyle().ScrollbarSize);
        DrawToolbar();
        ImGui.Separator();
        DrawObjectiveList();
        ImGui.Separator();
        DrawEditor(designTokens);
        if (!string.IsNullOrEmpty(_status))
            ImGui.TextUnformatted(_status);
        DrawDeleteConfirmation();
        ImGui.PopTextWrapPos();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button(PayloadStrings.ObjectiveNewButton))
            NewDraft();

        SeatObjective? selected = SelectedSaved();
        ImGui.SameLine();
        ImGui.BeginDisabled(selected is null);
        if (ImGui.Button(PayloadStrings.ObjectiveMoveUpButton))
            Move(selected!, -1);
        ImGui.SameLine();
        if (ImGui.Button(PayloadStrings.ObjectiveMoveDownButton))
            Move(selected!, 1);
        ImGui.SameLine();
        if (ImGui.Button(PayloadStrings.ObjectiveDeleteButton))
            ImGui.OpenPopup(DeletePopupLabel);
        ImGui.EndDisabled();
    }

    private void DrawObjectiveList()
    {
        if (_document.Objectives.Count == 0)
        {
            ImGui.TextDisabled(PayloadStrings.ObjectiveNoneHint);
            return;
        }

        float availableHeight = ImGui.GetContentRegionAvail().Y;
        float rowHeight = ImGui.GetTextLineHeight() + ImGui.GetStyle().CellPadding.Y * 2;
        float listHeight = _draft is null
            ? Math.Max(rowHeight * 2, availableHeight - ImGui.GetTextLineHeightWithSpacing()
                - ImGui.GetStyle().ItemSpacing.Y * 2 - StatusHeight())
            : Math.Max(rowHeight * 2, Math.Min(availableHeight * .25f,
                rowHeight * (_document.Objectives.Count + 1) + 2));
        if (!ImGui.BeginTable("##objective-list", 3,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp,
                new Vector2(0, listHeight)))
            return;

        ImGui.TableSetupColumn(PayloadStrings.ObjectivePriorityColumnHeader, ImGuiTableColumnFlags.WidthFixed, UiScale.Pixels(80));
        ImGui.TableSetupColumn(PayloadStrings.ObjectiveDisplayNameColumnHeader, ImGuiTableColumnFlags.WidthStretch, 2);
        ImGui.TableSetupColumn(PayloadStrings.ObjectiveShapeColumnHeader, ImGuiTableColumnFlags.WidthStretch, 3);
        ImGui.TableHeadersRow();
        foreach (SeatObjective objective in _document.Objectives)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.PushID(objective.Id.Value.ToString());
            bool selected = _draft is { } draft && draft.Id == objective.Id;
            if (ImGui.Selectable(objective.Priority.ToString(), selected,
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap))
                Select(objective);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(objective.DisplayName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(ShapeSummary(objective));
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private float StatusHeight() => string.IsNullOrEmpty(_status) ? 0
        : ImGui.CalcTextSize(_status, false, ImGui.GetContentRegionAvail().X).Y + ImGui.GetStyle().ItemSpacing.Y;

    private void DrawEditor(UiDesignTokens designTokens)
    {
        if (_draft is null)
        {
            if (_document.Objectives.Count > 0)
                ImGui.TextDisabled(PayloadStrings.ObjectiveSelectHint);
            return;
        }

        ImGui.Text(PayloadStrings.ObjectiveEditorHeading);
        SeatObjective draft = _draft;

        ImGui.SetNextItemWidth(UiScale.Pixels(320));
        if (ImGui.InputText(PayloadStrings.ObjectiveDisplayNameLabel, ref _draftName, 128))
            _draft = draft = draft with { DisplayName = _draftName };

        draft = DrawAttributeCombo(draft);
        draft = DrawDimensionCombo(draft);
        draft = DrawShapeCombo(draft);

        int priority = draft.Priority;
        ImGui.SetNextItemWidth(UiScale.Pixels(120));
        if (ImGui.InputInt(PayloadStrings.ObjectivePriorityLabel, ref priority))
        {
            priority = Math.Max(1, priority);
            _draft = draft = draft with { Priority = priority };
        }

        if (draft.Shape == ObjectiveShape.RowBalance)
            _draft = draft = draft with { Dimension = SeatDimension.Row, Region = null };
        else
        {
            if (draft.Shape == ObjectiveShape.RegionPreference)
            {
                int direction = (int)draft.RegionPreferenceDirection;
                ImGui.SetNextItemWidth(UiScale.Pixels(220));
                if (ImGui.Combo(PayloadStrings.ObjectiveDirectionLabel, ref direction, DirectionLabels, DirectionLabels.Length))
                    _draft = draft = draft with { RegionPreferenceDirection = (RegionPreferenceDirection)direction };
                if (draft.Region is null) ImGui.TextUnformatted(PayloadStrings.ObjectiveRegionRequired);
            }
            ImGui.TextUnformatted(PayloadStrings.ObjectiveRegionDragHint);
            SeatRegion? region = draft.Region;
            float footerHeight = ImGui.GetFrameHeightWithSpacing() * 2 + StatusHeight();
            float previewHeight = Math.Max(UiScale.Pixels(120), ImGui.GetContentRegionAvail().Y - footerHeight);
            if (_picker.Draw(draft.Id.Value.ToString(), ref region, designTokens, previewHeight))
                _draft = draft = draft with { Region = region };
            ImGui.BeginDisabled(draft.Region is null);
            if (ImGui.Button(PayloadStrings.ObjectiveClearRegion))
                _draft = draft = draft with { Region = null };
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(PayloadStrings.ObjectiveClearRegionHint);
            if (draft.Region is { } selected)
            {
                ImGui.SameLine();
                ImGui.Text(string.Format(PayloadStrings.ObjectiveRegionBoundsFormat, selected.RowMin, selected.RowMax, selected.ColMin, selected.ColMax));
            }
        }

        if (ImGui.Button(PayloadStrings.ObjectiveSaveButton))
            SaveDraft();
    }

    private SeatObjective DrawAttributeCombo(SeatObjective draft)
    {
        IReadOnlyList<PersonAttributeDefinition> definitions = _roster.Snapshot.AttributeDefinitions;
        PersonAttributeDefinition? current = definitions.FirstOrDefault(definition => definition.Id == draft.AttributeId);
        string preview = current?.Name ?? PayloadStrings.ObjectiveNoAttributeOption;
        ImGui.SetNextItemWidth(UiScale.Pixels(320));
        if (ImGui.BeginCombo(PayloadStrings.ObjectiveAttributeLabel, preview))
        {
            if (definitions.Count == 0)
            {
                ImGui.TextDisabled(PayloadStrings.ObjectiveNoAttributesAvailable);
            }
            else
            {
                foreach (PersonAttributeDefinition definition in definitions)
                {
                    string label = definition.Kind == PersonAttributeKind.Number
                        ? definition.Name
                        : string.Format(PayloadStrings.ObjectiveAttributeNonNumericFormat, definition.Name);
                    if (ImGui.Selectable(label, definition.Id == draft.AttributeId))
                    {
                        draft = draft with { AttributeId = definition.Id };
                        _draft = draft;
                        ImGui.CloseCurrentPopup();
                    }
                }
            }
            ImGui.EndCombo();
        }

        if (current is not null && current.Kind != PersonAttributeKind.Number)
            ImGui.TextDisabled(PayloadStrings.ObjectiveNoNumberAttribute);
        return draft;
    }

    private SeatObjective DrawDimensionCombo(SeatObjective draft)
    {
        int dimension = (int)draft.Dimension;
        ImGui.SetNextItemWidth(UiScale.Pixels(220));
        ImGui.BeginDisabled(draft.Shape is ObjectiveShape.RowBalance or ObjectiveShape.RegionPreference);
        bool changed = ImGui.Combo(PayloadStrings.ObjectiveDimensionLabel, ref dimension, DimensionLabels, DimensionLabels.Length);
        ImGui.EndDisabled();
        if (!changed)
            return draft;

        var nextDimension = (SeatDimension)dimension;
        ObjectiveShape shape = draft.Shape;
        if ((shape is ObjectiveShape.Centered or ObjectiveShape.Bimodal) && nextDimension != SeatDimension.DistanceFromCenter)
            shape = ObjectiveShape.AscendingToFront;
        draft = draft with { Dimension = nextDimension, Shape = shape };
        _draft = draft;
        return draft;
    }

    private SeatObjective DrawShapeCombo(SeatObjective draft)
    {
        ImGui.SetNextItemWidth(UiScale.Pixels(220));
        if (ImGui.BeginCombo(PayloadStrings.ObjectiveShapeLabel, ShapeLabels[(int)draft.Shape]))
        {
            for (int index = 0; index < ShapeLabels.Length; index++)
            {
                var shape = (ObjectiveShape)index;
                bool allowed = shape is not (ObjectiveShape.Centered or ObjectiveShape.Bimodal)
                    || draft.Dimension == SeatDimension.DistanceFromCenter;
                ImGui.BeginDisabled(!allowed);
                if (ImGui.Selectable(ShapeLabels[index], draft.Shape == shape))
                {
                    draft = draft with { Shape = shape,
                        Dimension = shape == ObjectiveShape.RowBalance ? SeatDimension.Row : draft.Dimension,
                        Region = shape == ObjectiveShape.RowBalance ? null : draft.Region };
                    _draft = draft;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }

        if (draft.Shape == ObjectiveShape.RowBalance)
            ImGui.TextUnformatted(PayloadStrings.ObjectiveRowBalanceExplanation);
        return draft;
    }

    private void DrawDeleteConfirmation()
    {
        if (!ImGui.BeginPopupModal(DeletePopupLabel, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        SeatObjective? selected = SelectedSaved();
        if (selected is not null)
        {
            ImGui.TextUnformatted(string.Format(PayloadStrings.ObjectiveDeleteConfirmFormat, selected.DisplayName));
            if (ImGui.Button(PayloadStrings.ObjectiveDeleteConfirmButton))
            {
                Delete(selected);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
        }
        if (ImGui.Button(PayloadStrings.CommonCancel) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void Select(SeatObjective objective)
    {
        _draft = objective;
        _draftName = objective.DisplayName;
    }

    private void NewDraft()
    {
        IReadOnlyList<PersonAttributeDefinition> definitions = _roster.Snapshot.AttributeDefinitions;
        PersonAttributeDefinition? numeric = definitions.FirstOrDefault(definition => definition.Kind == PersonAttributeKind.Number);
        PersonAttributeId attribute = numeric is not null
            ? numeric.Id
            : definitions.Count > 0 ? definitions[0].Id : default;
        int priority = _document.Objectives.Count == 0 ? 1 : _document.Objectives.Max(objective => objective.Priority) + 1;
        _draft = new SeatObjective(
            SeatObjectiveId.New(),
            string.Empty,
            attribute,
            SeatDimension.Row,
            ObjectiveShape.AscendingToFront,
            priority);
        _draftName = string.Empty;
    }

    private void SaveDraft()
    {
        if (_draft is null)
            return;

        SeatObjective draft = _draft;
        string? error = Validate(draft);
        if (error is not null)
        {
            _status = error;
            return;
        }

        draft = draft with { DisplayName = draft.DisplayName.Trim() };
        SeatObjective? previous = _document.Objectives.FirstOrDefault(objective => objective.Id == draft.Id);
        _document.Upsert(draft);
        try
        {
            _document.Save(_storagePath);
            _draft = draft;
            _draftName = draft.DisplayName;
            _status = PayloadStrings.ObjectiveSavedStatus;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (previous is null)
                _document.Remove(draft.Id);
            else
                _document.Upsert(previous);
            _status = string.Format(PayloadStrings.ObjectiveSaveFailedFormat, exception.Message);
        }
    }

    private void Move(SeatObjective selected, int offset)
    {
        int index = IndexOf(selected.Id);
        int target = index + offset;
        if (index < 0 || target < 0 || target >= _document.Objectives.Count)
            return;

        SeatObjectiveId[] before = _document.Objectives.Select(objective => objective.Id).ToArray();
        if (offset < 0)
            _document.MoveUp(selected.Id);
        else
            _document.MoveDown(selected.Id);
        if (_document.Objectives.Select(objective => objective.Id).SequenceEqual(before))
            return;

        try
        {
            _document.Save(_storagePath);
            _status = PayloadStrings.ObjectiveSavedStatus;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (offset < 0)
                _document.MoveDown(selected.Id);
            else
                _document.MoveUp(selected.Id);
            _status = string.Format(PayloadStrings.ObjectiveSaveFailedFormat, exception.Message);
        }
    }

    private void Delete(SeatObjective selected)
    {
        _document.Remove(selected.Id);
        try
        {
            _document.Save(_storagePath);
            if (_draft is { } draft && draft.Id == selected.Id)
            {
                _draft = null;
                _draftName = string.Empty;
            }
            _status = PayloadStrings.ObjectiveDeletedStatus;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _document.Upsert(selected);
            _status = string.Format(PayloadStrings.ObjectiveSaveFailedFormat, exception.Message);
        }
    }

    private string? Validate(SeatObjective draft)
    {
        if (string.IsNullOrWhiteSpace(draft.DisplayName))
            return PayloadStrings.ObjectiveNameRequired;
        PersonAttributeDefinition? definition =
            _roster.Snapshot.AttributeDefinitions.FirstOrDefault(candidate => candidate.Id == draft.AttributeId);
        if (definition is null)
            return PayloadStrings.ObjectiveAttributeRequired;
        if (definition.Kind != PersonAttributeKind.Number)
            return PayloadStrings.ObjectiveNoNumberAttribute;
        if (ObjectiveDocument.ShapeValidationError(draft) is { } shapeError)
            return shapeError;
        if (draft.Priority < 1)
            return PayloadStrings.ObjectivePriorityInvalid;
        return null;
    }

    private SeatObjective? SelectedSaved()
    {
        if (_draft is not { } draft)
            return null;
        foreach (SeatObjective objective in _document.Objectives)
            if (objective.Id == draft.Id)
                return objective;
        return null;
    }

    private int IndexOf(SeatObjectiveId id)
    {
        for (int index = 0; index < _document.Objectives.Count; index++)
            if (_document.Objectives[index].Id == id)
                return index;
        return -1;
    }

    private static string ShapeSummary(SeatObjective objective)
    {
        if (objective.Shape == ObjectiveShape.RowBalance) return PayloadStrings.ObjectiveRowBalance;
        if (objective.Shape == ObjectiveShape.RegionPreference)
            return $"{PayloadStrings.ObjectiveRegionPreference}，{(objective.RegionPreferenceDirection == RegionPreferenceDirection.HighValues ? PayloadStrings.ObjectiveHighValues : PayloadStrings.ObjectiveLowValues)}";
        string summary = string.Format(PayloadStrings.ObjectiveListShapeSummaryFormat,
            ShapeLabels[(int)objective.Shape], DimensionLabels[(int)objective.Dimension]);
        return objective.Region is null ? summary : $"{summary}，{PayloadStrings.ObjectiveRegionLimited}";
    }
}
