using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using PokeSeats.UI.PluginContracts;
namespace PokeSeats.UI.DefaultPayload;

internal sealed class RosterPanel : IUiView
{
    private readonly StudentRoster _roster;
    private readonly string _storagePath;
    private string _filePath = string.Empty;
    private SpreadsheetPreview? _preview;
    private int _nameColumn;
    private int _studentNumberColumn;
    private string _status = "请选择 .xls 或 .xlsx 文件。";

    public RosterPanel(StudentRoster roster, string storageDirectory)
    {
        _roster = roster;
        _storagePath = Path.Combine(storageDirectory, "students.json");
    }

    public string Id => "pokeseats.defaultpayload.roster";

    public string DisplayName => "学生名单";

    public Vector2? MinimumSize => new(520, 360);

    public void DrawContent(float deltaSeconds)
    {
        ImGui.Text("从 Excel 导入学生名单");
        ImGui.TextDisabled("首个工作表的第一行会作为列名。导入后名单保存到 PokeSeats 数据目录。");

        ImGui.SetNextItemWidth(UiScale.Pixels(-110));
        ImGui.InputText("##roster-file-path", ref _filePath, 2048);
        ImGui.SameLine();
        if (ImGui.Button("浏览..."))
        {
            string? selected = WindowsFilePicker.SelectExcelFile();
            if (selected is not null)
            {
                _filePath = selected;
                TryPreview();
            }
        }

        if (ImGui.Button("读取表头"))
            TryPreview();

        if (_preview is not null)
        {
            ImGui.Separator();
            ImGui.Text($"文件：{Path.GetFileName(_preview.FilePath)}");
            ImGui.Text("请选择姓名列和学号列（学号可选）：");
            DrawColumnSelector("姓名列", ref _nameColumn, false);
            DrawColumnSelector("学号列", ref _studentNumberColumn, true);

            if (ImGui.Button("导入名单"))
                TryImport();

            ImGui.SameLine();
            ImGui.TextDisabled($"预览 {_preview.Rows.Count} 行");
        }

        ImGui.Separator();
        ImGui.Text($"当前学生：{_roster.Students.Count} 人");
        ImGui.TextDisabled(_status);
        DrawRosterTable();
    }

    private void DrawColumnSelector(string label, ref int selectedColumn, bool allowNone)
    {
        SpreadsheetPreview previewData = _preview!;
        string preview = selectedColumn >= 0 && selectedColumn < previewData.Headers.Count
            ? previewData.Headers[selectedColumn]
            : allowNone ? "不导入" : "请选择";
        if (ImGui.BeginCombo(label, preview))
        {
            if (allowNone && ImGui.Selectable($"不导入##{label}-none", selectedColumn < 0))
                selectedColumn = -1;
            for (int index = 0; index < previewData.Headers.Count; index++)
            {
                bool selected = selectedColumn == index;
                if (ImGui.Selectable($"{previewData.Headers[index]}##{label}{index}", selected))
                    selectedColumn = index;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
    }


    private void DrawRosterTable()
    {
        if (_roster.Students.Count == 0 || !ImGui.BeginTable("##student-roster", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, UiScale.Pixels(180))))
            return;

        ImGui.TableSetupColumn("姓名");
        ImGui.TableSetupColumn("学号");
        ImGui.TableHeadersRow();
        foreach (var student in _roster.Students)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(student.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(student.StudentNumber ?? "");
        }
        ImGui.EndTable();
    }

    private void TryPreview()
    {
        try
        {
            _preview = ExcelRosterImporter.Preview(_filePath.Trim());
            _nameColumn = FindHeader(_preview.Headers, "姓名", 0);
            _studentNumberColumn = FindHeader(_preview.Headers, "学号", -1);
            _status = "已读取表头，请确认列映射后导入。";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _preview = null;
            _status = $"读取失败：{exception.Message}";
        }
    }

    private void TryImport()
    {
        try
        {
            ImportResult result = ExcelRosterImporter.Import(_preview!, _nameColumn, _studentNumberColumn);
            _roster.Save(_storagePath, result.Students);
            _roster.Replace(result.Students);
            _status = $"导入完成：{result.Students.Count} 人，跳过 {result.SkippedRows} 行。";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _status = $"导入失败：{exception.Message}";
        }
    }

    private static int FindHeader(IReadOnlyList<string> headers, string term, int fallback)
    {
        for (int index = 0; index < headers.Count; index++)
            if (headers[index].Contains(term, StringComparison.OrdinalIgnoreCase))
                return index;
        return fallback;
    }
}

internal static class WindowsFilePicker
{
    public static string? SelectExcelFile()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var dialog = new OpenFileName
        {
            Filter = "Excel 文件 (*.xlsx;*.xls)\0*.xlsx;*.xls\0所有文件 (*.*)\0*.*\0",
            File = new string('\0', 4096),
            MaxFile = 4096,
            Title = "选择学生名单 Excel 文件",
            Size = Marshal.SizeOf<OpenFileName>(),
            FilterIndex = 1,
            Flags = 0x00080000 | 0x00001000 | 0x00000800
        };
        return GetOpenFileName(ref dialog) ? dialog.File.TrimEnd('\0') : null;
    }

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
