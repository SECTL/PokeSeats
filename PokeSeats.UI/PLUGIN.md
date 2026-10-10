# PokeSeats UI 插件

可执行文件是一个小型插件宿主。工作区插件拥有完整的 ImGui 内容区域：停靠、导航、菜单和面板。宿主只负责窗口/渲染生命周期、插件发现、诊断信息和工作区选择器。

## 平台目标

目标优先级为 Windows、Linux，然后是 Android；iOS 不在范围内。当前实现是桌面宿主。Windows 和 Linux 共用工作区契约和外部 DLL 插件部署。Linux 运行时验证尚未完成。

Android 需要平台入口点、OpenGL ES 渲染器、触控输入，以及为 ImGui 和求解器打包的本地库。目标是复用工作区实现和嵌入字体；当前 OpenGL 控制器与 OpenGL ES 的兼容性尚未验证。`PokeSeats.Core` 当前目标为 x64 并引用了 `Microsoft.Z3.x64`，因此无法在 Android ARM64 上原样复用。

桌面宿主目前通过清单和 `AssemblyLoadContext` 发现插件 DLL。Android 的插件部署仍需针对选定的运行时和裁剪配置进行验证；编译时注册是选项之一，不是 UI 契约的硬性要求。Android 还需要触控可访问的宿主工作区选择器，而不是依赖 `Ctrl+Alt+Shift+X`。

`PokeSeats.UI.PluginContracts` 不包含任何 Windows 或图形后端 API。将平台相关操作保留在宿主中；可复用插件必须自行处理路径、输入和本地依赖在各平台上的差异。

## 安装布局

把每个插件放在可执行文件旁边 `plugins` 的直属子目录中：

```text
plugins/
  example/
    plugin.json
    Example.Plugin.dll
    Example.Plugin.deps.json
```

清单如下：

```json
{
  "id": "example",
  "apiVersion": 3,
  "assembly": "Example.Plugin.dll",
  "entryPoint": "Example.Plugin.Plugin",
  "dependencies": [],
  "fonts": [
    {
      "family": "HarmonyOS Sans SC",
      "sizePixels": 18,
      "glyphRange": "ChineseSimplifiedCommon"
    }
  ]
}
```
`fonts` 是可选的。每个请求指定一个字族、一个 8 到 128 之间的 `sizePixels` 值，以及一个字形范围（`Default`、`ChineseSimplifiedCommon`、`ChineseFull`、`Cyrillic`、`Greek`、`Japanese`、`Korean`、`Thai` 或 `Vietnamese`）。宿主在构建 ImGui 字体图集之前解析这些请求；为兼容已有清单，`ChineseSimplifiedCommon` 当前按完整中文范围加载，以覆盖 GB2312 中的“帧”等字符。`HarmonyOS Sans SC` 已嵌入宿主，不需要系统安装。其他字族在 Windows、Linux 和 macOS 的常用系统字体目录中解析，优先选择 Regular 字形。缺失的字体会记录日志并跳过，不会阻止插件启动。请求按字族去重，第一个声明的字族拥有该字族，第一个成功加载的请求会成为默认的 ImGui 字体。宿主拥有图集和字体纹理的生命周期，因此插件不得自行调用 `AddFontFromFileTTF`。

宿主以 HarmonyOS Sans Fonts License Agreement 提供未修改的 `HarmonyOS Sans SC` Regular 字形。版权声明和许可证随应用一起分发，默认工作区在其帮助菜单中展示所需的署名。字体随应用捆绑，不作为独立字体包分发。

内置插件为简体中文 UI 文本请求 `HarmonyOS Sans SC`。`Maple Mono NF CN` 安装后同样受支持，可由外部插件请求：

```json
"fonts": [
  {
    "family": "Maple Mono NF CN",
    "sizePixels": 18,
    "glyphRange": "ChineseSimplifiedCommon"
  }
]
```

入口点必须是一个公共类，带有公共无参构造函数，并实现 `IUiPlugin`。引用 `PokeSeats.UI.PluginContracts`；宿主提供已加载的 ImGui.NET 和 Silk.NET 程序集。插件程序集在进程内运行。`AssemblyLoadContext` 用于隔离依赖解析，不是权限或崩溃隔离。安装、更新或移除插件后需要重启应用才能生效。

