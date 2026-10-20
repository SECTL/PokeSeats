using System.Numerics;
using ImGuiNET;
using Silk.NET.Input;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI;

internal sealed class UiScaling
{
    private ImGuiStyle _baseline;
    private float _appliedScale;
    private int _adjustmentSteps;
    private int _shortcutWasDown;
    private double _showScaleUntil;
    private float _lastDisplayScale = 1f;

    internal float ScaleForDisplay(float displayScale)
        => Math.Clamp(UiScale.ForDisplayScale(displayScale) + _adjustmentSteps * 0.05f, 0.5f, 3f);

    internal void HandleShortcut(IInputContext input, float displayScale)
    {
        int down = 0;
        foreach (IKeyboard keyboard in input.Keyboards)
        {
            if (!(keyboard.IsKeyPressed(Key.ControlLeft) || keyboard.IsKeyPressed(Key.ControlRight))
                || keyboard.IsKeyPressed(Key.AltLeft) || keyboard.IsKeyPressed(Key.AltRight))
                continue;
            if (keyboard.IsKeyPressed(Key.Equal) || keyboard.IsKeyPressed(Key.KeypadAdd) || keyboard.IsKeyPressed(Key.KeypadEqual)) down |= 1;
            if (keyboard.IsKeyPressed(Key.Minus) || keyboard.IsKeyPressed(Key.KeypadSubtract)) down |= 2;
            if (keyboard.IsKeyPressed(Key.Number0) || keyboard.IsKeyPressed(Key.Keypad0)) down |= 4;
        }
        int pressed = down & ~_shortcutWasDown;
        _shortcutWasDown = down;
        if ((pressed & 4) != 0)
            _adjustmentSteps = 0;
        else if ((pressed & 3) is 1 or 2)
        {
            int direction = (pressed & 1) != 0 ? 1 : -1;
            float scale = ScaleForDisplay(displayScale);
            if (direction > 0 ? scale >= 3f : scale <= 0.5f)
                return;
            _adjustmentSteps += direction;
        }
        else
            return;
        _showScaleUntil = ImGui.GetTime() + 1.5;
    }

    internal void DrawScaleIndicator()
    {
        if (ImGui.GetTime() >= _showScaleUntil)
            return;
        var viewport = ImGui.GetMainViewport();
        string text = $"UI {UiScale.Current:F2}x";
        Vector2 padding = new(UiScale.Pixels(8));
        Vector2 size = ImGui.CalcTextSize(text) + padding * 2;
        Vector2 position = viewport.Pos + viewport.Size - size - padding;
        var draw = ImGui.GetForegroundDrawList();
        draw.AddRectFilled(position, position + size, 0xE6222222, UiScale.Pixels(4));
        draw.AddText(position + padding, 0xFFFFFFFF, text);
    }

    // Workspace activation can replace the theme, so retain its unscaled style.
    internal unsafe void CaptureStyle()
    {
        _baseline = *ImGui.GetStyle().NativePtr;
        _appliedScale = 0f;
    }

    internal unsafe void RestoreStyle()
    {
        *ImGui.GetStyle().NativePtr = _baseline;
        _appliedScale = 0f;
    }

    internal void ApplyLast() => Apply(_lastDisplayScale);

    internal unsafe void Apply(float displayScale)
    {
        _lastDisplayScale = displayScale;
        float scale = ScaleForDisplay(displayScale);
        if (scale == _appliedScale)
            return;

        ImGuiStylePtr style = ImGui.GetStyle();
        *style.NativePtr = _baseline;
        style.ScaleAllSizes(scale);
        UiScale.Set(scale);
        _appliedScale = scale;
    }
}
