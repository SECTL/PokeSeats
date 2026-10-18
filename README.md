# PokeSeats 戳座

PokeSeats 是一个给老师排座位的桌面小程序。你把花名册、教室布局和几条座位规则交给它，它用约束求解算出一份满足全部规则的座位表。

排座位这件事本身不难，难的是规则一多就互相打架：既要照顾视力差的学生坐前排，又要把两个爱说话的分开，还得保证每组人数均衡。手工排完往往顾此失彼。PokeSeats 把这堆要求写成明确的约束，交给 OR-Tools CP-SAT 求解并优化；如果证明无解，再调用 Z3 提取 UNSAT 核心，告诉你哪几条规则凑在一起导致了冲突。

## 它现在能做什么

- 从 Excel、CSV 或 SecRandom JSON 导入姓名、学号和小组，按姓名、学号、全拼或拼音首字母搜索学生。
- 用网格画出教室：学生座位、讲台、主走道（宽过道）、行间过道（窄过道），行列数 1 到 32 可调。
- 编写四类规则：固定某人坐某位、限定某人只能坐某个区域、指定两名学生的相对位置、限制某排或某列的人数。
- 一键求解，在满足全部规则的前提下优先让空座靠后，再让空座左右均匀分散，并按教室布局显示姓名和行列位置。
- 无解时标红导致冲突的那几条规则。

求解先满足全部硬规则，再按排座目标的优先级从小到大分层优化；同一优先级的代价相加，后续层不能牺牲前层的最优值。完成自定义目标后，再依次让空座靠后、左右均匀分散。没有自定义目标时保留原来的空座优化行为；没有空座且没有目标时只求可行解。

界面默认使用 OR-Tools CP-SAT；目标包含居中或两侧形状时，整次求解自动改用 Z3。CP-SAT 判定无解后仍会调用 Z3 定位冲突规则；预检查拒绝或求解异常不触发冲突诊断。Core 同时支持两套后端。优化不保证多次排座得到不同结果。

## 运行要求