私有的托管和本地依赖应放在插件旁边，连同其 `.deps.json`。针对宿主的 ImGui.NET `1.91.6.1` 和 Silk.NET `2.23.0` 构建 UI 插件；这些程序集是共享的，不会私有加载。清单中的依赖是插件 ID：所需插件先初始化，缺失、失败或循环依赖会导致其消费者无法初始化。`Initialize` 失败不会提交任何面板、命令或工作区。成功初始化的插件按逆初始化顺序释放。

## 工作区

插件通过 `IPluginContext.RegisterWorkspace` 注册一个或多个工作区。宿主按显示名称进行不区分大小写的序号比较排序，然后按稳定的工作区 ID 排序。默认使用第一个工作区，除非上次选中的 ID 可用。选中的 ID 存储在平台本地应用数据目录下（Windows 为 `%LOCALAPPDATA%/PokeSeats`，其他平台使用对应的 .NET 本地应用数据路径）。

按 `Ctrl+Alt+Shift+X` 打开宿主拥有的模态选择器。按住快捷键不会重复打开。选择在当前 ImGui 帧结束后生效；切换时会调用旧工作区的 `Deactivate` 和新工作区的 `Activate`。如果激活失败，旧工作区会被恢复，已保存的 ID 不会改变。如果保存的工作区已被移除，启动时会使用第一个可用工作区。未安装任何工作区时，宿主仍会启动并显示选择器。一个插件失败不会阻止独立插件加载，加载诊断信息会显示在选择器中。

插件也可以注册面板和命令。贡献项在进程生命周期内不可变，任何工作区都可通过 `IWorkspaceContext` 查看它们。

仅在 `Initialize` 内部注册贡献项。工作区工厂必须返回全新实例：宿主在切换期间保留它，并在释放插件前先释放它。所有生命周期和绘制回调都在 UI 线程上运行。平衡 ImGui `Begin`/`End` 调用；插件不得调用 `NewFrame`、`EndFrame`、`Render`，也不得销毁共享上下文。插件绘制异常或本地/上下文损坏无法被宿主安全恢复。

默认工作区使用覆盖主视口的无装饰根容器：每帧同步视口位置和尺寸，无标题栏、边框、内边距、拖动、停靠或布局持久化，视觉上直接绘制在应用内容区。`InvisibleButton` 等控件和 `GetWindowDrawList` 必须在 `Begin`/`End` 内调用；否则会落入 ImGui 隐式的 `Debug##Default` 窗口。纯图形可以通过 `GetBackgroundDrawList` 绘制，但它不提供控件布局或输入处理。

### 窗口比例缩放

桌面宿主以默认客户区 `1280 × 720` 为基准，自动基础比例为 `max(1, min(width / 1280, height / 720))`。用户缩放是独立的全局 `0.05x` 步进修正，最终比例为自动基础比例加上该修正，范围为 `0.50x` 到 `3.00x`。字体和 ImGui 样式在每帧开始之前应用最终比例；样式从未缩放的快照重算，避免最大化、恢复窗口和手动缩放时累计误差。工作区激活后的样式成为新的基准。

字体根据原始轮廓以 `sizePixels × 窗口比例 × framebuffer 像素密度` 重新栅格化；例如 18 px 字体在 1.5 倍和 2 倍窗口下分别生成 27 px 和 36 px 字形。像素密度通过字体逻辑比例抵消，控件大小仍使用窗口坐标，不通过 `FontGlobalScale` 拉伸旧字形。宿主仅在比例或像素密度变化时，于上一帧结束后、下一帧开始前重建图集。

字体缩放器拥有可重新分配的 OpenGL 纹理；Silk.NET 的初始字体纹理使用不可变存储，不能用 `TexImage2D` 扩容。ImGui 的 `Build()` 会清空 `TexID`，宿主上传新图集后重新设置该句柄，并在退出时释放自有纹理。插件不得修改图集或接管字体纹理。

