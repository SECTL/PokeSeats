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
    private string _status = "请选择 Excel、CSV 或 SecRandom JSON 名单文件。";

    public RosterPanel(StudentRoster roster, string storageDirectory)
    {
        _roster = roster;
        _storagePath = Path.Combine(storageDirectory, "students.json");
    }

    public string Id => "pokeseats.defaultpayload.roster";

    public string DisplayName => "学生名单";

    public Vector2? MinimumSize => new(520, 360);

    public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
    {
        ImGui.Text("导入学生花名册（Excel / CSV / SecRandom JSON）");
        ImGui.TextDisabled("表格使用首行列标题；SecRandom JSON 自动对应姓名、学号和小组。");

        ImGui.SetNextItemWidth(UiScale.Pixels(-110));
        ImGui.InputText("##roster-file-path", ref _filePath, 2048);
        ImGui.SameLine();
        if (ImGui.Button("浏览文件..."))
        {
            string? selected = WindowsFilePicker.SelectRosterFile();
            if (selected is not null)
            {
                _filePath = selected;
                TryPreview();
            }
        }

        if (ImGui.Button("读取名单"))
            TryPreview();

        if (_preview is not null)
        {
            ImGui.Separator();
            ImGui.Text($"已选文件：{Path.GetFileName(_preview.FilePath)}");
            ImGui.Text("请指定姓名、学号和小组对应的列（学号和小组非必填）：");
            DrawColumnSelector("姓名列", ref _nameColumn, false);
            DrawColumnSelector("学号列（可选）", ref _studentNumberColumn, true);
            DrawColumnSelector("小组列（可选）", ref _groupColumn, true);

            if (ImGui.Button("确认导入名单"))
                TryImport();

            ImGui.SameLine();
            ImGui.TextDisabled($"共读取 {_preview.Rows.Count} 条记录");
        }
        ImGui.TextDisabled("确认导入会替换当前名单并保存；姓名为空的记录会跳过。");

        ImGui.Separator();
        ImGui.Text($"当前班级人数：{_roster.Students.Count} 人");
        ImGui.SameLine();
        ImGui.BeginDisabled(_roster.Students.Count == 0);
        if (ImGui.Button("清空名单"))
            ImGui.OpenPopup("确认清空名单###roster-clear-confirm");
        ImGui.EndDisabled();
        DrawClearConfirmation();
        ImGui.TextDisabled(_status);
        DrawRosterTable();
    }

    private void DrawClearConfirmation()
    {
        if (!ImGui.BeginPopupModal("确认清空名单###roster-clear-confirm", ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.Text($"确认清空当前名单中的 {_roster.Students.Count} 名学生？");
        ImGui.Text("清空后将保存为空名单，可重新导入名单文件。");
        ImGui.Text("已有排座规则会保留，涉及原名单学生的规则需重新设置。");
        if (ImGui.Button("确认清空") && TryClearRoster())
            ImGui.CloseCurrentPopup();
        ImGui.SameLine();
        if (ImGui.Button("取消") || ImGui.IsKeyPressed(ImGuiKey.Escape))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private bool TryClearRoster()
    {
        try
        {
            var empty = Array.Empty<Person>();
            _roster.Save(_storagePath, empty);
            _roster.Replace(empty);
            _status = "学生名单已清空并保存，可重新导入名单。";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status = $"清空失败，当前名单未更改：{exception.Message}";
            return false;
        }
    }


    private void DrawColumnSelector(string label, ref int selectedColumn, bool allowNone)
    {
        SpreadsheetPreview previewData = _preview!;
        string preview = selectedColumn >= 0 && selectedColumn < previewData.Headers.Count
            ? previewData.Headers[selectedColumn]
            : allowNone ? "（不导入此项）" : "（请选择匹配列）";
        if (ImGui.BeginCombo(label, preview))
        {
            if (allowNone && ImGui.Selectable($"（不导入此项）##{label}-none", selectedColumn < 0))
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
        if (_roster.Students.Count == 0 || !ImGui.BeginTable("##student-roster", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY, new Vector2(0, UiScale.Pixels(180))))
            return;

        ImGui.TableSetupColumn("学生姓名");
        ImGui.TableSetupColumn("学号");
        ImGui.TableSetupColumn("小组");
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
            _status = "名单读取成功，请核对姓名、学号和小组列后点击导入。";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            _preview = null;
            _status = $"名单读取失败：{exception.Message}";
        }
    }

    private void TryImport()
    {
        try
        {
            ImportResult result = RosterImporter.Import(_preview!, _nameColumn, _studentNumberColumn, _groupColumn);
            _roster.Save(_storagePath, result.Students);
            _roster.Replace(result.Students);
            _status = $"导入成功：已录入 {result.Students.Count} 名学生（跳过空白或无效行 {result.SkippedRows} 行）。";
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _status = $"导入失败：{exception.Message}";
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

        var dialog = new OpenFileName
        {
            Filter = "名单文件 (*.xlsx;*.xls;*.csv;*.json)\0*.xlsx;*.xls;*.csv;*.json\0Excel 文件 (*.xlsx;*.xls)\0*.xlsx;*.xls\0CSV 文件 (*.csv)\0*.csv\0SecRandom JSON (*.json)\0*.json\0所有文件 (*.*)\0*.*\0",
            File = new string('\0', 4096),
            MaxFile = 4096,
            Title = "选择学生名单文件",
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
