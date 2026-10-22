using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.Betapayload;

/// <summary>
/// Presents the stable GitHub Releases updater state. Downloading and installing are always
/// explicit user actions, and a restart happens only after the installer helper was started
/// successfully.
/// </summary>
internal sealed class UpdateView : IUiView, IUiCommand
{
    public const string ViewId = "pokeseats.betapayload.updates";

    private readonly GitHubUpdater _updater;
    private readonly IUiEventBus _events;
    private readonly string _currentVersionText;
    private ReleaseUpdate? _formattedRelease;
    private string? _availableText;
    private string? _actionError;

    public UpdateView(GitHubUpdater updater, IUiEventBus events)
    {
        _updater = updater ?? throw new ArgumentNullException(nameof(updater));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _currentVersionText = string.Format(WorkspaceStrings.UpdateCurrentVersionFormat, updater.CurrentVersion);
    }

    public string Id => ViewId;

    public string DisplayName => WorkspaceStrings.UpdateViewName;

    public bool InitiallyVisible => false;

    public Vector2? MinimumSize => new(640, 480);

    /// <summary>Reveals the view; an update that is already downloading keeps running untouched.</summary>
    public void Execute() => _events.Publish(new UiViewOpenRequested(Id));

    public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
    {
        UpdateState state = _updater.State;
        bool platformSupported = _updater.Platform is not null;

        ImGui.TextUnformatted(_currentVersionText);
        if (!platformSupported)
            ImGui.TextWrapped(WorkspaceStrings.UpdatePlatformUnsupported);
        ImGui.Spacing();

        DrawStatus(state, designTokens);

        if (state.Error is { } stateError)
            DrawError(stateError, designTokens);
        else if (state.Status == UpdateStatus.Failed)
            DrawError(string.Format(WorkspaceStrings.UpdateStatusFailedFormat,
                WorkspaceStrings.UpdateUnknownError), designTokens);

        if (_actionError is { } actionError)
            DrawError(actionError, designTokens);

        ImGui.Spacing();
        DrawActions(state, platformSupported);
    }

    private void DrawStatus(UpdateState state, UiDesignTokens designTokens)
    {
        switch (state.Status)
        {
            case UpdateStatus.Checking:
                ImGui.TextDisabled(WorkspaceStrings.UpdateStatusChecking);
                break;
            case UpdateStatus.UpToDate:
                DrawStatusLine(WorkspaceStrings.UpdateStatusUpToDate, designTokens.Colors.Success);
                break;
            case UpdateStatus.Available:
                if (state.Release is not { } release)
                {
                    DrawError(string.Format(WorkspaceStrings.UpdateStatusFailedFormat,
                        WorkspaceStrings.UpdateUnknownError), designTokens);
                    break;
                }
                DrawStatusLine(AvailableText(release), designTokens.Colors.Text);
                DrawRelease(release);
                break;
            case UpdateStatus.Downloading:
                ImGui.TextDisabled(WorkspaceStrings.UpdateStatusDownloading);
                ImGui.ProgressBar(state.Progress, new Vector2(0, 0));
                break;
            case UpdateStatus.Ready:
                DrawStatusLine(WorkspaceStrings.UpdateStatusReady, designTokens.Colors.Success);
                DrawStatusLine(WorkspaceStrings.UpdateSaveWarning, designTokens.Colors.Warning);
                if (state.Release is { } readyRelease)
                    DrawRelease(readyRelease);
                break;
            case UpdateStatus.Installing:
                ImGui.TextDisabled(WorkspaceStrings.UpdateStatusInstalling);
                break;
        }
    }

    private void DrawRelease(ReleaseUpdate release)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted(WorkspaceStrings.UpdateReleaseNotesHeading);

        float height = Math.Max(UiScale.Pixels(120), ImGui.GetContentRegionAvail().Y * 0.45f);
        ImGui.BeginChild("##update-release-notes", new Vector2(0, height), ImGuiChildFlags.Borders,
            ImGuiWindowFlags.AlwaysVerticalScrollbar);
        if (string.IsNullOrWhiteSpace(release.Notes))
        {
            ImGui.TextWrapped(WorkspaceStrings.UpdateNoReleaseNotes);
        }
        else
        {
            ImGui.PushTextWrapPos(0f);
            ImGui.TextUnformatted(release.Notes);
            ImGui.PopTextWrapPos();
        }
        ImGui.EndChild();

        if (ImGui.TextLink(WorkspaceStrings.UpdateReleasePageLink))
            OpenReleasePage(release.PageUrl);
    }

    private void DrawActions(UpdateState state, bool platformSupported)
    {
        bool checkDisabled = state.Status
            is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Installing or UpdateStatus.Ready;

        ImGui.BeginDisabled(checkDisabled);
        if (ImGui.Button(state.Status == UpdateStatus.Failed
                ? WorkspaceStrings.UpdateRetryButton
                : WorkspaceStrings.UpdateCheckButton))
            RunCheck();
        ImGui.EndDisabled();

        switch (state.Status)
        {
            case UpdateStatus.Available:
                ImGui.SameLine();
                ImGui.BeginDisabled(!platformSupported);
                if (ImGui.Button(WorkspaceStrings.UpdateDownloadButton))
                    RunDownload();
                ImGui.EndDisabled();
                break;
            case UpdateStatus.Ready:
                ImGui.SameLine();
                ImGui.BeginDisabled(!platformSupported);
                if (ImGui.Button(WorkspaceStrings.UpdateInstallButton))
                    RunInstall();
                ImGui.EndDisabled();
                break;
        }
    }

    private string AvailableText(ReleaseUpdate release)
    {
        if (!ReferenceEquals(_formattedRelease, release))
        {
            _formattedRelease = release;
            _availableText = string.Format(WorkspaceStrings.UpdateStatusAvailableFormat, release.Version);
        }
        return _availableText!;
    }

    private void RunCheck()
    {
        _actionError = null;
        try
        {
            _updater.Check();
        }
        catch (Exception exception)
        {
            _actionError = string.Format(WorkspaceStrings.UpdateActionFailedFormat, exception.Message);
        }
    }

    private void RunDownload()
    {
        _actionError = null;
        try
        {
            _updater.Download();
        }
        catch (Exception exception)
        {
            _actionError = string.Format(WorkspaceStrings.UpdateActionFailedFormat, exception.Message);
        }
    }

    private void RunInstall()
    {
        _actionError = null;
        try
        {
            if (_updater.Install())
                _events.Publish(new ApplicationExitRequested());
        }
        catch (Exception exception)
        {
            _actionError = string.Format(WorkspaceStrings.UpdateInstallLaunchFailedFormat, exception.Message);
        }
    }

    private void OpenReleasePage(Uri pageUrl)
    {
        _actionError = null;
        try
        {
            Process.Start(new ProcessStartInfo(pageUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _actionError = string.Format(WorkspaceStrings.UpdateOpenLinkFailedFormat, exception.Message);
        }
    }

    private static void DrawStatusLine(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void DrawError(string message, UiDesignTokens designTokens)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, designTokens.Colors.Danger);
        ImGui.TextWrapped(message);
        ImGui.PopStyleColor();
    }
}
