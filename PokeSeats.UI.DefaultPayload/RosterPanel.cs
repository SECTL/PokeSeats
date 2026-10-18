using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ImGuiNET;
using PokeSeats.UI.PluginContracts;
using PokeSeats.IR.Base;
namespace PokeSeats.UI.DefaultPayload;

internal sealed class RosterPanel : IUiView
{
    private readonly StudentRoster _roster;
    private readonly string _storagePath;
    private string _filePath = string.Empty;
    private SpreadsheetPreview? _preview;
    private int _nameColumn;
    private int _studentNumberColumn;
    private int _groupColumn = -1;
    private string _status = PayloadStrings.RosterInitialStatus;

    public RosterPanel(StudentRoster roster, string storageDirectory)
    {
        _roster = roster;
        _storagePath = Path.Combine(storageDirectory, "students.json");
    }

    public string Id => "pokeseats.defaultpayload.roster";

    public string DisplayName => PayloadStrings.RosterViewTitle;

    public Vector2? MinimumSize => new(520, 360);

    public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
    {
        ImGui.Text(PayloadStrings.RosterImportHeading);
        ImGui.TextDisabled(PayloadStrings.RosterImportHint);

        ImGui.SetNextItemWidth(UiScale.Pixels(-110));
        ImGui.InputText("##roster-file-path", ref _filePath, 2048);
        ImGui.SameLine();
        if (ImGui.Button(PayloadStrings.RosterBrowseButton))
        {
            string? selected = WindowsFilePicker.SelectRosterFile();
            if (selected is not null)
            {
                _filePath = selected;
                TryPreview();
            }
        }

        if (ImGui.Button(PayloadStrings.RosterReadButton))
            TryPreview();

        if (_preview is not null)
        {
            ImGui.Separator();
            ImGui.Text(string.Format(PayloadStrings.RosterSelectedFileFormat, Path.GetFileName(_preview.FilePath)));
            ImGui.BeginDisabled(_preview.NativeRoster is not null);
            ImGui.Text(PayloadStrings.RosterColumnPrompt);
            DrawColumnSelector("roster-name-column", PayloadStrings.RosterNameColumnLabel, ref _nameColumn, false);
            DrawColumnSelector("roster-student-number-column", PayloadStrings.RosterStudentNumberColumnLabel, ref _studentNumberColumn, true);
            DrawColumnSelector("roster-group-column", PayloadStrings.RosterGroupColumnLabel, ref _groupColumn, true);
            ImGui.EndDisabled();

            if (ImGui.Button(PayloadStrings.RosterImportConfirmButton))
                TryImport();

            ImGui.SameLine();
            ImGui.TextDisabled(string.Format(PayloadStrings.RosterRecordCountFormat, _preview.Rows.Count));
        }
        ImGui.TextDisabled(PayloadStrings.RosterImportNote);

        ImGui.Separator();
        ImGui.Text(string.Format(PayloadStrings.RosterCurrentCountFormat, _roster.Students.Count));
        ImGui.SameLine();
        ImGui.BeginDisabled(_roster.Students.Count == 0);
        if (ImGui.Button(PayloadStrings.RosterClearButton))
            ImGui.OpenPopup(PayloadStrings.RosterClearConfirmTitle + "###roster-clear-confirm");
        ImGui.EndDisabled();
        DrawClearConfirmation();
        ImGui.TextDisabled(_status);
        DrawRosterTable();
    }

