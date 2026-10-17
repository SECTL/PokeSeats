using ImGuiNET;

namespace PokeSeats.UI.Fonts;


/// <summary>Rasterizes the retained font outlines at the current framebuffer resolution.</summary>
internal sealed class FontAtlasScaling
{
    private readonly Action _updateFontTexture;
    private readonly float[] _referenceSizes;
    private float _scale = 1f;
    private float _pixelDensity = 1f;

    internal FontAtlasScaling(Action updateFontTexture)
    {
        _updateFontTexture = updateFontTexture;
        var configs = ImGui.GetIO().Fonts.ConfigData;
        _referenceSizes = new float[configs.Size];
        for (int index = 0; index < configs.Size; index++)
            _referenceSizes[index] = configs[index].SizePixels;
    }

    // Called between Render and Update/NewFrame: changing a locked atlas is invalid.
    internal void Apply(float scale, float pixelDensity)
    {
        if (scale == _scale && pixelDensity == _pixelDensity)
            return;

        ImFontAtlasPtr atlas = ImGui.GetIO().Fonts;
        for (int index = 0; index < _referenceSizes.Length; index++)
            atlas.ConfigData[index].SizePixels = _referenceSizes[index] * scale * pixelDensity;
        if (!atlas.Build())
            throw new InvalidOperationException("Unable to rebuild the ImGui font atlas.");

        // Font metrics remain in window coordinates; the texture holds physical pixels.
        for (int index = 0; index < atlas.Fonts.Size; index++)
            atlas.Fonts[index].Scale = 1f / pixelDensity;
        ImGui.GetIO().FontGlobalScale = 1f;

        _updateFontTexture();
        _scale = scale;
        _pixelDensity = pixelDensity;
    }

}
