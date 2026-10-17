using System.Text;
using System.Text.Json;

namespace PokeSeats.UI;

/// <summary>
/// Reads and writes <c>config.json</c> in the data root. The file holds the stable device id
/// and the last workspace. A missing or malformed file falls back to defaults and is replaced
/// on the next save; writes go through a temporary file so a crash cannot truncate it.
/// </summary>
internal sealed class AppConfigStore
{
    private const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _path;
    private readonly StoredConfig _config;

    public AppConfigStore(string dataRoot, string? legacyWorkspaceId)
    {
        _path = Path.Combine(dataRoot, "config.json");
        _config = Load(_path);

        // The machine-derived id always wins so a copied data folder adopts the new device.
        // Without a machine id the saved value, or finally a fresh UUID, keeps the id stable.
        Guid resolved = DeviceIdentity.TryDerive()
            ?? (_config.DeviceId != Guid.Empty ? _config.DeviceId : Guid.NewGuid());
        bool changed = _config.Schema != CurrentSchema || _config.DeviceId != resolved;
        _config.Schema = CurrentSchema;
        _config.DeviceId = resolved;

        if (string.IsNullOrWhiteSpace(_config.WorkspaceId) && !string.IsNullOrWhiteSpace(legacyWorkspaceId))
        {
            _config.WorkspaceId = legacyWorkspaceId.Trim();
            changed = true;
        }

        if (changed || !File.Exists(_path))
        {
            try
            {
                Save();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"Unable to save configuration '{_path}': {exception.Message}");
            }
        }
    }

    public Guid DeviceId => _config.DeviceId;

    public string? WorkspaceId => _config.WorkspaceId;

    public void SetWorkspace(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));

        if (string.Equals(_config.WorkspaceId, workspaceId, StringComparison.Ordinal))
            return;

        _config.WorkspaceId = workspaceId;
        Save();
    }

    private static StoredConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new StoredConfig { Schema = CurrentSchema };

            StoredConfig? stored = JsonSerializer.Deserialize<StoredConfig>(
                File.ReadAllText(path, Encoding.UTF8), SerializerOptions);
            return stored ?? new StoredConfig { Schema = CurrentSchema };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Unable to read configuration '{path}': {exception.Message}");
            return new StoredConfig { Schema = CurrentSchema };
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = _path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(_config, SerializerOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private sealed class StoredConfig
    {
        public int Schema { get; set; }
        public Guid DeviceId { get; set; }
        public string? WorkspaceId { get; set; }
    }
}