    private void DrawClearConfirmation()
    {
        if (!ImGui.BeginPopupModal(PayloadStrings.RosterClearConfirmTitle + "###roster-clear-confirm", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.Text(string.Format(PayloadStrings.RosterClearConfirmQuestionFormat, _roster.Students.Count));
        ImGui.Text(PayloadStrings.RosterClearConfirmBody);
        ImGui.Text(PayloadStrings.RosterClearConfirmRulesNote);
        if (ImGui.Button(PayloadStrings.RosterClearConfirmButton) && TryClearRoster())
            ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if (ImGui.Button(PayloadStrings.CommonCancel) || ImGui.IsKeyPressed(ImGuiKey.Escape))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private bool TryClearRoster()
    {
        try
        {
            var empty = RosterSnapshot.Empty;
            _roster.Save(_storagePath, empty);
            _roster.Replace(empty);
            _status = PayloadStrings.RosterClearedStatus;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status = string.Format(PayloadStrings.RosterClearFailedFormat, exception.Message);
            return false;
        }
    }


    private void DrawColumnSelector(string id, string label, ref int selectedColumn, bool allowNone)
    {
        SpreadsheetPreview previewData = _preview!;
        string preview = selectedColumn >= 0 && selectedColumn < previewData.Headers.Count
            ? previewData.Headers[selectedColumn]
            : allowNone ? PayloadStrings.RosterColumnNoneOption : PayloadStrings.RosterColumnUnsetOption;
        if (ImGui.BeginCombo($"{label}##{id}", preview))
        {
            if (allowNone && ImGui.Selectable($"{PayloadStrings.RosterColumnNoneOption}##{id}-none", selectedColumn < 0))
                selectedColumn = -1;
            for (int index = 0; index < previewData.Headers.Count; index++)
            {
                bool selected = selectedColumn == index;
                if (ImGui.Selectable($"{previewData.Headers[index]}##{id}{index}", selected))
                    selectedColumn = index;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }


    private void DrawRosterTable()
    {
        if (_roster.Students.Count == 0 || !ImGui.BeginTable("##student-roster", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, UiScale.Pixels(180))))
            return;

        ImGui.TableSetupColumn(PayloadStrings.RosterNameHeader + "##student-name");
        ImGui.TableSetupColumn(PayloadStrings.RosterStudentNumberHeader + "##student-number");
        ImGui.TableSetupColumn(PayloadStrings.RosterGroupHeader + "##student-group");
        ImGui.TableHeadersRow();
        foreach (var student in _roster.Students)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(student.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(student.StudentNumber ?? "");
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(student.Group ?? "");
        }
        ImGui.EndTable();
    }

    private void TryPreview()
    {
        try
        {
            _preview = RosterImporter.Preview(_filePath.Trim());
            _nameColumn = FindHeader(_preview.Headers, 0, "姓名", "名称", "学生姓名", "名字", "Name", "Student Name", "Full Name");
            _studentNumberColumn = FindHeader(_preview.Headers, -1, "学号", "序号/编号", "编号", "学籍号", "Sequence / ID", "Student ID", "Student Number", "ID");
            _groupColumn = FindHeader(_preview.Headers, -1, "小组", "分组", "组别", "Group", "Team");
            _status = PayloadStrings.RosterPreviewSuccessStatus;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            _preview = null;
            _status = string.Format(PayloadStrings.RosterPreviewFailedFormat, exception.Message);
        }
    }

    private void TryImport()
    {
        try
        {
            ImportResult result = RosterImporter.Import(_preview!, _nameColumn, _studentNumberColumn, _groupColumn);
            _roster.Save(_storagePath, result.Roster);
            _roster.Replace(result.Roster);
            _status = string.Format(PayloadStrings.RosterImportSuccessFormat, result.Roster.Count, result.SkippedRows);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _status = string.Format(PayloadStrings.RosterImportFailedFormat, exception.Message);
        }
    }

    private static int FindHeader(IReadOnlyList<string> headers, int fallback, params string[] terms)
    {
        foreach (string term in terms)
            for (int index = 0; index < headers.Count; index++)
                if (string.Equals(headers[index], term, StringComparison.OrdinalIgnoreCase))
                    return index;
        foreach (string term in terms)
            for (int index = 0; index < headers.Count; index++)
                if (headers[index].Contains(term, StringComparison.OrdinalIgnoreCase))
                    return index;
        return fallback;
    }
}

internal static class WindowsFilePicker
{
    public static string? SelectRosterFile()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        string filter = string.Join(
            "\0",
            PayloadStrings.FilePickerRosterFilter, "*.xlsx;*.xls;*.csv;*.json",
            PayloadStrings.FilePickerExcelFilter, "*.xlsx;*.xls",
            PayloadStrings.FilePickerCsvFilter, "*.csv",
            PayloadStrings.FilePickerSecRandomJsonFilter, "*.json",
            PayloadStrings.FilePickerAllFilesFilter, "*.*") + "\0";

        var dialog = new OpenFileName
        {
            Filter = filter,
            File = new string('\0', 4096),
            MaxFile = 4096,
            Title = PayloadStrings.FilePickerSelectRosterTitle,
            Size = Marshal.SizeOf<OpenFileName>(),
            FilterIndex = 1,
            Flags = 0x00080000 | 0x00001000 | 0x00000800
        };
        return GetOpenFileName(ref dialog) ? dialog.File.TrimEnd('\0') : null;
    }

    public static string? SelectPngFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var dialog = new OpenFileName
        {
            Size = Marshal.SizeOf<OpenFileName>(),
            Filter = ExportStrings.PngFilter + "\0*.png\0\0",
            File = Path.GetFileName(path).PadRight(4096, '\0'),
            MaxFile = 4096,
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(path)) ? Path.GetDirectoryName(path)! : AppContext.BaseDirectory,
            Title = ExportStrings.Title,
            DefExt = "png",
            FilterIndex = 1,
            Flags = 0x00080000 | 0x00000800 | 0x00000002
        };
        return GetSaveFileName(ref dialog) ? dialog.File.TrimEnd('\0') : null;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileName(ref OpenFileName dialog);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OpenFileName dialog);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int Size;
        public IntPtr Owner;
        public IntPtr Instance;
        [MarshalAs(UnmanagedType.LPWStr)] public string Filter;
        [MarshalAs(UnmanagedType.LPWStr)] public string CustomFilter;
        public int MaxCustFilter;
        public int FilterIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public string File;
        public int MaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string FileTitle;
        public int MaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string InitialDirectory;
        [MarshalAs(UnmanagedType.LPWStr)] public string Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string DefExt;
        public IntPtr CustData;
        public IntPtr Hook;
        [MarshalAs(UnmanagedType.LPWStr)] public string TemplateName;
        public IntPtr ReservedPtr;
        public int ReservedInt;
        public int FlagsEx;
    }
}
