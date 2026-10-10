using ImGuiNET;
using Silk.NET.OpenGL;

namespace PokeSeats.UI.Fonts;

/// <summary>Rasterizes the retained font outlines at the current framebuffer resolution.</summary>
internal sealed class FontAtlasScaling : IDisposable
{
    private readonly GL _gl;
    private readonly float[] _referenceSizes;
    private float _scale = 1f;
    private float _pixelDensity = 1f;
    private uint _texture;

    internal FontAtlasScaling(GL gl)
    {
        _gl = gl;
        var configs = ImGui.GetIO().Fonts.ConfigData;
        _referenceSizes = new float[configs.Size];
        for (int index = 0; index < configs.Size; index++)
            _referenceSizes[index] = configs[index].SizePixels;
    }

    // Called between Render and Update/NewFrame: changing a locked atlas is invalid.
    internal unsafe void Apply(float scale, float pixelDensity)
    {
        if (scale == _scale && pixelDensity == _pixelDensity)
            return;

        ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;
        // Silk.NET allocates immutable storage; rebuilt atlases need our own mutable texture.
        if (_texture == 0)
            _texture = _gl.GenTexture();
        for (int index = 0; index < _referenceSizes.Length; index++)
            atlas.ConfigData[index].SizePixels = _referenceSizes[index] * scale * pixelDensity;
        if (!atlas.Build())
            throw new InvalidOperationException("Unable to rebuild the ImGui font atlas.");

        // Font metrics remain in window coordinates; the texture holds physical pixels.
        for (int index = 0; index < atlas.Fonts.Size; index++)
            atlas.Fonts[index].Scale = 1f / pixelDensity;
        ImGui.GetIO().FontGlobalScale = 1f;

        atlas.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height);
        _gl.GetInteger(GLEnum.TextureBinding2D, out int previousTexture);
        _gl.GetInteger(GLEnum.UnpackAlignment, out int previousAlignment);
        _gl.GetInteger(GLEnum.UnpackRowLength, out int previousRowLength);
        _gl.GetInteger(GLEnum.UnpackSkipPixels, out int previousSkipPixels);
        _gl.GetInteger(GLEnum.UnpackSkipRows, out int previousSkipRows);
        _gl.GetInteger(GLEnum.PixelUnpackBufferBinding, out int previousBuffer);
        try
        {
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
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
        // Build clears TexID, so publish the uploaded texture before the next NewFrame.
        atlas.SetTexID((IntPtr)_texture);
        atlas.ClearTexData();
        _scale = scale;
        _pixelDensity = pixelDensity;
    }

    public void Dispose()
    {
        if (_texture == 0)
            return;
        _gl.DeleteTexture(_texture);
        _texture = 0;
    }
}
