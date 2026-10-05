using System.Runtime.InteropServices;
using ImGuiNET;
using PokeSeats.UI.Plugins;
using SixLabors.Fonts;

namespace PokeSeats.UI.Fonts;

/// <summary>
/// Resolves manifest font requests before ImGui builds its atlas. The built-in HarmonyOS Sans
/// face is embedded in this assembly; other requests are resolved from common system font roots.
/// </summary>
internal static class PluginFontLoader
{
    private const string EmbeddedHarmonyFamily = "HarmonyOS Sans SC";
    private const string EmbeddedHarmonyResource = "PokeSeats.UI.Fonts.Assets.HarmonyOS_Sans_SC_Regular.ttf";

    private static readonly string[] FontFileExtensions = { ".ttf", ".otf", ".ttc" };

    /// <summary>
    /// Adds every resolvable request to the atlas. The first font added becomes ImGui's default.
    /// </summary>
    internal static void AddRequestedFonts(ImFontAtlasPtr fonts, IEnumerable<PluginFontRequest> requests)
    {
        foreach (var request in requests)
        {
            try
            {
                if (string.Equals(request.Family, EmbeddedHarmonyFamily, StringComparison.OrdinalIgnoreCase))
                {
                    AddEmbeddedHarmonyFont(fonts, request);
                    Console.WriteLine($"[Fonts] loaded {request.Family} from embedded resource");
                    continue;
                }

                var path = FindInstalledFont(request.Family);
                if (path is null)
                {
                    Console.Error.WriteLine($"[Fonts] font '{request.Family}' was not found in common system font directories; request skipped");
                    continue;
                }

                fonts.AddFontFromFileTTF(path, request.SizePixels, default, GetGlyphRange(fonts, request.GlyphRange));
                Console.WriteLine($"[Fonts] loaded {request.Family} from {path}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Fonts] font '{request.Family}' failed: {exception.Message}");
            }
        }
    }

    private static void AddEmbeddedHarmonyFont(ImFontAtlasPtr fonts, PluginFontRequest request)
    {
        using Stream? resource = typeof(PluginFontLoader).Assembly.GetManifestResourceStream(EmbeddedHarmonyResource);
        if (resource is null)
            throw new FileNotFoundException($"embedded font resource '{EmbeddedHarmonyResource}' was not found");

        byte[] data = ReadAllBytes(resource);
        IntPtr nativeData = ImGui.MemAlloc((uint)data.Length);
        if (nativeData == IntPtr.Zero)
            throw new OutOfMemoryException("ImGui could not allocate the embedded font buffer");
        try
        {
            Marshal.Copy(data, 0, nativeData, data.Length);
            fonts.AddFontFromMemoryTTF(nativeData, data.Length, request.SizePixels, default,
                GetGlyphRange(fonts, request.GlyphRange));
            // ImGui owns memory returned by ImGui.MemAlloc after a successful add.
            nativeData = IntPtr.Zero;
        }
        finally
        {
            if (nativeData != IntPtr.Zero)
                ImGui.MemFree(nativeData);
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string? FindInstalledFont(string family)
    {
        foreach (string root in GetFontRoots())
        {
            if (!Directory.Exists(root))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(path => FontFileExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[Fonts] unable to scan '{root}': {exception.Message}");
                continue;
            }

            string? fallback = null;
            foreach (string path in files)
            {
                if (!TryGetFontDescription(path, out FontDescription? description) || description is null)
                    continue;
                if (!string.Equals(description.FontFamilyInvariantCulture, family, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (description.Style == FontStyle.Regular)
                    return path;
                fallback ??= path;
            }

            if (fallback is not null)
                return fallback;
        }

        return null;
    }

    private static bool TryGetFontDescription(string path, out FontDescription? description)
    {
        try
        {
            description = FontDescription.LoadDescription(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            description = null;
            return false;
        }
    }

    private static IEnumerable<string> GetFontRoots()
    {
        string? home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".fonts");
            yield return Path.Combine(home, ".local", "share", "fonts");
            yield return Path.Combine(home, "Library", "Fonts");
        }

        if (OperatingSystem.IsWindows())
        {
            string windowsFonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (!string.IsNullOrWhiteSpace(windowsFonts))
                yield return windowsFonts;
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Library/Fonts";
            yield return "/System/Library/Fonts";
            yield return "/System/Library/Fonts/Supplemental";
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
        }
    }

    private static IntPtr GetGlyphRange(ImFontAtlasPtr fonts, string range)
        => range switch
        {
            "Default" => fonts.GetGlyphRangesDefault(),
            // Keep the manifest name for compatibility, but load the complete Chinese range.
            // The common preset omits valid GB2312 characters such as 帧.
            "ChineseSimplifiedCommon" or "ChineseFull" => fonts.GetGlyphRangesChineseFull(),
            "Cyrillic" => fonts.GetGlyphRangesCyrillic(),
            "Greek" => fonts.GetGlyphRangesGreek(),
            "Japanese" => fonts.GetGlyphRangesJapanese(),
            "Korean" => fonts.GetGlyphRangesKorean(),
            "Thai" => fonts.GetGlyphRangesThai(),
            "Vietnamese" => fonts.GetGlyphRangesVietnamese(),
            _ => throw new ArgumentException($"Unsupported glyph range '{range}'", nameof(range))
        };
}
