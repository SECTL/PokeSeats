using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

var options = WindowOptions.Default with
{
    Size = new Vector2D<int>(1280, 720),
    Title = "PokeSeats 戳座",
    API = GraphicsAPI.Default,
    VSync = true
};

using var window = Window.Create(options);

GL?              gl    = null;
IInputContext?   input = null;
ImGuiController? imGui = null;

window.Load += () =>
{
    gl    = GL.GetApi(window);
    input = window.CreateInput();
    imGui = new ImGuiController(gl, window, input, () =>
    {
        // 全局 IO 配置：触控调优、字体、样式都放这里
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        // io.FontGlobalScale = 1.5f;  // 触控模式的目标尺寸
    });
};

window.Render += delta =>
{
    imGui!.Update((float)delta);
    ImGui.DockSpaceOverViewport();
    ImGui.Begin("PokeSeats");
    ImGui.Text($"帧时间: {delta * 1000:F1} ms");
    ImGui.End();
    imGui!.Render();
};

window.Closing += () =>
{
    imGui?.Dispose();
    input?.Dispose();
};

window.Run();
