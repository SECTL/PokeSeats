using System.Numerics;
using ImGuiNET;
using PokeSeats.Core.Solver;
using PokeSeats.UI.PluginContracts;

namespace PokeSeats.UI.DefaultPayload;

internal sealed class SeatingArchiveExportDialog
{
    private const string PopupId = "###seating-archive-export";
    private string path;
    private string? status;
    private bool openRequested;

    public SeatingArchiveExportDialog(string dataDirectory)
        => path = Path.Combine(dataDirectory, "exports", "seating.zip");

    public void Open()
    {
        status = null;
        openRequested = true;
    }

    public void ResetResult() => status = null;

    public void Draw(SolverRequest request, SolverResult result)
    {
        string popup = ExportStrings.ArchiveTitle + PopupId;
        if (openRequested)
        {
            ImGui.OpenPopup(popup);
            openRequested = false;
        }
        ImGui.SetNextWindowSize(new Vector2(Math.Min(UiScale.Pixels(620), ImGui.GetMainViewport().Size.X * .94f), 0), ImGuiCond.Appearing);
        bool open = true;
        if (!ImGui.BeginPopupModal(popup, ref open, ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings))
            return;

        ImGui.TextWrapped(ExportStrings.ArchiveHint);
        ImGui.Separator();
        ImGui.TextUnformatted(ExportStrings.ArchivePath);
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##archive-path", ref path, 4096);
        if (OperatingSystem.IsWindows())
        {
            if (ImGui.Button(ExportStrings.Browse))
            {
                string? chosen = WindowsFilePicker.SelectZipFile(path);
                if (chosen is not null) path = chosen;
            }
            ImGui.SameLine();
        }
        if (ImGui.Button(ExportStrings.ArchiveSave))
        {
            try
            {
                path = NormalizePath(path);
                if (File.Exists(path)) ImGui.OpenPopup(ExportStrings.ArchiveOverwriteTitle);
                else Save(request, result);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
            { status = string.Format(ExportStrings.ArchiveFailure, exception.Message); }
        }
        ImGui.SameLine();
        if (ImGui.Button(ExportStrings.Close)) ImGui.CloseCurrentPopup();
        if (status is not null) ImGui.TextWrapped(status);
        if (ImGui.BeginPopupModal(ExportStrings.ArchiveOverwriteTitle, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted(ExportStrings.OverwritePrompt);
            ImGui.TextUnformatted(path);
            if (ImGui.Button(ExportStrings.Overwrite))
            {
                Save(request, result);
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button(ExportStrings.Cancel)) ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
        }
        ImGui.EndPopup();
    }

    private static string NormalizePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(ExportStrings.ArchivePathRequired);
        string full = Path.GetFullPath(value.Trim());
        if (Path.GetExtension(full).Length == 0) full += ".zip";
        if (!string.Equals(Path.GetExtension(full), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(ExportStrings.ZipOnly);
        return full;
    }

    private void Save(SolverRequest request, SolverResult result)
    {
        try
        {
            SeatingArchiveExporter.SaveZip(path, request, result);
            status = string.Format(ExportStrings.ArchiveSuccess, path);
        }
        catch (Exception exception)
        { status = string.Format(ExportStrings.ArchiveFailure, exception.Message); }
    }
}
