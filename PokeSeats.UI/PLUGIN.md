# PokeSeats 插件开发

PokeSeats 的界面不是一个整体，而是由宿主和插件拼起来的。

宿主 `PokeSeats.UI` 只负责窗口、OpenGL 渲染循环、插件发现和工作区切换。你看到的一切内容——菜单栏、面板、对话窗口——都来自插件的工作区实现。默认工作区提供菜单栏和面板排布，默认业务面板提供名单、布局、规则和结果四个界面。

这意味着你可以把整个界面换成自己的样子：注册一个新的工作区，接管全部绘制。也可以只写一个小面板，挂在现有工作区下面。

插件是普通的 .NET 类库，编译成 DLL 放进程序目录即可，进程内加载。本文档讲清楚怎么写一个。

## 目录结构

插件放在可执行文件的 `plugins` 目录下，每个插件一个子目录：

```text
plugins/
  example/
    plugin.json
    Example.Plugin.dll
    Example.Plugin.deps.json
```

如果插件有私有的托管或原生依赖，一起放进这个目录，带上 `.deps.json`。宿主不会去别处找它们。

内置插件放在 `builtins` 目录下，用的是完全相同的清单和注册契约——内置和外部插件之间没有特殊通道。

## 清单

`plugin.json` 描述插件的入口：

```json
{
  "id": "example",
  "apiVersion": 4,
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

`id` 会作为插件数据目录的名字，必须是安全的单层路径片段。

`entryPoint` 指向一个公共类，需要公共无参构造函数，并实现 `IUiPlugin`。

`dependencies` 填的是别的插件的 `id`。声明之后，那个插件会先初始化；它缺失、初始化失败或出现循环依赖时，当前插件不会初始化。依赖插件的入口程序集在进程内共享，所以两边可以放心地共用同一套消息类型。

`fonts` 是可选的，下面单独讲。

安装、更新或删除插件之后需要重启程序。

## 最小插件

先看一个能跑起来的最小例子——注册一个工作区，画一句话：

```csharp
using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace Example.Plugin;

public sealed class Plugin : IUiPlugin
{
    public void Initialize(IPluginContext context)
    {
        context.RegisterWorkspace("example.workspace", "示例工作区", () => new ExampleWorkspace());
    }

    public void Dispose() { }
}

internal sealed class ExampleWorkspace : IWorkspace
{
    public UiDesignTokens DesignTokens { get; } = UiDesignTokens.Light;

    public void Activate(IWorkspaceContext context) { }

    public void Draw(float deltaSeconds)
    {
        ImGui.DockSpaceOverViewport();
        if (ImGui.Begin("示例面板"))
        {
            ImGui.Text("你好，PokeSeats。");
        }
        ImGui.End();
    }

