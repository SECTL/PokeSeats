using Silk.NET.Maths;

namespace PokeSeats.UI.Rendering;

internal interface IUiRenderer : IDisposable
{
    void Update(float delta);
    void Render();
    void Resize(Vector2D<int> size);
    void UpdateFontTexture();
}
