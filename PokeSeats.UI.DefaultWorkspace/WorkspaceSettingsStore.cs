using System.Numerics;
using System.Text;
using System.Text.Json;

namespace PokeSeats.UI.DefaultWorkspace;

/// <summary>
/// Persists the default workspace appearance in its plugin data directory. Missing or
/// malformed files fall back to the light preset; writes are atomic.
/// </summary>
internal sealed class WorkspaceSettingsStore
{
    private const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;

    public WorkspaceSettingsStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A settings path is required.", nameof(path));
        _path = path;
    }

    public WorkspaceAppearance Load()
    {
        WorkspaceAppearance fallback = WorkspaceAppearance.FromPreset(dark: false);
        try
        {
            if (!File.Exists(_path))
                return fallback;

            StoredAppearance? stored = JsonSerializer.Deserialize<StoredAppearance>(
                File.ReadAllText(_path, Encoding.UTF8), SerializerOptions);
            if (stored is null)
                return fallback;

            WorkspaceAppearance preset = WorkspaceAppearance.FromPreset(stored.Dark);
            return new WorkspaceAppearance(
                stored.Dark,
                ReadColor(stored.Background, preset.Background),
                ReadColor(stored.Surface, preset.Surface),
                ReadColor(stored.Text, preset.Text),
                ReadColor(stored.Accent, preset.Accent));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Unable to read workspace appearance '{_path}': {exception.Message}");
            return fallback;
        }
    }

    public void Save(WorkspaceAppearance appearance)
    {
        appearance = appearance.Normalize();
        var stored = new StoredAppearance
        {
            Schema = CurrentSchema,
            Dark = appearance.Dark,
            Background = ToArray(appearance.Background),
            Surface = ToArray(appearance.Surface),
            Text = ToArray(appearance.Text),
            Accent = ToArray(appearance.Accent)
        };

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(stored, SerializerOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static Vector4 ReadColor(float[]? values, Vector4 fallback)
    {
        if (values is not { Length: >= 3 })
            return fallback;

        return new Vector4(
            Math.Clamp(values[0], 0f, 1f),
            Math.Clamp(values[1], 0f, 1f),
            Math.Clamp(values[2], 0f, 1f),
            1f);
    }

    private static float[] ToArray(Vector4 color) => new[] { color.X, color.Y, color.Z, color.W };

    private sealed class StoredAppearance
    {
        public int Schema { get; set; } = CurrentSchema;
        public bool Dark { get; set; }
        public float[]? Background { get; set; }
        public float[]? Surface { get; set; }
        public float[]? Text { get; set; }
        public float[]? Accent { get; set; }
    }
}
