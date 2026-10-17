using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.Core.Contexts;
using Silk.NET.Windowing;

namespace PokeSeats.UI;

/// <summary>
/// Resolves the operating-system display scale independently of the window size. UI scale
/// must follow DPI: resizing a window should reveal more content instead of enlarging every
/// control. Windows uses the per-window DPI with a Windows 7 compatible GDI fallback;
/// macOS and Linux stay in logical coordinates and rely on the framebuffer density for
/// crisp rasterization.
/// </summary>
internal static class DisplayScale
{
    private const int LogPixelsX = 88;

    private static bool _windowDpiUnavailable;
    private static float? _systemScale;

    /// <summary>Current scale of the monitor hosting <paramref name="window"/>.</summary>
    public static float ForWindow(IWindow window)
    {
        if (!OperatingSystem.IsWindows())
            return 1f;

        IntPtr handle = GetWindowHandle(window);
        return handle != IntPtr.Zero ? GetWindowScale(handle) : GetSystemScale();
    }

    [SupportedOSPlatform("windows")]
    private static float GetWindowScale(IntPtr handle)
    {
        if (!_windowDpiUnavailable)
        {
            try
            {
                uint dpi = GetDpiForWindow(handle);
                if (dpi > 0)
                    return dpi / 96f;
            }
            catch (EntryPointNotFoundException)
            {
                // Windows 7 has no per-window DPI API; fall back to the system DPI.
                _windowDpiUnavailable = true;
            }
            catch (DllNotFoundException)
            {
                _windowDpiUnavailable = true;
            }
        }

        return GetSystemScale();
    }

    [SupportedOSPlatform("windows")]
    private static float GetSystemScale()
    {
        if (_systemScale is float cached)
            return cached;

        float scale;
        try
        {
            uint dpi = GetDpiForSystem();
            scale = dpi > 0 ? dpi / 96f : ReadGdiDpi() / 96f;
        }
        catch (EntryPointNotFoundException)
        {
            scale = ReadGdiDpi() / 96f;
        }
        catch (DllNotFoundException)
        {
            scale = 1f;
        }

        _systemScale = scale;
        return scale;
    }

    [SupportedOSPlatform("windows")]
    private static int ReadGdiDpi()
    {
        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero)
            return 96;

        try
        {
            int dpi = GetDeviceCaps(dc, LogPixelsX);
            return dpi > 0 ? dpi : 96;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr GetWindowHandle(IWindow window)
    {
        INativeWindow? native = window.Native;
        if (native is null)
            return IntPtr.Zero;
        if (native.Win32 is { } win32 && win32.Hwnd != IntPtr.Zero)
            return win32.Hwnd;
        return native.DXHandle.GetValueOrDefault();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);
}
