using System.Runtime.InteropServices;
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
    float initialScale = DisplayScale.ForWindow(window);
    if (initialScale > 1f)
    {
        float width = UiScale.ReferenceWidth * initialScale;
        float height = UiScale.ReferenceHeight * initialScale;
        if (window.Monitor is { } monitor)
        {
            width = MathF.Min(width, monitor.Bounds.Size.X * 0.9f);
            height = MathF.Min(height, monitor.Bounds.Size.Y * 0.9f);
        }

        var initialSize = new Vector2D<int>(
            Math.Max(320, (int)MathF.Round(width)),
            Math.Max(240, (int)MathF.Round(height)));
        if (initialSize != window.Size)
        {
            window.Size = initialSize;
            window.Center(window.Monitor);
        }
    }

    input = window.CreateInput();

    string applicationDirectory = AppContext.BaseDirectory;
    string builtinsDirectory = Path.Combine(applicationDirectory, "builtins");
    string pluginsDirectory = Path.Combine(applicationDirectory, "plugins");
    AppData appData = AppData.Resolve();
    appData.MigrateLegacyData();
    var config = new AppConfigStore(appData.Root, appData.ReadLegacyWorkspaceSelection());
    IntPtr iniFilename = Marshal.StringToCoTaskMemUTF8(appData.ImGuiIniPath);
    Console.WriteLine($"Data directory: {appData.Root}");
    Console.WriteLine($"Device id: {config.DeviceId}");

    catalog = new PluginCatalog(builtinsDirectory, pluginsDirectory, appData.PluginsDirectory);
    IReadOnlyList<PluginFontRequest> fontRequests = catalog.FontRequests;
    void configureIo()
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        unsafe { io.NativePtr->IniFilename = (byte*)iniFilename; }
        PluginFontLoader.AddRequestedFonts(io.Fonts, fontRequests);
    }
    renderer = OperatingSystem.IsWindows()
        ? new Direct3D11ImGuiController(window, input, configureIo)
        : new OpenGlImGuiRenderer(window, input, configureIo);
    fontAtlasScaling = new FontAtlasScaling(renderer.UpdateFontTexture);
    var textures = new UiTextureService(renderer);

    scaling.CaptureStyle();
    workspaces = new WorkspaceManager(catalog, config, scaling, textures);
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
    float displayScale = DisplayScale.ForWindow(window);
    scaling.HandleShortcut(input!, displayScale);

    scaling.Apply(displayScale);
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
