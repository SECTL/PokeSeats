using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using PokeSeats.UI;
using PokeSeats.UI.Fonts;
using PokeSeats.UI.Plugins;


var options = WindowOptions.Default with
{
    Size = new Vector2D<int>(1280, 720),
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
PluginCatalog? catalog = null;
WorkspaceManager? workspaces = null;
WorkspaceSelector? selector = null;
Vector2D<int>? pendingFramebufferSize = null;

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

    workspaces = new WorkspaceManager(catalog, new WorkspaceSelectionStore());
    selector = new WorkspaceSelector(workspaces);
};

window.FramebufferResize += size =>
{
    pendingFramebufferSize = size;
};

window.Render += delta =>
{
    if (gl is null)
        return;

    if (pendingFramebufferSize is { } framebufferSize)
    {
        gl.Viewport(framebufferSize);
        pendingFramebufferSize = null;
    }

    gl.ClearColor(0.08f, 0.08f, 0.08f, 1.0f);
    gl.Clear(ClearBufferMask.ColorBufferBit);
    imGui!.Update((float)delta);
    selector!.HandleShortcut(input!);
    workspaces!.Draw((float)delta);
    selector.Draw();
    imGui.Render();
    selector.ApplyPendingSwitch();
};

window.Closing += () =>
{
    workspaces?.Dispose();
    catalog?.Dispose();
    imGui?.Dispose();
    input?.Dispose();
    gl?.Dispose();
};

window.Run();