`PokeSeats.UI.PluginContracts.UiScale.Current` 提供当前比例；插件的固定控件尺寸、自绘坐标和鼠标命中检测应使用 `UiScale.Pixels(...)` 转换。`IUiView.MinimumSize` 始终使用基准尺寸，由工作区乘以当前比例，视图不要重复缩放该属性。自动布局尺寸无需再次缩放。

默认工作区同步缩放浮动面板的位置和尺寸，保留运行期间用户调整后的相对布局；停靠面板的几何由 ImGui 停靠系统管理。布局网格、边框、选区和名单表格高度使用相同比例；缩放不改变布局行列或保存的数据。小于基准尺寸时自动基础比例维持 1，但用户仍可手动缩小 UI；宽高比不同的窗口使用较小的基础比例，避免拉伸。

全局缩放快捷键：`Ctrl +` 或 `Ctrl + 小键盘加号` 增大 `0.05x`，`Ctrl -` 或 `Ctrl + 小键盘减号` 减小 `0.05x`，`Ctrl + 0` 清除手动修正并恢复自动基础比例。主键盘的 `Ctrl + Shift + =` 等同于 `Ctrl +`。快捷键不响应带 `Alt` 的组合；调整后右下角短暂显示当前最终比例。字体仍从原始轮廓重新栅格化，不拉伸旧字体纹理。


## 插件间消息

API 版本为 `3`。插件初始化通过 `IPluginContext.Events` 获取消息总线；工作区每次激活通过 `IWorkspaceContext.Events` 获取新的作用域。两者连接同一个宿主总线，不需要全局静态事件或业务服务注册表。

```csharp
// 放在提供者插件的入口程序集里，供消费者引用。
public sealed record SelectionChanged(string PersonId);

// 提供者：例如在面板点击后发送。
context.Events.Publish(new SelectionChanged("person-42"));

// 消费者：订阅返回 IDisposable，可提前取消；否则宿主自动回收。
IDisposable subscription = context.Events.Subscribe<SelectionChanged>(message =>
{
    selectedPersonId = message.PersonId;
});
```

消费者项目引用定义消息的提供者程序集，并在 `plugin.json` 的 `dependencies` 中声明提供者插件 ID。宿主共享这些直接依赖插件的已加载入口程序集，即使消费者目录里带了一份相同 DLL，也不会重复加载消息类型。不要把同名消息类分别复制到两个插件中；未声明依赖的私有 DLL（包括单独的消息契约 DLL）仍各自加载，不保证类型身份一致。消息总线按 `Publish<TMessage>` 的精确 .NET 类型匹配，不向基类或接口订阅广播。

分发规则：

- UI 线程发布同步分发，嵌套发布也同步执行；回调应保持短小，并避免递归发布同一消息。
- 后台线程发布进入队列；宿主在下一帧开始、ImGui `Update` 之前按入队顺序分发当时待处理的消息。所有处理函数都在 UI 线程执行；消息处理用于更新状态，不应直接绘制 ImGui。
- 消息不做历史回放。后台消息在分发时匹配仍有效的订阅；消息对象应不可变，发布后不要继续修改它。
- 处理函数的托管异常逐个记录，其他订阅者继续执行。取消订阅不等待已开始的回调结束。
- 插件初始化失败或插件释放后，宿主回收其所有订阅；工作区的 `Deactivate` 结束后（包括抛异常）回收本次激活的订阅。激活失败也会回收，新一次激活须重新订阅。
- 已释放作用域不能再发布或订阅，会抛出 `ObjectDisposedException`；此前由该作用域排队、尚未分发的消息会丢弃。停用工作区不会取消通过插件初始化作用域创建的订阅。

例如，工作区只在激活期间接收消息：

```csharp
public sealed class SelectionWorkspace : IWorkspace
{
    private string? _selectedPersonId;

    public void Activate(IWorkspaceContext context)
    {
        context.Events.Subscribe<SelectionChanged>(message =>
            _selectedPersonId = message.PersonId);
    }

    public void Draw(float deltaSeconds)
    {
        ImGuiNET.ImGui.DockSpaceOverViewport();
        bool visible = ImGuiNET.ImGui.Begin("Selection");
        try
        {
            if (visible)
                ImGuiNET.ImGui.Text(_selectedPersonId ?? "No selection");
        }
        finally { ImGuiNET.ImGui.End(); }
    }

    public void Deactivate() { } // 宿主回收本次 Activate 的订阅。
    public void Dispose() { }
}
```