    public void Deactivate() { }
    public void Dispose() { }
}
```

项目引用 `PokeSeats.UI.PluginContracts`，针对宿主使用的 ImGui.NET `1.91.6.1` 和 Silk.NET `2.23.0` 构建。这两个程序集由宿主提供、进程内共享，不要把它们复制进插件目录。

编译出 DLL，按上面的目录结构放好，重启程序，`Ctrl+Alt+Shift+X` 就能在列表里看到"示例工作区"。

## 生命周期

所有回调和绘制都在 UI 线程上执行。几个约定值得记住：

**在 `Initialize` 里注册贡献项。** 面板、命令和工作区都在这个阶段登记；`Initialize` 抛异常时什么都不会提交，不会留下半成品。

**工作区工厂必须返回新实例。** 宿主在切换期间持有实例，并在释放插件之前先释放它。

**自己配平 ImGui 的 `Begin`/`End`。** 插件不要调用 `NewFrame`、`EndFrame`、`Render`，也不要销毁共享的上下文。绘制时的异常宿主无法安全恢复，所以别让它抛出去。

**贡献项在进程生命周期内不变。** 注册之后不能再增删，任何工作区都能通过 `IWorkspaceContext` 看到全部内容。

**成功初始化的插件按逆序释放。**

隔离方面：`AssemblyLoadContext` 只隔离依赖解析，不是权限边界，也不是崩溃隔离。插件和宿主在同一个进程里，插件崩了整个程序一起崩。

## 注册什么

`IPluginContext` 提供三种注册方式：

```csharp
void RegisterWorkspace(string id, string displayName, Func<IWorkspace> create);
void RegisterView(IUiView view);
void RegisterCommand(IUiCommand command);
```

工作区拥有整个内容区域，同一时刻只有一个处于激活状态。面板和命令是全局的，任何工作区都能看到，具体显示与否由工作区决定。默认工作区在根窗口里建 DockSpace，面板可以自由拖动停靠；首次出现时的位置和尺寸由工作区指定，之后的停靠布局交给 ImGui 记忆，窗口尺寸变化时按当前比例重算。

`IUiView.InitiallyVisible` 控制视图首次是否显示，默认显示。想让某个视图默认折叠（比如"排座结果"这种要点了命令才出现的），把它设成 `false`。

### 让工作区打开某个面板

工作区应该尊重用户手动设置过的显示／隐藏选择，同时响应来自命令的打开请求。订阅 `UiViewOpenRequested` 即可：

```csharp
context.Events.Subscribe<UiViewOpenRequested>(request =>
{
    if (request.ViewId != MyView.Id) return;
    SetViewVisible(MyView.Id, true);   // 显示
    // 并取消折叠、把窗口置前
});
```

默认工作区就是这么实现的，"开始自动排座"命令靠它打开初始隐藏的结果面板。

## 工作区切换

`Ctrl+Alt+Shift+X` 打开宿主自带的选择器（按住不放不会重复弹出）。选择在当帧结束后生效：先调用旧工作区的 `Deactivate`，再调用新工作区的 `Activate`。

切换失败时旧工作区会恢复，已保存的选择不变。保存的工作区已经被删掉时，启动时用列表里的第一个。一个工作区都没有时，宿主照样能启动并显示选择器。某个插件加载失败不影响其他插件，加载诊断会显示在选择器里。

工作区按显示名称排序（不区分大小写），名称相同时按 ID 排序。上次的选择记在 `%LOCALAPPDATA%/PokeSeats/selected-workspace.txt`。

## 界面文案资源

默认工作区和示例工作区各自使用 `WorkspaceStrings.resx`；默认业务面板使用 `PayloadStrings.resx`（名单、布局和排座）及 `RulesStrings.resx`（规则编辑、说明和诊断）。中文原文保存在中性资源文件中，随插件程序集嵌入，不需要额外安装资源文件。

各插件的 `.csproj` 为资源配置了 `StronglyTypedLanguage`、`StronglyTypedClassName`、`StronglyTypedNamespace` 和 `StronglyTypedFileName`。`dotnet build` 自动在 `obj/` 下生成内部强类型访问类；不要手工维护或提交生成的 `*.Designer.cs`。

设计时构建（Roslyn 语言服务、VS 设计时构建）只执行 `CompileDependsOn`，不经过 `PrepareResources`，默认不会生成强类型资源访问类，编辑器里会误报 `CS0103`（命令行构建却是成功的）。仓库根目录的 `Directory.Build.targets` 用 `GenerateStronglyTypedResources` 把 `ResGen` 挂到 `CoreCompile` 之前，让设计时构建也能生成并注册 `*.Designer.cs`；新增使用强类型资源的项目无需额外配置，但不要删除这个目标。

新增文案使用有意义的英文资源名，通过 `WorkspaceStrings.Confirm` 这样的属性读取。带变量的句子保留为完整格式模板，例如资源值 `帧时间：{0:F1} ms`，代码使用 `string.Format(WorkspaceStrings.FrameTime, milliseconds)`；在资源注释中说明占位参数含义。不要将一句话拆成多个固定顺序的片段。

ImGui 的 `##`／`###` 内部 ID、JSON 字段名、文件扩展名、导入列名识别别名以及拼音数据不属于界面文案，应继续留在代码中。弹窗标题从资源读取后拼接固定 `###id`，修改标题不会改变对应窗口的持久身份。Windows 文件选择器的筛选说明可放资源中，扩展名和 NUL 分隔符仍由代码组装。

当前只整理现有中文文案，没有增加语言选择器或运行时语言切换。资源使用 .NET 标准 `ResourceManager` 查找与回退；后续添加语言时可按标准 `.resx` 命名提供对应文化资源。