- Windows 7 SP1 及以上，64 位
- [.NET 6 运行时](https://dotnet.microsoft.com/download/dotnet/6.0)

Windows 使用 Direct3D 11（DX11）渲染；Linux 保持 OpenGL 后端，完整运行情况仍需在 Linux 环境验证。Android 在计划中，目前还不能用。

## 上手流程

从 [Github Releases](https://github.com/SECTL/PokeSeats/releases/latest) 或 [思拓创联 STK](https://stk.sectl.cn/PokeSeats/) 中选择最新版本下载或自行发布后运行 `PokeSeats.UI.exe`，按下面的顺序操作：

默认工作区左侧上下排列「教室布局」和「学生名单」，右侧由「规则编辑」占满整列；左侧占可用列宽的 40%，右侧占 60%。面板随主窗口大小调整，关闭其中一个后其余面板自动补位，可从「功能面板」菜单重新打开。面板内容超出可用空间时可滚动查看。「排座结果」保持独立悬浮，可以拖动、缩放和关闭。

1. **学生名单** —— 点「浏览文件」选择 Excel、CSV、PokeSeats 原生 JSON 或 SecRandom JSON。表格和 SecRandom 名单可核对姓名、学号和小组列（后两项可不填）；原生 JSON 直接保留学生 ID 和自定义属性，不重新映射列。确认导入后自动保存，下次打开自动载入。
2. **教室布局** —— 按住左键拖出一个矩形，松开后选择这片区域是什么（课桌、讲台、宽过道、窄过道、空白）；右键可以单独改一格。教室前方朝上。
3. **规则编辑** —— 先在左上角选规则类型，再挑学生、填条件。右侧列出已保存的规则，点一下就能改；也可以删除选中规则，或通过“清空所有规则”一次移除全部规则。
4. **开始自动排座** —— 菜单栏里的命令，会打开"排座结果"面板。求解在后台进行，期间可以继续操作。
5. **导出座位表图片** —— 在排座结果面板点击「导出图片」，可以选择讲台在上或在下；勾选姓名、学号、小组、座位坐标以及行号和列号，调整图片背景、文字、边框和各类格子颜色后保存为 PNG。预览中点击任意格子可以单独设置文字和底色，适合标注值日生、特殊座位等信息。
6. **导出排座数据 ZIP** —— 成功排座后，在结果面板点击「导出 ZIP」，选择保存位置后点击「保存 ZIP」。压缩包内只有 `students.json`、`rules.json` 和 `seating.json` 三个 UTF-8 JSON 文件；已有文件会先询问是否覆盖。未填写扩展名时自动补上 `.zip`。

改完名单、布局或规则之后需要重新排一次；结果面板会提示你当前显示的是旧结果。

导出图片使用随程序打包的 HarmonyOS Sans SC 字体，支持中文和多行内容。讲台在下时只旋转座位表位置 180°，格子里的文字保持正向，行列编号仍对应原教室座位；导出的 PNG 包含完整网格，不受当前窗口大小影响。

ZIP 中的三个文件均来自结果对应的排座快照；之后修改名单、属性定义、布局或规则时，导出旧结果仍会保留当时的数据，需重新排座才能导出新数据。`students.json` 沿用下述版本化本地名单格式，包含属性定义、学生基本信息和属性值；`rules.json` 沿用本地规则格式，保留四类规则的类型、标识和全部参数。

`seating.json` 包含 `Version: 1`、`Rows`、`Cols`、二维数组 `Cells` 与 `Assignments`。`Cells` 按行保存完整布局，格子类型为 `Desk`、`Aisle`、`NarrowAisle`、`Podium` 或 `Empty`；每条分配记录用 `PersonId` 引用名单中的 `Id`，`Row` 和 `Col` 从 **0** 开始，与规则坐标一致。没有分配学生的 `Desk` 即为空座。界面上的排、列编号仍从 1 开始。

规则编辑中的教室预览使用紧凑格子：格子宽高为原铺满尺寸的 37.5%，格内标记会按需缩小，点击选座和区域框选仍按实际格子位置定位。

### 名单格式与 SecRandom 兼容

| 文件 | 读取方式 |
| --- | --- |
| Excel `.xlsx` / `.xls` | 读取第一张工作表，首行为表头；可选择姓名、学号和小组列。 |
| CSV `.csv` | UTF-8（可带 BOM），逗号分隔，首行为表头；支持双引号包裹、引号转义和字段内换行。 |
| PokeSeats 原生名单 `.json` | 读取版本化名单对象或旧版名单数组，保留稳定学生 ID；新格式同时保留属性定义与属性值，不重新映射列。 |
| SecRandom 原生名单 `.json` | 直接选择 SecRandom 数据目录 `data/config/list/roll_call_list/` 下的名单文件，读取 `students` 数组中的 `name`、`id`、`group`。 |
| SecRandom 传输文档 `.secrandom-roster.json` | 接受 `version: 1`、`kind: 0` 的学生名单，读取 `rows` 数组中的 `name`、`id`、`detailTwo`。这套 JSON 用于 SecRandom 名单传输，当前上游的本地导出菜单只提供 Excel 和 CSV。 |

表格会自动识别「姓名 / 名称 / Name」「学号 / 序号/编号 / Sequence / ID」「小组 / 分组 / Group」等列头，导入前仍可手动调整。学号作为文本保存，CSV 和 JSON 中的前导零会保留；Excel 中需要保留前导零的学号应使用文本单元格。

确认导入会整体替换当前名单及属性定义，不会合并。表格和 SecRandom 名单中姓名为空的记录会跳过，重复的非空学号会使整次导入失败，原名单保持不变；它们目前只导入姓名、学号和小组，不自动识别身高、成绩等扩展列。SecRandom 的性别、标签以及抽取状态不导入；`exists: false` 的有姓名学生仍会导入排座。小组会显示在名单表格中并随本地缓存保存，目前不自动生成小组排座规则。表格和 SecRandom 导入会创建新的学生 ID，原有规则中的学生关联需要重新设置；原生 JSON 保留 ID，同 ID 的规则关联可继续使用。

格式依据为 [SecRandom v3 学生导出映射](https://github.com/SECTL/SecRandom/blob/f1b647c3e093c13d37d687083148bdfb6066d2d4/SecRandom/Views/SettingsPages/ListManagement/RollCallListExportView.cs#L12-L21)、[传输文档定义](https://github.com/SECTL/SecRandom/blob/f1b647c3e093c13d37d687083148bdfb6066d2d4/SecRandom/Services/RosterTransfer/RosterTransferService.cs#L352-L370) 和 [原生学生模型](https://github.com/SECTL/SecRandom/blob/f1b647c3e093c13d37d687083148bdfb6066d2d4/SecRandom.Shared/Models/Profile/Student.cs)。

### 自定义学生属性数据模型

IR 层的 `RosterSnapshot` 是不可变的完整名单快照，包含 `AttributeDefinitions` 和 `Students`；`SolverRequest.Roster`、本地保存与 ZIP 导出共用这份模型。`Person.Attributes` 以 `PersonAttributeId` 为键，属性改名不改变关联。构造时复制输入集合，后续改动原列表、字典或分类选项不会改写已经捕获的排座数据。

| 属性类型 | 值与定义 |
| --- | --- |
| `Number` | `decimal` 数值，可定义单位 `Unit` 与含端点的 `Minimum` / `Maximum`，适合身高、总分或单科成绩。 |
| `Category` | 字符串，必须属于定义中的非空、无重复 `Categories` 选项，按大小写精确匹配。 |
| `Boolean` | `true` / `false`，适合是否需要前排等标记。 |

学生未填写某属性时，省略 `Attributes` 中对应的键；不会自动补成 `0`、`false` 或某个分类，也不使用显式 `null` 表示缺失。名单快照会拒绝重复或空的学生/属性 ID、未定义的属性引用、类型不匹配、越界数值和无效分类。数值定义可不设上下界；分类和布尔定义不能设置数值单位与上下界。

新保存的 `students.json` 是 `Version: 1` 对象（旧版是无版本的数组），例如：

```json
{
  "Version": 1,
  "AttributeDefinitions": [
    {
      "Id": "11111111-1111-1111-1111-111111111111",
      "Name": "身高",
      "Kind": "Number",
      "Unit": "cm",
      "Minimum": 50,
      "Maximum": 250,
      "Categories": []
    }
  ],
  "Students": [
    {
      "Id": "22222222-2222-2222-2222-222222222222",
      "Name": "示例学生",
      "StudentNumber": "001",
      "Group": "一组",
      "Attributes": {
        "11111111-1111-1111-1111-111111111111": 168.5
      }
    }
  ]
}
```

旧版数组仍可读取，没有属性定义和值；已有非空学生 ID 原样保留，旧数据缺失的 ID 会生成新 ID。保存时统一写入新格式，不在加载时改写原文件。名单保存先写临时文件再替换原文件；无效文档加载失败时不替换当前内存快照。

数值属性可以在「规则编辑」的「排座目标」标签页中用于求解。属性定义和值目前通过原生 JSON 提供，尚未提供属性编辑界面或表格属性列映射。

### 排座目标

在「规则编辑」的「排座目标」标签页中新建目标，填写名称、选择数值属性、座位维度、映射形状及优先级后保存。支持编辑、删除及上下移动，保存在插件数据目录的 `objectives.json` 中。优先级从 `1` 开始，数字越小越优先；上下移动只改变列表顺序，同优先级目标仍合并优化。文件缺少 `Priority` 时按 `1` 读取，显式小于 `1` 的值会被拒绝；损坏文件加载失败时保留原文件并显示提示。

形状包括「值越小越靠前」「值越大越靠前」「值越大越居中」「值越大越靠两边」「各排均衡」及「区域偏好」。前两种可按行、列或距中心列距离优化；居中、两侧仅支持距中心列距离。缺失属性值的学生仍参与排座，其该项目标代价为零。

普通排序目标可以在教室预览中按住左键拖拽框选矩形，再次拖拽替换区域，点击「清除区域」恢复全教室排序。限定区域时，区域内按原维度排序，区域外座位同权且排在区域内之后；只有实际课桌参与评分，讲台、走道及空白不产生座位。

「区域偏好」（`RegionPreference`）必须选择区域，可切换「高值优先」或「低值优先」；优先把对应数值的学生安排在区域内，区域外不再排序。区域使用包含端点的 0-based 行列边界保存，不随教室尺寸变化裁剪；区域没有覆盖课桌时该目标不影响分配。

「各排均衡」（`RowBalance`）固定使用行维度，不允许限定区域。按各实际课桌行的容量比例平衡已知属性的归一化总量，最小化各行与容量比例目标的绝对偏差；已知数值全部相同时改为平衡已知人数。缺失属性不计入行和，名单中的小组不参与该目标。旧文件的 `GroupBalance` 在读取时迁移为 `RowBalance`，保留目标 ID、名称、属性和优先级，并重置为行维度、无区域；再次保存只写入新形状名。

`objectives.json` 可保存 `Region`（`RowMin`、`RowMax`、`ColMin`、`ColMax`）和区域偏好的 `RegionPreferenceDirection`（`HighValues` / `LowValues`，缺少时默认前者）。负数或颠倒边界、缺少区域的区域偏好、各排均衡使用非行维度或限定区域、未知形状等无效数据均会被拒绝；加载或保存校验失败不覆盖原文件。

修改目标会使已有结果标为过期；运行中的求解及旧结果继续保留启动时的目标快照。

## 四类规则

| 类型 | 说明 |
| --- | --- |
| 固定特定座位 | 某位学生必须坐在指定的那张课桌上 |
| 限定就座区域 | 某位学生只能坐在（或不能坐在）框选出的区域内 |
| 学生相对位置 | 指定两名学生之间的方位关系，见下 |
| 单排行列人数 | 限制某一排或某一列上坐多少人，可以只设下限、只设上限或设成固定值 |

相对位置支持这些关系：指定相对偏移（比如"坐在正后方第二排"）、坐在正前方／正后方／正左侧／正右侧、安排在同一排／同一列、相邻就座。方向类关系只管方向，不限距离——"坐在正后方"包含正后方任意一排。

相对位置和区域规则都可以勾选"反向限制"。例如把"相邻"变成"不相邻"，或者把"只能坐在这一片"变成"不许坐这一片"。

### 相邻的三种范围

相邻就座可以选横向、四向或八向：

- **横向** —— 同一排的左右邻座。
- **四向** —— 前后左右四个方向的邻座。
- **八向** —— 再加上斜对角的四个位置。

横向相邻还可以选择同桌方式：

- **必须同桌（紧挨着）** —— 只认直接左右相邻的课桌，中间隔着走道就不算。
- **可隔一条窄走道** —— 两侧只隔着一条窄走道的课桌也算相邻；宽走道不算，连续两三格的空隙也不算。

四向和八向仍按原有规则，把单格正交窄走道两侧的课桌视作相邻。

抽象的相对位置画布上没有过道，所以那里只显示直接紧邻的位置；实际教室预览、预检查和求解都按上面这套规则来。

## 数据存放位置

数据默认放在程序所在目录的 `data` 下：

```
<程序目录>/data/
  config.json      设备 UUID、最近使用的工作区
  imgui.ini        窗口与停靠布局
  plugins/
    pokeseats.defaultpayload/
      students.json    花名册
      layout.json      教室布局
      rules.json       排座规则
    pokeseats.default/
      workspace-settings.json  外观设置
```

外观设置（亮色/暗色、背景色、面板色、文字色、强调色）保存在 `data/plugins/pokeseats.default/workspace-settings.json`，修改后立即生效。

如果程序目录不可写（例如装进系统级目录，或 macOS 应用包受签名保护），宿主会退回到当前用户的数据目录：Windows 是 `%LOCALAPPDATA%\PokeSeats`，Linux 是 `~/.local/share/PokeSeats`，macOS 是 `~/Library/Application Support/PokeSeats`。

`config.json` 里的 `deviceId` 由当前设备的系统机器标识派生（Windows `MachineGuid`、Linux `/etc/machine-id`、macOS `IOPlatformUUID`），重启和升级都不变。把整个 `data` 目录复制到另一台设备后，那台设备会按自己的机器标识重新生成 UUID，不会两台机器共用一个身份。

`config.json` 会保存 SECTL 平台 ID `sectlPlatformId`（`platform_5d216a8e4424effc`）和 Client ID `sectlClientId`（`6ad35e32003196df861c`）。程序启动后会向 `https://appwrite.sectl.cn` 上报一次版本使用情况，并立即、每 5 分钟上报一次在线状态。版本接口使用平台 ID，在线接口使用 Client ID（平台文档 ID）；两类请求均携带设备 UUID，另分别携带版本号或设备类型。网络失败不会阻塞启动或排座。可用环境变量 `POKESEATS_SECTL_PLATFORM_ID` 覆盖版本上报的平台 ID；在线上报使用配置中的 `sectlClientId`。

名单、布局和规则都是明文 JSON，可以直接备份、复制到另一台机器，或者手工改。删掉文件等于恢复空白状态。排座结果不会自动写入磁盘，关掉程序就没了；需要留档时可以在结果面板导出 PNG 座位表，或导出包含名单、规则和完整座位表的 ZIP。

旧版本把数据放在 `%LOCALAPPDATA%\PokeSeats`。首次运行时会复制缺失的文件到新位置，旧文件保留不动。

## 从源码构建

需要 .NET SDK 6.0 或更高的 6.x 版本（仓库里的 `global.json` 会自动选版本）。首次构建会从 NuGet 拉取 Z3 和 OR-Tools，需要联网。

构建和运行：

```text
dotnet build PokeSeats.sln
dotnet run --project PokeSeats.UI
```

注意 `dotnet run --project PokeSeats.UI` 只构建界面工程本身，不会顺带部署内置插件。所以第一次跑要么先执行上面的解决方案构建，要么运行加 `--no-build` 使用已有产物。

发布：

```text
dotnet publish PokeSeats.UI -c Release -r win-x64 --self-contained true -o publish/win-x64
dotnet publish PokeSeats.UI -c Release -r ubuntu-x64 --self-contained true -o publish/linux-x64
```

按运行时分别发布，产物自带 .NET 运行时，每个输出目录只包含对应平台的原生库，整个目录拷走即可运行，目标机器不需要预装 .NET。发布时插件目录只拷贝插件自身的程序集与原生库，不会重复打包运行时；符号文件和 XML 文档在发布目标里被剔除。Linux 用 `ubuntu-x64` 而不是 `linux-x64`：Z3 的 NuGet 包只把 Linux 原生库放在 `runtimes/ubuntu-x64` 下，而 RID 回退只能从具体到宽泛，`linux-x64` 取不到它，无解时的冲突规则定位就会失效。产物在 glibc 发行版上通用，已在 Arch Linux 上验证启动与 Z3 求解。不指定 `-r` 时各平台原生库会一起打包，体积约 520 MB，通常没有必要。求解器依赖的原生库只有 64 位版本，构建目标固定为 x64。

## 项目结构

```text
PokeSeats.IR              规则、人员、网格的数据结构和基础类型
PokeSeats.Core            预检查、图距离分析、Z3 / CP-SAT 求解
PokeSeats.UI.PluginContracts  插件接口、共享 design tokens 和界面缩放
PokeSeats.UI              桌面宿主：窗口、渲染、插件发现、工作区切换
PokeSeats.UI.DefaultWorkspace 默认工作区（菜单栏和面板布局）
PokeSeats.UI.DefaultPayload   业务面板：名单、布局、规则、排座结果
PokeSeats.UI.TestPlugin   插件开发用的示例，不随程序发布
```

`PokeSeats.UI` 只是一个宿主。界面内容全部来自插件，默认工作区加上默认业务面板才是你看到的样子。想自己写插件看 [PokeSeats.UI/PLUGIN.md](PokeSeats.UI/PLUGIN.md)。

## 字体

界面使用 HarmonyOS Sans SC，字体已随程序打包，不需要额外安装。字体版权归华为所有，按 HarmonyOS Sans Fonts License Agreement 使用，许可证文本随程序一起分发（`LICENSE_Fonts`），程序内的"关于"对话框也有署名。

## 版权与许可

Copyright (c) 2025-2026 SECTL Studio & chenjintang-shrimp

本项目以 GNU General Public License 第 3 版或更高版本（`GPL-3.0-or-later`）发布，完整协议文本见 [LICENSE](LICENSE)。你可以自由使用、修改和分发，包括商用；分发修改后的版本时需要一并提供源码，并保留版权声明。

第三方依赖的许可证：Z3、Dear ImGui／ImGui.NET、Silk.NET、pinyin4net 为 MIT；OR-Tools、NPOI、SixLabors.Fonts、SixLabors.ImageSharp 2.1.10、SixLabors.ImageSharp.Drawing 1.0.0 为 Apache-2.0。HarmonyOS Sans 字体单独适用字体许可，见上一节。
