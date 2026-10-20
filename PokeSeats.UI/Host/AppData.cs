using System.Text;

namespace PokeSeats.UI;

/// <summary>
/// Resolves the writable data root. The preferred location is <c>data</c> beside the
/// executable, which keeps a portable installation self-contained. When that directory is
/// not writable (read-only install prefixes, signed macOS bundles) the host falls back to the
/// per-user application data folder. Data written by earlier versions is copied into the new
/// root on first start; the old files are left untouched.
/// </summary>
internal sealed class AppData
{
    private const string DataFolderName = "data";
    private const string ApplicationFolderName = "PokeSeats";

    private AppData(string root, bool isFallback)
    {
        Root = root;
        IsFallback = isFallback;
    }

    public string Root { get; }

    /// <summary>True when the application directory was not writable and the user data root is used.</summary>
    public bool IsFallback { get; }

    public string PluginsDirectory => Path.Combine(Root, "plugins");
    public string ConfigPath => Path.Combine(Root, "config.json");
    public string ImGuiIniPath => Path.Combine(Root, "imgui.ini");

    /// <summary>Per-user root used by versions before data moved beside the executable.</summary>
    public static string LegacyRoot { get; } = ResolveLegacyRoot();

    public static AppData Resolve()
    {
        string preferred = Path.Combine(AppContext.BaseDirectory, DataFolderName);
        if (TryPrepare(preferred))
            return new AppData(preferred, isFallback: false);

        string fallback = LegacyRoot;
        if (!TryPrepare(fallback))
            throw new IOException(
                $"No writable data directory. Tried '{preferred}' and '{fallback}'.");

        Console.Error.WriteLine(
            $"Application directory '{Path.GetDirectoryName(preferred)}' is not writable; using '{fallback}' for data.");
        return new AppData(fallback, isFallback: true);
    }

    /// <summary>
    /// Copies plugin data from the per-user directory into <see cref="Root"/> when the new
    /// location does not already contain it. Existing files always win, so the operation is
    /// idempotent and never overwrites newer data.
    /// </summary>
    public void MigrateLegacyData()
    {
        if (PathEquals(LegacyRoot, Root))
            return;

        string source = Path.Combine(LegacyRoot, "plugins");
        if (!Directory.Exists(source))
            return;

        if (CopyMissingFiles(source, PluginsDirectory))
            Console.WriteLine($"Copied existing data from '{LegacyRoot}' to '{Root}'.");
    }

    /// <summary>Reads the workspace selection written by earlier versions, if any.</summary>
    public string? ReadLegacyWorkspaceSelection()
    {
        string path = Path.Combine(LegacyRoot, "selected-workspace.txt");
        try
        {
            if (!File.Exists(path))
                return null;

            string value = File.ReadAllText(path, Encoding.UTF8).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Unable to read the legacy workspace selection: {exception.Message}");
            return null;
        }
    }

    private static bool TryPrepare(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            string probe = Path.Combine(path, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool CopyMissingFiles(string source, string destination)
    {
        bool copied = false;
        try
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.EnumerateFiles(source))
            {
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (File.Exists(target))
                    continue;
                try
                {
                    File.Copy(file, target, overwrite: false);
                    copied = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"Unable to copy '{file}' to '{target}': {exception.Message}");
                }
            }

            foreach (string directory in Directory.EnumerateDirectories(source))
                copied |= CopyMissingFiles(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Unable to copy legacy data from '{source}': {exception.Message}");
        }
        return copied;
    }

    private static bool PathEquals(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }

    private static string ResolveLegacyRoot()
    {
        string userData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(userData))
            userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(userData, ApplicationFolderName);
    }
}
