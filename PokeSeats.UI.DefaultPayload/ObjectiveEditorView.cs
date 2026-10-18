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
        PayloadStrings.ObjectiveGroupBalance,
    };

    private static readonly string DeletePopupLabel =
        $"{PayloadStrings.ObjectiveDeleteConfirmTitle}###objective-delete-confirm";

    private readonly StudentRoster _roster;
    private readonly ObjectiveDocument _document;
    private readonly string _storagePath;
    private SeatObjective? _draft;
    private string _draftName = string.Empty;
    private string _status;

    public ObjectiveEditorView(StudentRoster roster, ObjectiveDocument document, string storagePath, string? loadMessage)
    {
        _roster = roster;
        _document = document;
        _storagePath = storagePath;
        _status = loadMessage ?? string.Empty;
    }

    public void DrawContent()
    {
        DrawToolbar();
        ImGui.Separator();
        DrawObjectiveList();
        ImGui.Separator();
        DrawEditor();
        if (!string.IsNullOrEmpty(_status))
            ImGui.TextWrapped(_status);
        DrawDeleteConfirmation();
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

        float listHeight = Math.Min(
            UiScale.Pixels(260),
            ImGui.GetTextLineHeightWithSpacing() * (_document.Objectives.Count + 1) + UiScale.Pixels(8));
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

    private void DrawEditor()
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
            _draft = draft with { Priority = priority };
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
        ImGui.BeginDisabled(draft.Shape == ObjectiveShape.GroupBalance);
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
                    draft = draft with { Shape = shape };
                    _draft = draft;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndDisabled();
            }
            ImGui.EndCombo();
        }

        if (draft.Shape == ObjectiveShape.GroupBalance)
            ImGui.TextWrapped(PayloadStrings.ObjectiveGroupBalanceExplanation);
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
        if (draft.Shape is ObjectiveShape.Centered or ObjectiveShape.Bimodal
            && draft.Dimension != SeatDimension.DistanceFromCenter)
            return PayloadStrings.ObjectiveInvalidShape;
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

    private static string ShapeSummary(SeatObjective objective) =>
        objective.Shape == ObjectiveShape.GroupBalance
            ? PayloadStrings.ObjectiveGroupBalance
            : string.Format(
                PayloadStrings.ObjectiveListShapeSummaryFormat,
                ShapeLabels[(int)objective.Shape],
                DimensionLabels[(int)objective.Dimension]);
}
