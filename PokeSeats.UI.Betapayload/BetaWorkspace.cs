using System.Numerics;
using System.Text;
using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.Betapayload;

internal sealed class BetaWorkspace : DefaultWorkspace.DefaultWorkspace
{
    private const string RosterId = "pokeseats.defaultpayload.roster";
    private const string LayoutId = "pokeseats.defaultpayload.layout";
    private const string RulesId = "pokeseats.defaultpayload.rules";
    private const string ResultId = "pokeseats.defaultpayload.seating-result";
    private const string StartSeatingId = "pokeseats.defaultpayload.start-seating";

    private readonly List<Step> _steps = new();
    private IReadOnlyList<IUiView>? _sourceViews;
    private string _selectedViewId = RosterId;
    private string? _requestedViewId;

    private sealed record Step(IUiView View, string Label, string Hint)
    {
        public byte[] TabLabel { get; } = Encoding.UTF8.GetBytes(Label + '\0');
    }

    public BetaWorkspace(string settingsPath) : base(settingsPath)
    {
    }

    protected override bool IsViewVisible(IUiView view) => view.Id == _selectedViewId;

    protected override void SetViewVisible(string viewId, bool visible)
    {
        // A wizard always presents one page; selecting the current page must not hide it.
        if (visible)
            _requestedViewId = viewId;
    }

    protected override void OnViewOpenRequested(UiViewOpenRequested request)
        => _requestedViewId = request.ViewId;

    protected override void DrawViews(IWorkspaceContext workspaceContext, float deltaSeconds)
    {
        if (!ReferenceEquals(_sourceViews, workspaceContext.Views))
            BuildSteps(workspaceContext.Views);

        string? requested = _requestedViewId;
        _requestedViewId = null;
        bool selectRequested = requested is not null && _steps.Exists(step => step.View.Id == requested);
        if (selectRequested)
            _selectedViewId = requested!;

        float margin = UiScale.Pixels(DesignTokens.Metrics.SpaceLarge);
        ImGui.SetCursorPos(new Vector2(margin));
        Vector2 size = Vector2.Max(Vector2.One, ImGui.GetWindowSize() - new Vector2(margin * 2f));
        if (ImGui.BeginChild("##beta-wizard", size, ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextUnformatted(WorkspaceStrings.WorkspaceTitle);
            ImGui.TextDisabled(WorkspaceStrings.NavigationHint);
            ImGui.Spacing();

            if (_steps.Count == 0)
                ImGui.TextWrapped(WorkspaceStrings.NoViews);
            else if (ImGui.BeginTabBar("##beta-steps", ImGuiTabBarFlags.FittingPolicyScroll))
            {
                for (int index = 0; index < _steps.Count; index++)
                {
                    Step step = _steps[index];
                    ImGuiTabItemFlags flags = selectRequested && step.View.Id == requested
                        ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                    bool tabVisible;
                    unsafe
                    {
                        // ImGui.NET only exposes flags with a close button; a null open pointer
                        // keeps wizard steps nonclosable while allowing programmatic selection.
                        fixed (byte* label = step.TabLabel)
                            tabVisible = ImGuiNative.igBeginTabItem(label, null, flags) != 0;
                    }
                    if (!tabVisible)
                        continue;

                    _selectedViewId = step.View.Id;
                    ImGui.TextWrapped(step.Hint);
                    ImGui.Spacing();
                    DrawPage(step.View, deltaSeconds);
                    DrawNavigation(workspaceContext.Commands, index);
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }
        ImGui.EndChild();
    }

    private void BuildSteps(IReadOnlyList<IUiView> views)
    {
        _sourceViews = views;
        _steps.Clear();
        AddStep(views, RosterId, WorkspaceStrings.RosterTab, WorkspaceStrings.RosterHint);
        AddStep(views, LayoutId, WorkspaceStrings.LayoutTab, WorkspaceStrings.LayoutHint);
        AddStep(views, RulesId, WorkspaceStrings.RulesTab, WorkspaceStrings.RulesHint);
        AddStep(views, ResultId, WorkspaceStrings.ResultTab, WorkspaceStrings.ResultHint);
        foreach (IUiView view in views)
        {
            if (view.Id is RosterId or LayoutId or RulesId or ResultId)
                continue;
            _steps.Add(new Step(view, view.DisplayName, WorkspaceStrings.ExtensionHint));
        }
    }

    private void AddStep(IReadOnlyList<IUiView> views, string id, string label, string hint)
    {
        foreach (IUiView view in views)
        {
            if (view.Id != id)
                continue;
            _steps.Add(new Step(view, label, hint));
            return;
        }
    }

    private void DrawPage(IUiView view, float deltaSeconds)
    {
        float footerHeight = ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ItemSpacing.Y;
        Vector2 size = new(ImGui.GetContentRegionAvail().X,
            Math.Max(1f, ImGui.GetContentRegionAvail().Y - footerHeight));
        if (view.MinimumSize is { } minimum)
            ImGui.SetNextWindowContentSize(new Vector2(
                Math.Max(size.X - ImGui.GetStyle().WindowPadding.X * 2f,
                    minimum.X * UiScale.Current - ImGui.GetStyle().WindowPadding.X * 2f), 0f));

        ImGui.PushID(view.Id);
        if (ImGui.BeginChild("##page", size,
                ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding,
                ImGuiWindowFlags.HorizontalScrollbar))
            view.DrawContent(deltaSeconds, DesignTokens);
        ImGui.EndChild();
        ImGui.PopID();
    }

    private void DrawNavigation(IReadOnlyList<IUiCommand> commands, int index)
    {
        ImGui.BeginDisabled(index == 0);
        if (ImGui.Button(WorkspaceStrings.Previous))
            _requestedViewId = _steps[index - 1].View.Id;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled(string.Format(WorkspaceStrings.StepFormat, index + 1, _steps.Count));

        IUiCommand? startSeating = null;
        if (_selectedViewId is RulesId or ResultId)
        {
            foreach (IUiCommand command in commands)
            {
                if (command.Id == StartSeatingId)
                {
                    startSeating = command;
                    break;
                }
            }
        }

        string nextLabel = startSeating is null ? WorkspaceStrings.Next : startSeating.DisplayName;
        ImGui.SameLine();
        float buttonWidth = ImGui.CalcTextSize(nextLabel).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SetCursorPosX(Math.Max(ImGui.GetCursorPosX(),
            ImGui.GetWindowWidth() - ImGui.GetStyle().WindowPadding.X - buttonWidth));
        ImGui.BeginDisabled(startSeating is null && index == _steps.Count - 1);
        if (ImGui.Button(nextLabel))
        {
            if (startSeating is not null)
                startSeating.Execute();
            else
                _requestedViewId = _steps[index + 1].View.Id;
        }
        ImGui.EndDisabled();
    }
}