## 界面缩放

宿主以 `1280 × 720` 为基准算出一个基础比例 `max(1, min(宽/1280, 高/720))`。用户可以用 `Ctrl +`／`Ctrl -` 以 `0.05x` 为步进微调，`Ctrl + 0` 恢复自动值。最终比例限制在 `0.50x` 到 `3.00x`。

`UiScale.Current` 是当前比例，`UiScale.Pixels(n)` 把基准像素换算成屏幕像素。**插件里所有固定尺寸、自绘坐标和鼠标命中检测都应该走这个换算**，否则窗口一放大你的控件还停在原地。

两个容易踩的点：

- `IUiView.MinimumSize` 用基准尺寸填写，宿主会自动乘上比例，别再自己乘一遍。
- 自动布局的尺寸（比如 `BeginChild` 传 0 表示占满剩余空间）不需要换算。

字体不需要插件操心。宿主按 `字号 × 窗口比例 × framebuffer 像素密度` 从原始轮廓重新栅格化，所以 18 px 的字在 1.5 倍窗口下是 27 px 的清晰字形，不是拉伸出来的。图集和字体纹理都归宿主所有，插件不要碰。

## 工作区 design tokens

工作区通过 `IWorkspace.DesignTokens` 提供不可变的 `UiDesignTokens`。宿主在每次 `Activate` 前把 tokens 应用到普通 ImGui 控件的颜色、间距、圆角和边框，再交给已有的缩放逻辑处理。工作区仍可在 `Activate` 中补充 ImGui 样式。字体和字号仍由宿主管理。

内置标准工作区使用 `Light`，示例插件的测试工作区使用 `Dark`；两者共用同一组业务面板，可以用于检查插件在不同主题下的表现。测试插件不随程序发布。

从 `UiDesignTokens.Light` 或 `UiDesignTokens.Dark` 开始，用 `with` 定制；为每个工作区保存同一个实例，不要在属性 getter 或绘制循环中创建新对象：

```csharp
public UiDesignTokens DesignTokens { get; } = UiDesignTokens.Dark with
{
    Colors = UiDesignTokens.Dark.Colors with
    {
        Accent = new Vector4(.45f, .73f, 1f, 1f)
    },
    Metrics = UiDesignTokens.Dark.Metrics with { ControlRounding = 4f }
};
```

工作区调用面板时显式传入自己的 tokens：

```csharp
if (ImGui.Begin(title, ref open))
    view.DrawContent(deltaSeconds, DesignTokens);
ImGui.End();
```

面板实现 `DrawContent(float deltaSeconds, UiDesignTokens designTokens)`。普通控件自动继承样式，自绘内容从参数读取颜色和尺寸；不要把旧工作区的 tokens 缓存到后续绘制中：

```csharp
public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
{
    Vector2 min = ImGui.GetCursorScreenPos();
    Vector2 size = new(UiScale.Pixels(160), UiScale.Pixels(48));
    ImGui.InvisibleButton("##preview", size);
    ImDrawListPtr draw = ImGui.GetWindowDrawList();
    draw.AddRectFilled(min, min + size,
        ImGui.ColorConvertFloat4ToU32(designTokens.Colors.AccentSurface),
        UiScale.Pixels(designTokens.Metrics.ControlRounding));
    draw.AddText(min + new Vector2(UiScale.Pixels(designTokens.Metrics.SpaceMedium)),
        ImGui.ColorConvertFloat4ToU32(designTokens.Colors.Text), "预览");
}
```

颜色是归一化 RGBA `Vector4`。`Background`、`Surface`、`Canvas` 是背景层级，`Text`、`TextMuted` 用于这些背景和语义底色；`Accent`、`Success`、`Warning`、`Danger` 是强调／状态前景色，对应的 `*Surface` 是可承载文字的底色。`SecondarySurface` 提供第二类底色，`Border` 用于分隔和网格，`Selection` 是透明选区覆盖色，`SelectionBorder` 是不透明选区边线；`DangerHovered` 用于冲突项悬停底色。定制配色时应一起调整前景和底色，保持可读性。

