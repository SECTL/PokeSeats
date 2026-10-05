using System.Text;

namespace PokeSeats.UI;

internal sealed class WorkspaceSelectionStore
{
    private readonly string _path;

    public WorkspaceSelectionStore()
    {
        string dataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(dataRoot))
            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        string root = Path.Combine(dataRoot, "PokeSeats");
        _path = Path.Combine(root, "selected-workspace.txt");
    }

    public string? Read()
    {
        try
        {
            if (!File.Exists(_path))
                return null;

            string value = File.ReadAllText(_path, Encoding.UTF8).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Unable to read workspace selection: {exception.Message}");
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Unable to read workspace selection: {exception.Message}");
            return null;
        }
    }

    public void Write(string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
            throw new ArgumentException("A workspace ID is required.", nameof(workspaceId));

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporaryPath = _path + ".tmp";
        File.WriteAllText(temporaryPath, workspaceId + Environment.NewLine, Encoding.UTF8);
        File.Move(temporaryPath, _path, true);
    }
}
