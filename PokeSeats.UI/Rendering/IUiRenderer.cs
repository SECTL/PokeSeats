using Silk.NET.Maths;

namespace PokeSeats.UI.Rendering;

internal interface IUiRenderer : IDisposable
{
    void Update(float delta);
    void Render();
    void Resize(Vector2D<int> size);
    void UpdateFontTexture();

    /// <summary>Uploads RGBA8 pixels and returns an ImTextureID; call on the UI thread.</summary>
    IntPtr CreateTexture(ReadOnlySpan<byte> rgba, int width, int height);

    /// <summary>Releases a texture returned by <see cref="CreateTexture"/>.</summary>
    void DeleteTexture(IntPtr texture);
}
