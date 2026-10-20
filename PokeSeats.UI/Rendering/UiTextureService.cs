using PokeSeats.UI.PluginContracts;
using PokeSeats.UI.Rendering;
using StbImageSharp;

namespace PokeSeats.UI;

/// <summary>
/// Decodes embedded PNG/JPEG bytes into textures owned by the active UI renderer.
/// The service is shared by every workspace; each texture belongs to its caller.
/// </summary>
internal sealed class UiTextureService : IUiTextureService
{
    private readonly IUiRenderer _renderer;

    public UiTextureService(IUiRenderer renderer)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
    }

    public IUiTexture? Create(ReadOnlySpan<byte> encodedImage)
    {
        try
        {
            ImageResult image = ImageResult.FromMemory(encodedImage.ToArray(), ColorComponents.RedGreenBlueAlpha);
            if (image.Data is null || image.Width <= 0 || image.Height <= 0)
                return null;

            IntPtr textureId = _renderer.CreateTexture(image.Data, image.Width, image.Height);
            return new Texture(textureId, image.Width, image.Height, _renderer);
        }
        catch (Exception exception)
        {
            // An embedded asset must never take the application down.
            Console.Error.WriteLine($"Unable to decode an embedded image: {exception.Message}");
            return null;
        }
    }

    private sealed class Texture : IUiTexture
    {
        private readonly IUiRenderer _renderer;
        private bool _disposed;

        internal Texture(IntPtr textureId, int width, int height, IUiRenderer renderer)
        {
            TextureId = textureId;
            Width = width;
            Height = height;
            _renderer = renderer;
        }

        public IntPtr TextureId { get; }
        public int Width { get; }
        public int Height { get; }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _renderer.DeleteTexture(TextureId);
        }
    }
}
