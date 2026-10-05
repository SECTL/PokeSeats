using System.Text.Json;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.Plugins;

/// <summary>A plugin.json manifest that passed schema validation.</summary>
internal sealed record PluginFontRequest(
    string Family,
    float SizePixels,
    string GlyphRange);

/// <summary>A plugin.json manifest that passed schema validation.</summary>
internal sealed record PluginManifest(
    string Id,
    string Assembly,
    string EntryPoint,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<PluginFontRequest> Fonts);

/// <summary>
/// Result of reading one manifest. <see cref="DeclaredId"/> is set whenever the manifest declared
/// an id that is safe to use as a data directory name — even if a later field failed validation —
/// so dependency diagnostics can still name the plugin.
/// </summary>
internal sealed record ManifestParseResult(PluginManifest? Manifest, string? DeclaredId, string? Error)
{
    internal bool Success => Manifest is not null;
}

/// <summary>Reads and validates plugin.json manifests against the host schema.</summary>
internal static class PluginManifestParser
{
    private static readonly string[] ReservedWindowsNames =
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>Absolute path of the manifest's assembly. Only valid after a successful parse.</summary>
    internal static string ResolveAssemblyPath(string pluginDirectory, string assembly)
        => Path.GetFullPath(Path.Combine(pluginDirectory, assembly));

    internal static ManifestParseResult Parse(string manifestPath, string pluginDirectory)
    {
        string text;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception exception)
        {
            return new ManifestParseResult(null, null, $"cannot read manifest: {PluginDiagnostics.Describe(exception)}");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException exception)
        {
            return new ManifestParseResult(null, null, $"manifest is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ManifestParseResult(null, null, "the manifest root must be a JSON object");

            var id = ReadString(root, "id");
            if (string.IsNullOrWhiteSpace(id))
                return new ManifestParseResult(null, null, "missing or invalid \"id\" (expected a non-empty string)");
            if (!IsSafeDirectoryName(id))
                return new ManifestParseResult(null, null,
                    $"id '{id}' cannot be used as a plugin data directory name (no path separators, invalid file name characters, trailing dots or Windows device names)");
            var declaredId = id;

            if (!TryReadInt32(root, "apiVersion", out var apiVersion))
                return new ManifestParseResult(null, declaredId, $"missing or invalid \"apiVersion\" (this host expects the integer {PluginApi.Version})");
            if (apiVersion != PluginApi.Version)
                return new ManifestParseResult(null, declaredId,
                    $"unsupported apiVersion {apiVersion}; this host supports apiVersion {PluginApi.Version}");

            var assembly = ReadString(root, "assembly");
            if (string.IsNullOrWhiteSpace(assembly) || !assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return new ManifestParseResult(null, declaredId, "missing or invalid \"assembly\" (expected a file path ending in .dll)");
            if (Path.IsPathRooted(assembly))
                return new ManifestParseResult(null, declaredId, "\"assembly\" must be a relative path inside the plugin directory");

            string assemblyPath;
            try
            {
                assemblyPath = ResolveAssemblyPath(pluginDirectory, assembly);
            }
            catch (Exception exception)
            {
                return new ManifestParseResult(null, declaredId,
                    $"\"assembly\" value '{assembly}' is not a valid file path: {PluginDiagnostics.Describe(exception)}");
            }

            if (!IsWithinDirectory(pluginDirectory, assemblyPath))
                return new ManifestParseResult(null, declaredId, "\"assembly\" must resolve to a file inside the plugin directory");
            if (!File.Exists(assemblyPath))
                return new ManifestParseResult(null, declaredId, $"assembly file '{assembly}' was not found");

            var entryPoint = ReadString(root, "entryPoint");
            if (string.IsNullOrWhiteSpace(entryPoint))
                return new ManifestParseResult(null, declaredId, "missing or invalid \"entryPoint\" (expected a fully qualified type name)");

            var dependencies = new List<string>();
            if (root.TryGetProperty("dependencies", out var dependenciesElement))
            {
                if (dependenciesElement.ValueKind != JsonValueKind.Array)
                    return new ManifestParseResult(null, declaredId, "\"dependencies\" must be an array of plugin ids");
                foreach (var dependencyElement in dependenciesElement.EnumerateArray())
                {
                    var dependency = dependencyElement.ValueKind == JsonValueKind.String ? dependencyElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(dependency))
                        return new ManifestParseResult(null, declaredId, "\"dependencies\" must contain only non-empty plugin ids");
                    if (!dependencies.Contains(dependency, StringComparer.Ordinal))
                        dependencies.Add(dependency);
                }
            }

            var fonts = new List<PluginFontRequest>();
            if (root.TryGetProperty("fonts", out var fontsElement))
            {
                if (fontsElement.ValueKind != JsonValueKind.Array)
                    return new ManifestParseResult(null, declaredId, "\"fonts\" must be an array of font requests");
                foreach (var fontElement in fontsElement.EnumerateArray())
                {
                    if (fontElement.ValueKind != JsonValueKind.Object)
                        return new ManifestParseResult(null, declaredId, "\"fonts\" must contain objects");
                    var family = ReadString(fontElement, "family");
                    var glyphRange = ReadString(fontElement, "glyphRange") ?? "ChineseSimplifiedCommon";
                    if (string.IsNullOrWhiteSpace(family))
                        return new ManifestParseResult(null, declaredId, "font request is missing a non-empty \"family\"");
                    if (glyphRange is not ("Default" or "ChineseSimplifiedCommon" or "ChineseFull" or "Cyrillic" or "Greek" or "Japanese" or "Korean" or "Thai" or "Vietnamese"))
                        return new ManifestParseResult(null, declaredId, $"font '{family}' has unsupported glyphRange '{glyphRange}'");
                    var sizePixels = 18f;
                    if (fontElement.TryGetProperty("sizePixels", out var sizeElement)
                        && (sizeElement.ValueKind != JsonValueKind.Number || !sizeElement.TryGetSingle(out sizePixels)))
                        return new ManifestParseResult(null, declaredId, $"font '{family}' has invalid sizePixels");
                    if (sizePixels < 8 || sizePixels > 128)
                        return new ManifestParseResult(null, declaredId, $"font '{family}' sizePixels must be between 8 and 128");
                    if (!fonts.Any(font => string.Equals(font.Family, family, StringComparison.OrdinalIgnoreCase)))
                        fonts.Add(new PluginFontRequest(family, sizePixels, glyphRange));
                }
            }

            return new ManifestParseResult(new PluginManifest(id, assembly, entryPoint, dependencies, fonts), declaredId, null);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryReadInt32(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.Number
               && property.TryGetInt32(out value);
    }

    /// <summary>An id is used as a single directory name below the plugin data root.</summary>
    private static bool IsSafeDirectoryName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (!string.Equals(id, id.Trim(), StringComparison.Ordinal)) return false;
        if (id is "." or "..") return false;
        if (id.EndsWith(".", StringComparison.Ordinal)) return false;
        if (id.IndexOf('\\') >= 0 || id.IndexOf('/') >= 0) return false;
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        var dotIndex = id.IndexOf('.');
        var baseName = dotIndex < 0 ? id : id[..dotIndex];
        foreach (var reservedName in ReservedWindowsNames)
        {
            if (string.Equals(baseName, reservedName, StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>Lexical containment check; the manifest assembly must live inside the plugin directory.</summary>
    private static bool IsWithinDirectory(string directory, string candidatePath)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(candidatePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return fullPath.StartsWith(root, comparison);
    }
}
