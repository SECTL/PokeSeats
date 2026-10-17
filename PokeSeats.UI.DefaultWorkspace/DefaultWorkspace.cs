using System.Numerics;
using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultWorkspace;

public class DefaultWorkspace : IWorkspace
{
    private const string AuthorGitHub = "chenjintang-shrimp";
    private const string AuthorAvatarResource = "PokeSeats.UI.DefaultWorkspace.Assets.chenjintang-shrimp.png";
    private const string SectIconResource = "PokeSeats.UI.DefaultWorkspace.Assets.sectl.jpg";

    public UiDesignTokens DesignTokens => _designTokens;

    internal bool               IsDisposed { get; private set; }
    private readonly WorkspaceSettingsStore _settings;
    private WorkspaceAppearance _appearance;
    private UiDesignTokens _designTokens;
    private bool openSettingsPopup;
    private bool _settingsDirty;
    private IWorkspaceContext? context;
    private IUiTextureService? _textures;
    private IUiTexture? _authorAvatar;
    private IUiTexture? _sectlIcon;
    private bool _aboutTexturesLoaded;
    private bool openAboutPopup;
    private IDisposable? _viewOpenSubscription;
    private readonly Dictionary<string, bool> _viewVisibility = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ViewGeometry> _viewGeometry = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingFocus = new(StringComparer.Ordinal);

    private readonly record struct ViewGeometry(Vector2 Position, Vector2 Size, float Scale, bool Docked);

    public DefaultWorkspace(string settingsPath)
    {
        if (string.IsNullOrWhiteSpace(settingsPath))
            throw new ArgumentException("A settings path is required.", nameof(settingsPath));

        _settings = new WorkspaceSettingsStore(settingsPath);
        _appearance = _settings.Load();
        _designTokens = _appearance.ToTokens();
    }

    public void Activate(IWorkspaceContext _context)
    {
        if (IsDisposed)
            throw new ObjectDisposedException(nameof(DefaultWorkspace));

        this.context = _context ?? throw new ArgumentNullException(nameof(_context));
        _textures = _context.Textures;
        _viewOpenSubscription = _context.Events.Subscribe<UiViewOpenRequested>(OnViewOpenRequested);
        
    }

    private bool IsViewVisible(IUiView view)
        => _viewVisibility.TryGetValue(view.Id, out bool visible) ? visible : view.InitiallyVisible;

    private void SetViewVisible(string viewId, bool visible)
        => _viewVisibility[viewId] = visible;

    private void OnViewOpenRequested(UiViewOpenRequested request)
    {
        IWorkspaceContext? current = context;
        if (current is null)
            return;

        foreach (IUiView view in current.Views)
        {
            if (!string.Equals(view.Id, request.ViewId, StringComparison.Ordinal))
                continue;

            SetViewVisible(view.Id, true);
            _pendingFocus.Add(view.Id);
            return;
        }
    }
    private void DrawMainMenuBar(IReadOnlyList<IUiCommand> uiCommands, IReadOnlyList<IUiView> uiViews)
    {
        ImGui.PushStyleColor(ImGuiCol.MenuBarBg, DesignTokens.Colors.Surface);
        if (!ImGui.BeginMainMenuBar())
        {
            ImGui.PopStyleColor();
            return;
        }

        if (ImGui.BeginMenu(WorkspaceStrings.SystemMenu))
        {
            if (ImGui.MenuItem(WorkspaceStrings.SettingsTitle))
                openSettingsPopup = true;

            if (ImGui.MenuItem(WorkspaceStrings.AboutTitle))
                openAboutPopup = true;

            if (ImGui.MenuItem(WorkspaceStrings.Exit))
                context!.Events.Publish(new ApplicationExitRequested());

            ImGui.EndMenu();
        }

        if (ImGui.BeginMenu(WorkspaceStrings.PanelsMenu))
        {
            if (uiViews.Count == 0)
            {
                ImGui.MenuItem(WorkspaceStrings.NoPanels, null, false, false);
            }
            else
            {
                foreach (IUiView view in uiViews)
                {
                    bool visible = IsViewVisible(view);
                    ImGui.PushID(view.Id);
                    bool clicked = ImGui.MenuItem(view.DisplayName, null, visible, true);
                    ImGui.PopID();
                    if (clicked)
                        SetViewVisible(view.Id, !visible);
                }
            }
            ImGui.EndMenu();
        }

        IUiCommand? pendingCommand = null;
        foreach (IUiCommand command in uiCommands)
        {
            ImGui.PushID(command.Id);
            bool clicked = ImGui.MenuItem(command.DisplayName);
            ImGui.PopID();
            if (clicked)
                pendingCommand = command;
        }

        ImGui.EndMainMenuBar();
        ImGui.PopStyleColor();

        pendingCommand?.Execute();
    }