## 内置插件与构建

`PokeSeats.UI.DefaultWorkspace` 是随程序构建和发布的默认工作区插件，保留当前自定义菜单栏绘制。入口类型为 `PokeSeats.UI.DefaultWorkspace.DefaultUIPlugin`，工作区 ID 为 `pokeseats.defaultworkspace.workspace`。它作为单独的 DLL 构建，并在 `dotnet build` 和 `dotnet publish` 期间与 `plugin.json` 一起部署到 `builtins/pokeseats.default`；宿主在运行时不会引用其实现。内置插件与外部插件使用完全相同的清单和注册契约。

`PokeSeats.UI.TestPlugin` 保持为独立测试插件，提供停靠工作区、面板切换菜单、贡献命令入口和性能面板，不会自动部署为内置插件。需要使用时，将其 DLL 和该项目的 `plugin.json` 放入应用目录的 `plugins/pokeseats.test` 后重启。
### 默认业务面板

`PokeSeats.UI.DefaultPayload` 注册“学生名单”“教室布局”“规则编辑”三个独立视图，可通过“面板”菜单显示或隐藏。教室布局使用 ImGui 绘图列表绘制网格，不依赖表格控件：左键拖动选择矩形，松开后选择“桌子/座位”“宽过道”“窄过道”“讲台”或“空白”，右键可直接编辑单格。行列尺寸限制为 1 到 32，布局保存到插件数据目录的 `layout.json`，名单仍保存到 `students.json`。

布局编辑器只负责生成 `LayoutGrid` 快照；后续求解视图应通过同一份 payload 状态读取该快照，再调用 `PokeSeats.Core.Solver.SolverService`。

规则编辑与名单、布局共享内存状态，支持固定座位、区域限制、相对位置及行列人数。人员选择可搜索姓名、学号、全拼和拼音首字母；拼音使用 `hyjiacan.pinyin4net`，支持常见多音姓和复姓，名字中的多音字采用词典首读音。超出词典范围的生僻字仍可按姓名和学号搜索。

相对网格以 A 为中心，描述 B 相对于 A 的位置。可中键平移、滚轮缩放；左键按住并拖动时 B 随目标格更新，松开后弹出菜单选择固定偏移、前后左右、同排、同列或相邻，按 Escape 取消拖动。方向关系不附加距离限制；相邻默认指上下左右紧挨，可选择含斜角。相对规则和区域规则支持取反，例如“相邻”变成“不能相邻”；不提供相对轴区间或距离输入。

真实教室预览占据编辑区剩余高度，网格保持方格比例、居中并按可用宽高缩放，完整显示布局。相对关系无需选择人员即可预览；默认在第一张桌子临时放置 A，点击其他桌子可移动 A，绿色显示 B 候选座位。取反后的候选是其他桌子中不满足正向关系的座位，不包含 A 自己、过道或讲台；悬停关系菜单会同步预览该关系。这不代表满足所有规则的求解结果。保存仍要求选择两名不同人员；UI 在 Build/Load 边界交换人员映射，保持既有 IR“模型 A 相对于模型 B”的契约及已保存规则含义不变。

固定座位在真实教室中点选桌子，区域规则拖动框选矩形，人数规则点选行或列并设置最少、最多或恰好人数。顶部保存按钮写入 `rules.json`，右侧列表可重新编辑或确认删除；四种规则均使用稳定 RuleId，保存通过临时文件替换。预检查提示缺失人员、越界、非桌子、重复固定及规则恒真/恒假；修改名单或布局会刷新搜索和预览。求解仍通过 `SolverService` 读取保存的 IR 规则，取反由后端统一处理。

```text
dotnet build PokeSeats.sln
dotnet run --project PokeSeats.UI
dotnet publish PokeSeats.UI -c Release
```

宿主通过运行时 DLL 发现和归属贡献注册支持带记忆选择的完整工作区替换。API 3 提供类型安全的插件间消息、后台发布的 UI 线程分发、插件/工作区作用域自动清理，以及声明依赖插件的入口程序集共享。
