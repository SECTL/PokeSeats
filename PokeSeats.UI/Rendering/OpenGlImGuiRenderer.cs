using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace PokeSeats.UI.Rendering;

internal sealed class OpenGlImGuiRenderer : IUiRenderer
{
    private readonly GL _gl;
    private readonly ImGuiController _controller;
    private uint _fontTexture;

    internal OpenGlImGuiRenderer(IWindow window, IInputContext input, Action configureIo)
    {
        _gl = GL.GetApi(window);
        try
        {
            _gl.Viewport(window.FramebufferSize);
            _controller = new ImGuiController(_gl, window, input, configureIo);
            // The Silk controller starts a frame; unlock the atlas before scaling it.
            _controller.Render();
        }
        catch
        {
            _gl.Dispose();
            throw;
        }
    }

    public void Update(float delta)
    {
        _gl.ClearColor(0.08f, 0.08f, 0.08f, 1.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _controller.Update(delta);
    }

    public void Render() => _controller.Render();

    public void Resize(Vector2D<int> size) => _gl.Viewport(size);

    public unsafe void UpdateFontTexture()
    {
        ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;
        // Silk uses immutable storage; rebuilt atlases need a mutable texture.
        if (_fontTexture == 0)
            _fontTexture = _gl.GenTexture();
        atlas.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height);
        _gl.GetInteger(GLEnum.TextureBinding2D, out int previousTexture);
        _gl.GetInteger(GLEnum.UnpackAlignment, out int previousAlignment);
        _gl.GetInteger(GLEnum.UnpackRowLength, out int previousRowLength);
        _gl.GetInteger(GLEnum.UnpackSkipPixels, out int previousSkipPixels);
        _gl.GetInteger(GLEnum.UnpackSkipRows, out int previousSkipRows);
        _gl.GetInteger(GLEnum.PixelUnpackBufferBinding, out int previousBuffer);
        try
        {
            _gl.BindTexture(TextureTarget.Texture2D, _fontTexture);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, 0);
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
            _gl.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
            _gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
            _gl.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
                (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (void*)pixels);
        }
        finally
        {
            _gl.BindTexture(TextureTarget.Texture2D, (uint)previousTexture);
            _gl.BindBuffer(BufferTargetARB.PixelUnpackBuffer, (uint)previousBuffer);
            _gl.PixelStore(PixelStoreParameter.UnpackAlignment, previousAlignment);
            _gl.PixelStore(PixelStoreParameter.UnpackRowLength, previousRowLength);
            _gl.PixelStore(PixelStoreParameter.UnpackSkipPixels, previousSkipPixels);
            _gl.PixelStore(PixelStoreParameter.UnpackSkipRows, previousSkipRows);
        }
        atlas.SetTexID((IntPtr)_fontTexture);
        atlas.ClearTexData();
    }

    public void Dispose()
    {
        if (_fontTexture != 0)
            _gl.DeleteTexture(_fontTexture);
        _controller.Dispose();
        _gl.Dispose();
    }
}
