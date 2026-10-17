using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using PokeSeats.UI;
using PokeSeats.UI.Fonts;
using PokeSeats.UI.Plugins;
using PokeSeats.UI.PluginContracts;
using PokeSeats.UI.Rendering;


var options = WindowOptions.Default with
{
    Size = new Vector2D<int>((int)UiScale.ReferenceWidth, (int)UiScale.ReferenceHeight),
    Title = "PokeSeats 戳座",
    API = OperatingSystem.IsWindows() ? GraphicsAPI.None : GraphicsAPI.Default,
    VSync = true,
    ShouldSwapAutomatically = !OperatingSystem.IsWindows(),
    FramesPerSecond = 60,
    UpdatesPerSecond = 60
};

using var window = Window.Create(options);

IInputContext? input = null;
IUiRenderer? renderer = null;
FontAtlasScaling? fontAtlasScaling = null;
PluginCatalog? catalog = null;
WorkspaceManager? workspaces = null;
WorkspaceSelector? selector = null;
Vector2D<int>? pendingFramebufferSize = null;
var scaling = new UiScaling();

window.Load += () =>
{
    input = window.CreateInput();

    string applicationDirectory = AppContext.BaseDirectory;
    string builtinsDirectory = Path.Combine(applicationDirectory, "builtins");
    string pluginsDirectory = Path.Combine(applicationDirectory, "plugins");
    string dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    if (string.IsNullOrWhiteSpace(dataRoot))
        dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
    string dataDirectory = Path.Combine(dataRoot, "PokeSeats", "plugins");

    catalog = new PluginCatalog(builtinsDirectory, pluginsDirectory, dataDirectory);
    IReadOnlyList<PluginFontRequest> fontRequests = catalog.FontRequests;
    void configureIo()
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        PluginFontLoader.AddRequestedFonts(io.Fonts, fontRequests);
    }
    renderer = OperatingSystem.IsWindows()
        ? new Direct3D11ImGuiController(window, input, configureIo)
        : new OpenGlImGuiRenderer(window, input, configureIo);
    fontAtlasScaling = new FontAtlasScaling(renderer.UpdateFontTexture);

    scaling.CaptureStyle();
    workspaces = new WorkspaceManager(catalog, new WorkspaceSelectionStore(), scaling);
    selector = new WorkspaceSelector(workspaces);
    
    catalog.CreateEventScope().Subscribe<ApplicationExitRequested>(_ => window.Close());
};

window.FramebufferResize += size =>
{
    pendingFramebufferSize = size;
};

window.Render += delta =>
{
    if (renderer is null || window.Size.X <= 0 || window.Size.Y <= 0
        || window.FramebufferSize.X <= 0 || window.FramebufferSize.Y <= 0)
        return;

    if (pendingFramebufferSize is { } framebufferSize)
    {
        renderer.Resize(framebufferSize);
        pendingFramebufferSize = null;
    }

    catalog!.DrainPendingEvents();
    var viewportSize = new System.Numerics.Vector2(window.Size.X, window.Size.Y);
    scaling.HandleShortcut(input!, viewportSize);

    scaling.Apply(viewportSize);
    float pixelDensity = MathF.Max(
        window.FramebufferSize.X / (float)window.Size.X,
        window.FramebufferSize.Y / (float)window.Size.Y);
    fontAtlasScaling!.Apply(UiScale.Current, pixelDensity);
    renderer.Update((float)delta);
    selector!.HandleShortcut(input!);
    workspaces!.Draw((float)delta);
    selector.Draw();
    scaling.DrawScaleIndicator();
    renderer.Render();
    selector.ApplyPendingSwitch();
};

window.Closing += () =>
{
    workspaces?.Dispose();
    catalog?.Dispose();
    renderer?.Dispose();
    input?.Dispose();
};

window.Run();
