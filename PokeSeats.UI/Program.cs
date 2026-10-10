using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using PokeSeats.UI;
using PokeSeats.UI.Fonts;
using PokeSeats.UI.Plugins;
using PokeSeats.UI.PluginContracts;


var options = WindowOptions.Default with
{
    Size = new Vector2D<int>((int)UiScale.ReferenceWidth, (int)UiScale.ReferenceHeight),
    Title = "PokeSeats 戳座",
    API = GraphicsAPI.Default,
    VSync = true,
    FramesPerSecond = 60,
    UpdatesPerSecond = 60
};

using var window = Window.Create(options);

GL? gl = null;
IInputContext? input = null;
ImGuiController? imGui = null;
FontAtlasScaling? fontAtlasScaling = null;
PluginCatalog? catalog = null;
WorkspaceManager? workspaces = null;
WorkspaceSelector? selector = null;
Vector2D<int>? pendingFramebufferSize = null;
var scaling = new UiScaling();

window.Load += () =>
{
    gl = GL.GetApi(window);
    gl.Viewport(window.FramebufferSize);
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
    imGui = new ImGuiController(gl, window, input, () =>
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        PluginFontLoader.AddRequestedFonts(io.Fonts, fontRequests);
    });
    // The controller starts a frame in its constructor; unlock the atlas before resizing it.
    imGui.Render();
    fontAtlasScaling = new FontAtlasScaling(gl);

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
    if (gl is null || window.Size.X <= 0 || window.Size.Y <= 0)
        return;

    if (pendingFramebufferSize is { } framebufferSize)
    {
        gl.Viewport(framebufferSize);
        pendingFramebufferSize = null;
    }

    catalog!.DrainPendingEvents();
    var viewportSize = new System.Numerics.Vector2(window.Size.X, window.Size.Y);
    scaling.HandleShortcut(input!, viewportSize);

    gl.ClearColor(0.08f, 0.08f, 0.08f, 1.0f);
    gl.Clear(ClearBufferMask.ColorBufferBit);
    scaling.Apply(viewportSize);
    float pixelDensity = MathF.Max(
        window.FramebufferSize.X / (float)window.Size.X,
        window.FramebufferSize.Y / (float)window.Size.Y);
    fontAtlasScaling!.Apply(UiScale.Current, pixelDensity);
    imGui!.Update((float)delta);
    selector!.HandleShortcut(input!);
    workspaces!.Draw((float)delta);
    selector.Draw();
    scaling.DrawScaleIndicator();
    imGui.Render();
    selector.ApplyPendingSwitch();
};

window.Closing += () =>
{
    workspaces?.Dispose();
    catalog?.Dispose();
    fontAtlasScaling?.Dispose();
    imGui?.Dispose();
    input?.Dispose();
    gl?.Dispose();
};

window.Run();