    private void DrawView(IUiView view, Vector2 initialPosition, Vector2 initialSize, float deltaSeconds)
    {
        float scale = UiScale.Current;
        if (_pendingFocus.Remove(view.Id))
        {
            // Reopen a collapsed window and bring it to the front.
            ImGui.SetNextWindowCollapsed(false, ImGuiCond.Always);
            ImGui.SetNextWindowFocus();
        }

        Vector2 viewportOrigin = ImGui.GetMainViewport().Pos;
        if (!_viewGeometry.TryGetValue(view.Id, out ViewGeometry previous))
        {
            ImGui.SetNextWindowPos(initialPosition, ImGuiCond.Always);
            ImGui.SetNextWindowSize(initialSize, ImGuiCond.Always);
        }
        else if (previous.Scale != scale && !previous.Docked)
        {
            ImGui.SetNextWindowPos(viewportOrigin + previous.Position * scale, ImGuiCond.Always);
            ImGui.SetNextWindowSize(previous.Size * scale, ImGuiCond.Always);
        }
        if (view.MinimumSize is { } minimumSize)
            ImGui.SetNextWindowSizeConstraints(minimumSize * scale, new Vector2(float.MaxValue, float.MaxValue));

        bool open = true;
        string title = $"{view.DisplayName}###{view.Id}";
        bool visible = ImGui.Begin(title, ref open);
        _viewGeometry[view.Id] = new ViewGeometry(
            (ImGui.GetWindowPos() - viewportOrigin) / scale,
            ImGui.GetWindowSize() / scale, scale, ImGui.IsWindowDocked());
        if (visible)
            view.DrawContent(deltaSeconds, DesignTokens);
        ImGui.End();

        if (!open)
            SetViewVisible(view.Id, false);
    }

    private void DrawViews(float deltaSeconds)
    {
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        float scale = UiScale.Current;
        float margin = UiScale.Pixels(DesignTokens.Metrics.SpaceLarge);
        Vector2 offset = new(margin, 40f * scale);
        float rowHeight = 0f;
        foreach (IUiView view in context!.Views)
        {
            if (!IsViewVisible(view))
                continue;
            Vector2 size = (view.MinimumSize ?? new Vector2(520, 360)) * scale;
            if (offset.X > margin && offset.X + size.X > viewport.Size.X - margin)
            {
                offset.X = margin;
                offset.Y += rowHeight + margin;
                rowHeight = 0f;
            }
            // Additional views must remain reachable even when the tiled rows exceed the viewport.
            Vector2 position = new(offset.X, Math.Min(offset.Y, Math.Max(40f * scale, viewport.Size.Y - size.Y - margin)));
            DrawView(view, viewport.Pos + position, size, deltaSeconds);
            offset.X += size.X + margin;
            rowHeight = Math.Max(rowHeight, size.Y);
        }
    }

