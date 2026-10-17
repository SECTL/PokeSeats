using System.Numerics;

namespace PokeSeats.UI.PluginContracts;

/// <summary>
/// Immutable workspace theme shared with views. Colors are normalized RGBA; metrics are
/// reference pixels and must be converted with UiScale.Pixels when drawing custom content.
/// Keep the same instance for an activation. Use with-expressions to customize a preset.
/// </summary>
public sealed record UiDesignTokens
{
    public UiColors Colors { get; init; } = new();
    public UiMetrics Metrics { get; init; } = new();

    public static UiDesignTokens Light { get; } = new();
    public static UiDesignTokens Dark { get; } = new()
    {
        Colors = new UiColors
        {
            Background = new(.09f, .10f, .12f, 1),
            Surface = new(.16f, .18f, .21f, 1),
            Canvas = new(.12f, .14f, .17f, 1),
            Text = new(.94f, .96f, .98f, 1),
            TextMuted = new(.65f, .70f, .76f, 1),
            Border = new(.36f, .41f, .47f, 1),
            Accent = new(.45f, .73f, 1, 1),
            AccentSurface = new(.16f, .31f, .46f, 1),
            Selection = new(.25f, .57f, .90f, .25f),
            SelectionBorder = new(.50f, .80f, 1, 1),
            Success = new(.46f, .85f, .60f, 1),
            SuccessSurface = new(.16f, .35f, .24f, 1),
            Warning = new(1, .80f, .37f, 1),
            WarningSurface = new(.40f, .30f, .13f, 1),
            Danger = new(1, .55f, .55f, 1),
            DangerSurface = new(.38f, .15f, .18f, 1),
            DangerHovered = new(.48f, .20f, .24f, 1),
            SecondarySurface = new(.32f, .23f, .39f, 1)
        }
    };
}

/// <summary>
/// Semantic colors independent of business cell types and ImGui. Text and TextMuted are
/// intended for Canvas, Surface and semantic surface fills. Status colors are foregrounds.
/// Selection is a translucent overlay; SelectionBorder is its opaque outline.
/// </summary>
public sealed record UiColors
{
    public Vector4 Background { get; init; } = new(.90f, .91f, .93f, 1);
    public Vector4 Surface { get; init; } = new(.97f, .98f, .99f, 1);
    public Vector4 Canvas { get; init; } = new(.94f, .95f, .97f, 1);
    public Vector4 Text { get; init; } = new(.12f, .16f, .20f, 1);
    public Vector4 TextMuted { get; init; } = new(.35f, .40f, .44f, 1);
    public Vector4 Border { get; init; } = new(.56f, .60f, .66f, 1);
    public Vector4 Accent { get; init; } = new(.10f, .36f, .65f, 1);
    public Vector4 AccentSurface { get; init; } = new(.79f, .88f, .98f, 1);
    public Vector4 Selection { get; init; } = new(.20f, .55f, .95f, .22f);
    public Vector4 SelectionBorder { get; init; } = new(.10f, .39f, .73f, 1);
    public Vector4 Success { get; init; } = new(.12f, .43f, .24f, 1);
    public Vector4 SuccessSurface { get; init; } = new(.80f, .92f, .84f, 1);
    public Vector4 Warning { get; init; } = new(.52f, .33f, .04f, 1);
    public Vector4 WarningSurface { get; init; } = new(.97f, .89f, .69f, 1);
    public Vector4 Danger { get; init; } = new(.73f, .08f, .12f, 1);
    public Vector4 DangerSurface { get; init; } = new(.99f, .79f, .80f, 1);
    public Vector4 DangerHovered { get; init; } = new(.98f, .69f, .71f, 1);
    public Vector4 SecondarySurface { get; init; } = new(.88f, .82f, .95f, 1);
}

/// <summary>Unscaled reference-pixel spacing, radii and strokes. Font sizing remains host-owned.</summary>
public sealed record UiMetrics
{
    public float SpaceSmall { get; init; } = 4;
    public float SpaceMedium { get; init; } = 8;
    public float SpaceLarge { get; init; } = 16;
    public float WindowRounding { get; init; } = 8;
    public float ControlRounding { get; init; } = 6;
    public float BorderWidth { get; init; } = 1;
}
