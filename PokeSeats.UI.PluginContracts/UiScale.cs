using System.Numerics;

namespace PokeSeats.UI.PluginContracts;

/// <summary>
/// Shared UI scale derived from the desktop host's default 1280 x 720 client area.
/// Values below the reference size keep scale 1 so compact windows remain usable.
/// </summary>
public static class UiScale
{
    public const float ReferenceWidth = 1280f;
    public const float ReferenceHeight = 720f;

    public static float Current { get; private set; } = 1f;

    public static float ForViewport(Vector2 viewportSize)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return 1f;

        return MathF.Max(1f, MathF.Min(
            viewportSize.X / ReferenceWidth,
            viewportSize.Y / ReferenceHeight));
    }

    public static void Set(float scale)
    {
        if (!float.IsFinite(scale) || scale <= 0f)
            throw new ArgumentOutOfRangeException(nameof(scale));
        Current = scale;
    }

    public static float Pixels(float referencePixels) => referencePixels * Current;
}