    public void Draw(float deltaSeconds)
    {
        ArgumentNullException.ThrowIfNull(context);
        ImGuiViewportPtr viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos);
        ImGui.SetNextWindowSize(viewport.Size);
        ImGui.SetNextWindowViewport(viewport.ID);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBringToFrontOnFocus
            | ImGuiWindowFlags.NoNavFocus;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, DesignTokens.Colors.Background);
        bool visible = ImGui.Begin("###pokeseats.defaultworkspace.root", flags);
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
        try
        {
            if (visible)
            {
                uint dockspaceId = ImGui.GetID("pokeseats.defaultworkspace.dockspace");
                ImGui.DockSpace(dockspaceId, Vector2.Zero, ImGuiDockNodeFlags.PassthruCentralNode);
                DrawViews(deltaSeconds);
                DrawMainMenuBar(context.Commands, context.Views);
                DrawAboutPopup();
                DrawSettingsPopup();

            }
        }
        finally
        {
            ImGui.End();
        }
    }

    private void DrawAboutPopup()
    {
        string popupId = WorkspaceStrings.AboutTitle + "###pokeseats.defaultworkspace.about";
        if (openAboutPopup)
        {
            ImGui.OpenPopup(popupId);
            openAboutPopup = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            EnsureAboutTextures();

            ImGui.SetWindowFontScale(1.8f);
            ImGui.TextUnformatted(WorkspaceStrings.ProductName);
            ImGui.SetWindowFontScale(1f);

            ImGui.TextDisabled(WorkspaceStrings.VersionCopyright);
            ImGui.TextDisabled(WorkspaceStrings.LicenseNotice);
            ImGui.Spacing();
            DrawCreditLine(WorkspaceStrings.MadeBy, _authorAvatar, AuthorGitHub);
            DrawCreditLine(WorkspaceStrings.SupportsThanks, _sectlIcon, WorkspaceStrings.Supporter);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Text(WorkspaceStrings.FontDescription);
            ImGui.TextDisabled(WorkspaceStrings.FontLicenseNotice);
            ImGui.Separator();
            if (ImGui.Button(WorkspaceStrings.Confirm))
                ImGui.CloseCurrentPopup();
            
            ImGui.EndPopup();
        }
    }

    private static void DrawCreditLine(string prefix, IUiTexture? texture, string suffix)
    {
        ImGui.TextUnformatted(prefix);
        ImGui.SameLine(0f, 6f);
        if (texture is not null)
        {
            float size = ImGui.GetTextLineHeight() * 1.3f;
            ImGui.Image(texture.TextureId, new Vector2(size, size));
            ImGui.SameLine(0f, 6f);
        }
        ImGui.TextUnformatted(suffix);
    }

    private void EnsureAboutTextures()
    {
        if (_aboutTexturesLoaded || _textures is null)
            return;

        _aboutTexturesLoaded = true;
        _authorAvatar = LoadTexture(AuthorAvatarResource);
        _sectlIcon = LoadTexture(SectIconResource);
    }

    private IUiTexture? LoadTexture(string resourceName)
    {
        using Stream? stream = typeof(DefaultWorkspace).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            Console.Error.WriteLine($"Embedded resource '{resourceName}' was not found.");
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return _textures!.Create(buffer.ToArray());
    }

    private void DrawSettingsPopup()
    {
        string popupId = WorkspaceStrings.SettingsTitle + "###pokeseats.defaultworkspace.settings";
        if (openSettingsPopup)
        {
            ImGui.OpenPopup(popupId);
            openSettingsPopup = false;
        }

        bool open = true;
        if (ImGui.BeginPopupModal(popupId, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextUnformatted(WorkspaceStrings.SettingsAppearanceSection);
            if (ImGui.RadioButton(WorkspaceStrings.SettingsLightMode, !_appearance.Dark))
            {
                SetAppearance(WorkspaceAppearance.FromPreset(dark: false));
                SaveSettingsIfDirty();
            }
            ImGui.SameLine();
            if (ImGui.RadioButton(WorkspaceStrings.SettingsDarkMode, _appearance.Dark))
            {
                SetAppearance(WorkspaceAppearance.FromPreset(dark: true));
                SaveSettingsIfDirty();
            }

            ImGui.Separator();
            DrawColorEdit(WorkspaceStrings.SettingsBackgroundColor, _appearance.Background,
                color => _appearance with { Background = color });
            DrawColorEdit(WorkspaceStrings.SettingsSurfaceColor, _appearance.Surface,
                color => _appearance with { Surface = color });
            DrawColorEdit(WorkspaceStrings.SettingsTextColor, _appearance.Text,
                color => _appearance with { Text = color });
            DrawColorEdit(WorkspaceStrings.SettingsAccentColor, _appearance.Accent,
                color => _appearance with { Accent = color });

            ImGui.Separator();
            if (ImGui.Button(WorkspaceStrings.SettingsReset))
            {
                SetAppearance(WorkspaceAppearance.FromPreset(_appearance.Dark));
                SaveSettingsIfDirty();
            }
            ImGui.SameLine();
            if (ImGui.Button(WorkspaceStrings.SettingsClose))
            {
                SaveSettingsIfDirty();
                ImGui.CloseCurrentPopup();
            }

            ImGui.TextDisabled(WorkspaceStrings.SettingsHint);
            ImGui.TextDisabled(string.Format(WorkspaceStrings.SettingsScaleFormat, UiScale.Current));
            ImGui.EndPopup();
        }

        if (!open)
            SaveSettingsIfDirty();
    }

    private void DrawColorEdit(string label, Vector4 current, Func<Vector4, WorkspaceAppearance> update)
    {
        Vector4 color = current;
        if (ImGui.ColorEdit4(label, ref color, ImGuiColorEditFlags.NoAlpha | ImGuiColorEditFlags.NoInputs))
            SetAppearance(update(color));
        if (ImGui.IsItemDeactivatedAfterEdit())
            SaveSettingsIfDirty();
    }

    private void SetAppearance(WorkspaceAppearance appearance)
    {
        _appearance = appearance.Normalize();
        _designTokens = _appearance.ToTokens();
        _settingsDirty = true;
    }

    private void SaveSettingsIfDirty()
    {
        if (!_settingsDirty)
            return;

        try
        {
            _settings.Save(_appearance);
            _settingsDirty = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Unable to save workspace appearance: {exception.Message}");
        }
    }

    public void Deactivate()
    {
        SaveSettingsIfDirty();
        _viewOpenSubscription?.Dispose();
        _viewOpenSubscription = null;
        _pendingFocus.Clear();
        // Visible-view choices deliberately survive deactivation.
        openAboutPopup = false;
        openSettingsPopup = false;
        context = null;
    }

    public void Dispose()
    {
        SaveSettingsIfDirty();
        _viewOpenSubscription?.Dispose();
        _viewOpenSubscription = null;
        _authorAvatar?.Dispose();
        _authorAvatar = null;
        _sectlIcon?.Dispose();
        _sectlIcon = null;
        _textures = null;
        context = null;
        // _panelVisibility.Clear();
        _viewVisibility.Clear();
        _viewGeometry.Clear();
        _pendingFocus.Clear();
        IsDisposed = true;
    }
}
