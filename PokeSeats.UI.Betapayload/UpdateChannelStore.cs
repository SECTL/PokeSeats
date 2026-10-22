using System.Text;
using System.Text.Json;

namespace PokeSeats.UI.Betapayload;

/// <summary>
/// Remembers the chosen release channel in the plugin data directory. A missing, unreadable or
/// unknown value falls back to stable, and writes go through a temporary file.
/// </summary>
internal sealed class UpdateChannelStore
{
    private const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;

    public UpdateChannelStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A settings path is required.", nameof(path));
        _path = path;
    }

    public UpdateChannel Load()
    {
        try
        {
            if (!File.Exists(_path))
                return UpdateChannel.Stable;

            StoredChannel? stored = JsonSerializer.Deserialize<StoredChannel>(
                File.ReadAllText(_path, Encoding.UTF8), SerializerOptions);
            return stored?.Channel == "prerelease" ? UpdateChannel.Prerelease : UpdateChannel.Stable;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Unable to read the update channel '{_path}': {exception.Message}");
            return UpdateChannel.Stable;
        }
    }

    public void Save(UpdateChannel channel)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var stored = new StoredChannel
        {
            Schema = CurrentSchema,
            Channel = channel == UpdateChannel.Prerelease ? "prerelease" : "stable"
        };
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(stored, SerializerOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed class StoredChannel
    {
        public int Schema { get; set; } = CurrentSchema;
        public string? Channel { get; set; }
    }
}
