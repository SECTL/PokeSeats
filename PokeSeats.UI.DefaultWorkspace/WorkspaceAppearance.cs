using System.Numerics;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultWorkspace;

/// <summary>
/// User-editable appearance. Four base colors drive the rest of the semantic palette so
/// status colors keep coming from the selected light/dark preset.
/// </summary>
internal readonly record struct WorkspaceAppearance(
    bool Dark,
    Vector4 Background,
    Vector4 Surface,
    Vector4 Text,
    Vector4 Accent)
{
    public static WorkspaceAppearance FromPreset(bool dark)
    {
        UiColors colors = (dark ? UiDesignTokens.Dark : UiDesignTokens.Light).Colors;
        return new WorkspaceAppearance(dark, colors.Background, colors.Surface, colors.Text, colors.Accent)
            .Normalize();
    }

    public WorkspaceAppearance Normalize()
        => new(Dark, NormalizeColor(Background), NormalizeColor(Surface), NormalizeColor(Text), NormalizeColor(Accent));

    public static Vector4 NormalizeColor(Vector4 color)
        => new(
            Math.Clamp(color.X, 0f, 1f),
            Math.Clamp(color.Y, 0f, 1f),
            Math.Clamp(color.Z, 0f, 1f),
            1f);

    public UiDesignTokens ToTokens()
    {
        UiDesignTokens preset = Dark ? UiDesignTokens.Dark : UiDesignTokens.Light;
        UiColors colors = preset.Colors with
        {
            Background = Background,
            Surface = Surface,
            Canvas = Vector4.Lerp(Background, Surface, 0.5f),
            Text = Text,
            TextMuted = Vector4.Lerp(Text, Background, 0.38f),
            Border = Vector4.Lerp(Text, Background, 0.55f),
            Accent = Accent,
            AccentSurface = Vector4.Lerp(Surface, Accent, 0.20f),
            Selection = new Vector4(Accent.X, Accent.Y, Accent.Z, 0.22f),
            SelectionBorder = Accent,
        };
        return preset with { Colors = colors };
    }
}