`Metrics` 包含 `SpaceSmall`、`SpaceMedium`、`SpaceLarge`、`WindowRounding`、`ControlRounding` 和 `BorderWidth`，全部是基准像素。自绘时使用 `UiScale.Pixels` 换算一次；ImGui 样式由宿主统一缩放。控件专用尺寸仍由面板决定。默认业务面板已使用这些 tokens 绘制教室网格、规则预览、冲突高亮和座位表。

切换工作区会在下一次绘制时传入新工作区的 tokens；激活失败回退时也会重新应用原工作区的样式。当前契约版本是 **4**，旧插件需要补充 `DesignTokens` 属性、更新 `DrawContent` 签名、用新版契约重新编译，并将清单的 `apiVersion` 改成 `4`。业务面板不需要引用具体工作区插件，也不需要声明对它的依赖。

## 插件间消息

插件通过消息总线互相通信，不用全局静态事件，也不用自己搞服务注册表。

```csharp
// 消息类型定义在提供者插件的入口程序集里，消费者引用它。
public sealed record SelectionChanged(string PersonId);

// 提供者：需要的时候发一条。
context.Events.Publish(new SelectionChanged("person-42"));

// 消费者：订阅返回 IDisposable，可以提前取消；不取消的话宿主负责回收。
IDisposable subscription = context.Events.Subscribe<SelectionChanged>(message =>
{
    selectedPersonId = message.PersonId;
});
```

消息按**精确的 .NET 类型**匹配，订阅基类或接口收不到派生类型的消息。所以别把同一个消息类分别复制到两个插件里——那样是两个不同的类型。正确做法是消费者引用定义消息的那个程序集，并在 `plugin.json` 的 `dependencies` 里声明提供者。

未声明依赖的私有 DLL（包括单独的消息契约库）各加载各的，类型身份不保证相同。

### 分发规则

- **UI 线程发布立刻同步分发**，嵌套发布也是同步的。回调要短，别递归发同一条消息。
- **后台线程发布进队列**，宿主在下一帧开始、`ImGui.Update` 之前按入队顺序分发。所有处理函数仍在 UI 线程执行。处理函数用来改状态，不要在里面画界面。
- **不回放历史。** 后台消息在分发那一刻匹配当时还有效的订阅。
- **消息对象应当是不可变的**，发出去之后别再改。
- **单个处理函数抛异常只记录日志**，其他订阅者继续执行。取消订阅不会打断已经开始执行的回调。

订阅的生命周期跟着作用域走：插件初始化时拿到的总线（`IPluginContext.Events`）活到插件释放；工作区每次激活拿到的是新的作用域（`IWorkspaceContext.Events`），`Deactivate` 结束后——包括 `Deactivate` 抛异常的情况——本次激活的订阅全部回收。激活失败同样回收，下次激活要重新订阅。

已释放的作用域再发布或订阅会抛 `ObjectDisposedException`，它此前排队但还没分发的消息会被丢弃。停用工作区**不会**影响插件初始化阶段建立的订阅。

