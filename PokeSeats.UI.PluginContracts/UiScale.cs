using System.Numerics;

namespace PokeSeats.UI.PluginContracts;

/// <summary>
/// Shared UI scale for the desktop host. The automatic base scale follows the operating
/// system display DPI; <see cref="ForViewport"/> remains available for callers that
/// intentionally size content by viewport.
/// </summary>
public static class UiScale
{
    public const float ReferenceWidth = 1280f;
    public const float ReferenceHeight = 720f;

    public static float Current { get; private set; } = 1f;

    /// <summary>Base scale for an OS display scale; window resizing does not inflate the UI.</summary>
    public static float ForDisplayScale(float displayScale)
    {
        if (!float.IsFinite(displayScale) || displayScale <= 0f)
            return 1f;
        return MathF.Max(1f, displayScale);
    }

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