工作区只在激活期间收消息：

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
        ImGui.DockSpaceOverViewport();
        if (ImGui.Begin("Selection"))
        {
            ImGui.Text(_selectedPersonId ?? "未选择");
        }
        ImGui.End();
    }

    public void Deactivate() { }   // 本次激活的订阅由宿主回收。
    public void Dispose() { }
}
```

宿主自己发布的消息有两条：`ApplicationExitRequested`（请求退出程序）和 `UiViewOpenRequested`（请求显示并置前某个视图）。

## 字体

字体在清单的 `fonts` 里申请，宿主在建立字体图集之前统一处理。每个请求给三个值：字族、`sizePixels`（8 到 128）、字形范围。

字形范围可选：`Default`、`ChineseSimplifiedCommon`、`ChineseFull`、`Cyrillic`、`Greek`、`Japanese`、`Korean`、`Thai`、`Vietnamese`。

`HarmonyOS Sans SC` 已经内置在宿主里，不需要系统安装。其他字族会在 Windows、Linux、macOS 的常用字体目录里查找，优先用 Regular 字形——比如装了 `Maple Mono NF CN` 就可以直接申请：

```jsonc
"fonts": [
  { "family": "Maple Mono NF CN", "sizePixels": 18, "glyphRange": "ChineseSimplifiedCommon" }
]
```

几个细节：

- 请求按字族去重，第一个声明某个字族的插件拥有它，第一个成功加载的请求成为 ImGui 默认字体。
- 找不到的字体会记录日志并跳过，不会阻止插件启动。
- 为了兼容已有的清单，`ChineseSimplifiedCommon` 目前按完整中文范围加载，这样 GB2312 里的"帧"之类的字也能显示。
- 图集和字体纹理归宿主所有，插件**不要**自己调用 `AddFontFromFileTTF`。

内置插件为中文界面申请 `HarmonyOS Sans SC`。字体按 HarmonyOS Sans Fonts License Agreement 随程序分发，不单独作为字体包发布。

## 内置插件

`PokeSeats.UI.DefaultWorkspace` 是默认工作区，提供现在这套菜单栏和面板布局。入口类型 `PokeSeats.UI.DefaultWorkspace.DefaultUIPlugin`，工作区 ID `pokeseats.defaultworkspace.workspace`。它作为独立 DLL 构建，`dotnet build` 和 `dotnet publish` 时会连同 `plugin.json` 一起复制到 `builtins/pokeseats.default`。宿主在运行时并不引用它的实现。

`PokeSeats.UI.DefaultPayload` 提供四个业务面板："学生名单""教室布局""规则编辑""排座结果"，可以通过菜单栏的"功能面板"开关显示；"排座结果"默认隐藏，由"开始自动排座"命令打开。

`PokeSeats.UI.TestPlugin` 是给插件开发用的示例，有停靠工作区、面板切换菜单、命令和性能面板，**不随程序发布**。想用的话，把它的 DLL 和 `plugin.json` 放进应用目录的 `plugins/pokeseats.test` 再重启。

## 默认业务面板的细节

这一节讲默认实现的行为，自己写界面的话可以跳过。

**名单**支持从 Excel 导入，也有"清空名单"（会二次确认，然后写入空的 `students.json`）。清空名单不会连带删除排座规则，但引用到已删除学生的规则需要重新设置。

**教室布局**用 ImGui 绘图列表手绘网格，不走表格控件。左键拖出矩形选区，松开后选择区域类型；右键直接改单格。行列数限制在 1 到 32。

**相对位置网格**以学生 A 为中心描述 B 的位置，可以中键平移、滚轮缩放。左键按住拖动时 B 跟着走，松开后弹菜单选关系，按 Escape 取消。方向类关系不附加距离限制。

相邻范围提供横向、四向、八向三档，两侧只隔着单条窄过道的课桌也算相邻（横向支持左右跨窄过道，四向和八向支持正交方向跨窄过道；宽过道、连续多格空隙、隔着普通课桌的两格都不算）。抽象画布上没有过道格，所以那里只显示直接紧邻的位置；菜单也只在落点符合当前范围时才列出"相邻"，这个判断不受"反向限制"影响。保存的规则会写入 `Adjacency` 字段；旧文件没有这个字段时，按原来的 `Metric` 把 Manhattan 映射成四向、Chebyshev 映射成八向。

**真实教室预览**占据编辑区剩余高度，网格保持方格比例并尽量填满，格子最小边长取"讲"字的宽高较大值加 8 个基准像素。空间不够时滚动查看。不选学生也能预览：默认把 A 临时放在第一张桌子上，点别的桌子可以移动它，绿色格子就是 B 的候选位置。

**求解**捕获当前的名单、布局和规则快照，在后台通过 `PokeSeats.Core.Solver.SolverService` 调用 Z3。求解期间重复点命令只会重新打开结果窗口，不会起第二个任务；关掉结果窗口也不会取消已经开始的求解。

**冲突提示**依赖 `SolverResult.ConflictingRuleIds`：Z3 无解时它是 UNSAT 核心映射回规则 ID 的结果，CP-SAT 无解时用同一套展开计划补跑一次 Z3 来做诊断。这个集合是"共同导致无解"的一组规则，不保证最小，也不保证覆盖全部冲突；如果是预检查直接拒绝，返回的是带错误的规则 ID，全局性错误不会随便归到某条规则头上。失败时结果面板会打开规则编辑，把这些规则标红。改过名单、布局或规则之后旧标记失效，旧结果仍然保留，只是提示你重新排座。
