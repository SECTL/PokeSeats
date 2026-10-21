# PokeSeats 插件开发

宿主 `PokeSeats.UI` 只负责窗口、渲染循环（Windows 用 Direct3D 11，Linux 用 OpenGL）、插件发现和工作区切换。菜单栏、面板、对话框都由插件绘制：默认工作区 `PokeSeats.UI.DefaultWorkspace` 提供菜单和面板排布，默认业务面板 `PokeSeats.UI.DefaultPayload` 提供名单、布局、规则和排座结果。

插件是普通的 .NET 6 类库，编译成 DLL 后放进程序目录的 `builtins/<目录>/` 或 `plugins/<目录>/`，在宿主进程内加载。两个目录使用相同的清单格式和注册契约，没有权限差别。

当前契约版本是 5（`PluginApi.Version`）。清单里的 `apiVersion` 必须正好等于 5；旧插件需要用当前的 `PokeSeats.UI.PluginContracts` 重新编译。

## 目录与清单

```text
<程序目录>/
  PokeSeats.UI.exe
  builtins/
    pokeseats.default/          # 默认工作区
    pokeseats.defaultpayload/   # 默认业务面板
    pokeseats.betapayload/      # Beta 功能插件
  plugins/
    sample.plugin/
      plugin.json
      SamplePlugin.dll
      SamplePlugin.deps.json
      ...                       # 私有托管或原生依赖
```

宿主只扫描这两个根目录的直接子目录，要求子目录里有 `plugin.json`。目录名可以随意，插件身份由清单里的 `id` 决定。私有依赖放在插件自己的目录，由 `.deps.json` 解析；宿主不会去别处查找。

```json
{
  "id": "sample.plugin",
  "apiVersion": 5,
  "assembly": "SamplePlugin.dll",
  "entryPoint": "SamplePlugin.SamplePlugin",
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

| 字段 | 要求 |
| --- | --- |
| `id` | 必填。全局唯一，同时是 `data/plugins/<id>/` 的目录名，必须是安全的单层文件名：不能含路径分隔符、非法文件名字符、结尾的点，也不能是 `CON`、`NUL`、`COM1` 等 Windows 设备名。 |
| `apiVersion` | 必填整数，必须等于 5。 |
| `assembly` | 必填。相对路径，以 `.dll` 结尾，必须指向插件目录内部实际存在的文件；不能是绝对路径。 |
| `entryPoint` | 必填。`IUiPlugin` 实现类的完整类型名；必须是 public 的顶层、非抽象类，并提供 public 无参构造函数。 |
| `dependencies` | 可选，依赖插件的 `id` 数组。依赖先初始化；依赖缺失、失败或出现循环时，当前插件跳过。 |
| `fonts` | 可选，字体请求数组，见下文。 |

清单解析允许注释和尾逗号，未知字段会被忽略。任何字段校验失败都会使整个插件被跳过，原因写入标准错误，并显示在工作区选择器里。重复 `id` 按扫描顺序处理：先出现的有效清单拥有该 `id`，后面的重复项被忽略；如果先出现的是无效清单，后面的有效清单可以接管。扫描顺序固定为 `builtins`、`plugins`，各自按子目录名的序数顺序。

`dependencies` 中依赖插件的入口程序集在进程内共享，因此可以共用同一套消息类型。没有声明依赖的私有 DLL 各加载各的，即使文件名和类型名相同，也是不同的 .NET 类型。

加载上下文只隔离依赖解析，不是权限沙箱，也不是崩溃隔离。插件和宿主在同一进程里，插件抛出未处理异常会让整个程序退出。加载上下文不可回收，插件在进程生命周期内不会卸载。

安装、更新或删除插件后必须重启程序。运行中切换工作区不会重新扫描插件。

## 入口与生命周期

```csharp
public interface IUiPlugin : IDisposable
{
    void Initialize(IPluginContext context);
}
```

所有回调、绘制和事件处理都在 UI 线程执行。生命周期约定：

1. 宿主创建入口类型实例，调用一次 `Initialize`。工作区、视图和命令只能在这个阶段通过 `IPluginContext` 注册。
2. 注册先暂存，`Initialize` 正常返回后才提交。`Initialize` 抛异常时，宿主调用一次 `Dispose`，释放该插件的事件作用域，不提交任何工作区、视图或命令。
3. `Initialize` 返回后再调用 `RegisterWorkspace`、`RegisterView`、`RegisterCommand` 会抛 `InvalidOperationException`。同一插件内或跨插件出现重复的贡献 `id`，初始化直接失败。
4. 成功初始化的插件在程序关闭时按初始化逆序调用 `Dispose`；`Dispose` 抛出的异常只记录日志。之后插件级事件作用域被释放。
5. 贡献项在进程生命周期内固定，运行中不能增删。

`IPluginContext` 提供：

| 成员 | 说明 |
| --- | --- |
| `PluginId` | 清单里的 `id`。 |
| `DataDirectory` | `data/plugins/<id>/`，宿主在 `Initialize` 前创建。插件自己的 JSON、缓存等放这里，不要写安装目录。 |
| `Events` | 插件级事件总线，活到插件释放。 |
| `RegisterWorkspace` | 注册一个工作区工厂。 |
| `RegisterView` | 注册一个全局视图。 |
| `RegisterCommand` | 注册一个全局命令。 |

插件级上下文没有纹理服务。纹理只能在获得工作区激活上下文之后创建。

工作区工厂在第一次激活该工作区时调用，必须返回新实例；宿主为每个工作区保留一个实例并负责释放，工厂返回 `null` 会使激活失败。切换走再切回来，宿主复用同一实例并重新调用 `Activate`。

工作区切换由 `Ctrl+Alt+Shift+X` 打开的选择器发起，在当帧绘制结束后生效：先 `Deactivate` 旧工作区，再 `Activate` 新工作区。新工作区激活失败时，宿主恢复旧工作区；选择器里会显示失败原因。工作区按显示名称排序，名称相同按 `id` 排序；上次选择和启动时的回退都记录在 `data/config.json`。一个工作区都没有时程序仍能启动，只显示选择器和诊断信息。

## 完整示例

下面是一个能编译、能运行的最小插件：一个工作区、一个面板、一个用于打开面板的命令。把它保存为 `SamplePlugin.cs`。

```csharp
using System.Numerics;
using ImGuiNET;
using PokeSeats.UI.PluginContracts;

namespace SamplePlugin;

public sealed class SamplePlugin : IUiPlugin
{
    public const string WorkspaceId = "sample.workspace";
    public const string PanelId = "sample.panel";

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterWorkspace(WorkspaceId, "示例工作区", static () => new SampleWorkspace());
        context.RegisterView(new SamplePanel(PanelId));
        context.RegisterCommand(new ShowPanelCommand(context.Events));
    }

    public void Dispose()
    {
        // 工作区实例由宿主释放；插件自己创建的视图和命令在这里清理非托管资源。
    }
}

internal sealed class SampleWorkspace : IWorkspace
{
    private readonly Dictionary<string, bool> _visibility = new(StringComparer.Ordinal);
    private IWorkspaceContext? _context;
    private IDisposable? _viewOpenSubscription;
    private bool _disposed;

    public UiDesignTokens DesignTokens { get; } = UiDesignTokens.Light;

    public void Activate(IWorkspaceContext context)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SampleWorkspace));

        _context = context;
        _viewOpenSubscription = context.Events.Subscribe<UiViewOpenRequested>(request =>
        {
            if (string.Equals(request.ViewId, SamplePlugin.PanelId, StringComparison.Ordinal))
                _visibility[request.ViewId] = true;
        });
    }

    public void Draw(float deltaSeconds)
    {
        IWorkspaceContext? context = _context;
        if (context is null)
            return;

        ImGui.DockSpaceOverViewport();
        DrawMenuBar(context);

        foreach (IUiView view in context.Views)
        {
            if (IsVisible(view))
                DrawView(view, deltaSeconds);
        }
    }

    private void DrawMenuBar(IWorkspaceContext context)
    {
        if (!ImGui.BeginMainMenuBar())
            return;

        if (ImGui.BeginMenu("面板"))
        {
            foreach (IUiView view in context.Views)
            {
                bool visible = IsVisible(view);
                ImGui.PushID(view.Id);
                if (ImGui.MenuItem(view.DisplayName, null, visible))
                    _visibility[view.Id] = !visible;
                ImGui.PopID();
            }
            ImGui.EndMenu();
        }

        IUiCommand? pending = null;
        foreach (IUiCommand command in context.Commands)
        {
            ImGui.PushID(command.Id);
            if (ImGui.MenuItem(command.DisplayName))
                pending = command;
            ImGui.PopID();
        }

        ImGui.EndMainMenuBar();

        // 菜单关闭后再执行，避免命令在菜单栈未结束时切换状态。
        pending?.Execute();
    }

    private void DrawView(IUiView view, float deltaSeconds)
    {
        // MinimumSize 是基准像素，这里乘一次缩放比；不要再在视图内部乘。
        if (view.MinimumSize is { } minimum)
            ImGui.SetNextWindowSizeConstraints(
                minimum * UiScale.Current, new Vector2(float.MaxValue));

        bool open = true;
        if (ImGui.Begin($"{view.DisplayName}###{view.Id}", ref open))
            view.DrawContent(deltaSeconds, DesignTokens);
        ImGui.End();

        if (!open)
            _visibility[view.Id] = false;
    }

    private bool IsVisible(IUiView view)
        => _visibility.TryGetValue(view.Id, out bool visible) ? visible : view.InitiallyVisible;

    public void Deactivate()
    {
        _viewOpenSubscription?.Dispose();
        _viewOpenSubscription = null;
        _context = null;
    }

    public void Dispose()
    {
        _viewOpenSubscription?.Dispose();
        _viewOpenSubscription = null;
        _context = null;
        _visibility.Clear();
        _disposed = true;
    }
}

internal sealed class SamplePanel : IUiView
{
    public SamplePanel(string id) => Id = id;

    public string Id { get; }
    public string DisplayName => "示例面板";
    public Vector2? MinimumSize => new(360f, 200f);

    public void DrawContent(float deltaSeconds, UiDesignTokens designTokens)
    {
        ImGui.TextUnformatted($"帧时间：{deltaSeconds * 1000f:F1} ms");
        ImGui.TextDisabled("窗口标题、大小和关闭按钮由工作区负责，这里只画内容。");

        Vector2 origin = ImGui.GetCursorScreenPos();
        Vector2 size = new(UiScale.Pixels(160f), UiScale.Pixels(48f));
        ImGui.InvisibleButton("##sample-swatch", size);
        ImGui.GetWindowDrawList().AddRectFilled(
            origin, origin + size,
            ImGui.ColorConvertFloat4ToU32(designTokens.Colors.AccentSurface),
            UiScale.Pixels(designTokens.Metrics.ControlRounding));
    }
}

internal sealed class ShowPanelCommand : IUiCommand
{
    private readonly IUiEventBus _events;

    public ShowPanelCommand(IUiEventBus events) => _events = events;

    public string Id => "sample.command.show-panel";
    public string DisplayName => "显示示例面板";

    public void Execute()
        => _events.Publish(new UiViewOpenRequested(SamplePlugin.PanelId));
}
```

项目文件引用契约项目，并使用与宿主一致的 ImGui.NET 版本：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\PokeSeats.UI.PluginContracts\PokeSeats.UI.PluginContracts.csproj" />
    <PackageReference Include="ImGui.NET" Version="1.91.6.1" />
  </ItemGroup>
</Project>
```

插件只能用宿主提供的 `ImGui.NET`、`Silk.NET` 和 `PokeSeats.UI.PluginContracts` 程序集。这三个程序集由宿主加载并在所有插件间共享，不要把它们复制进插件目录，否则会出现类型身份或原生上下文不一致的问题。

构建后创建 `plugins/sample.plugin/`，放入 `SamplePlugin.dll`、`SamplePlugin.deps.json` 和上面的 `plugin.json`，重启程序，用 `Ctrl+Alt+Shift+X` 选择「示例工作区」。仓库里的 `PokeSeats.UI.TestPlugin` 是另一个可运行示例（停靠工作区 + 性能面板 + 命令），不随发布包分发。

## 工作区、视图与命令

### `IWorkspace`

工作区拥有整个内容区域：菜单栏、视图窗口、布局和导航都由它决定。同一时刻只有一个工作区处于激活状态。

| 成员 | 说明 |
| --- | --- |
| `DesignTokens` | 本次激活使用的主题。宿主在 `Activate` 前应用；之后若实例发生变化，会在当前帧绘制结束后重新应用，下一帧生效。 |
| `Activate(IWorkspaceContext)` | 进入工作区。这里可以订阅本次激活的事件、创建纹理。切换回来时会再次调用。 |
| `Draw(float deltaSeconds)` | 每帧绘制。只能画内容，不要调用 `ImGui.NewFrame`、`EndFrame` 或宿主渲染器的 `Render`。 |
| `Deactivate()` | 离开工作区。释放本次激活的订阅和纹理；宿主也会回收工作区级事件作用域。激活失败时同样会调用。 |
| `Dispose()` | 由宿主调用。工作区自己拥有、且不随激活销毁的资源在这里释放。 |

`Activate` 抛异常时，宿主调用 `Deactivate`、释放上下文，并尝试恢复上一个工作区。`Deactivate` 抛异常时，宿主仍会释放事件作用域，然后继续切换或退出。

### `IUiView`

视图只画内容，不拥有窗口。`Begin`/`End`、位置、大小、标题栏、关闭按钮和停靠策略都由工作区决定。

| 成员 | 说明 |
| --- | --- |
| `Id` | 非空、全局唯一、注册后不变。默认工作区用它做 ImGui 身份（`###<id>`）和菜单 ID，改了会让保存的窗口状态失效。 |
| `DisplayName` | 用户可见名称，可随文案调整。 |
| `InitiallyVisible` | 默认 `true`。首次呈现时是否显示；用户显式开关后，由工作区记住用户的选择。 |
| `MinimumSize` | 默认 `null`。基准像素下的最小尺寸；工作区会乘 `UiScale.Current`。不要在这里预先缩放。默认工作区对悬浮视图用它作为初始大小和缩放约束，对平铺视图用它作为内容最小宽度（不够时横向滚动）。 |
| `DrawContent(float deltaSeconds, UiDesignTokens designTokens)` | 只绘制内容。必须自行配平 `Begin`/`End`、`Push*`/`Pop*`。每次绘制都使用传入的 tokens，不要缓存上一次的主题。 |

视图实例是全局的，可能在多个工作区中被绘制。视图不能假定自己只属于某一个工作区，也不要在字段里保存某次激活的 tokens、纹理或事件总线。

### `IUiCommand`

命令由工作区呈现和调用，宿主不会自动生成菜单项。默认工作区把命令放在主菜单栏里，按 `id` 排序。`Execute` 在 UI 线程执行；耗时工作应转到后台，完成后用事件通知 UI。

### 贡献 ID 与可见顺序

工作区、视图、命令的 `id` 在各自类别内全局唯一，不能为空。插件内重复或与其他插件冲突都会让当前插件初始化失败。建议使用小写点分命名，例如 `sample.panel`、`sample.command.show-panel`。

视图按 `id` 的序数顺序呈现；工作区按显示名称（不区分大小写）排序，名称相同再按 `id` 排序。命令按 `id` 排序。

## 主题与缩放

### `UiDesignTokens`

`UiDesignTokens` 是不可变 record，包含 `Colors` 和 `Metrics`。从 `UiDesignTokens.Light` 或 `UiDesignTokens.Dark` 开始，用 `with` 复制并修改，保持每个工作区一个实例：

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

颜色是归一化 RGBA `Vector4`：

- `Background`、`Surface`、`Canvas` 是背景层级；`Text`、`TextMuted` 用于这些背景和语义底色。
- `Accent`、`Success`、`Warning`、`Danger` 是强调色和状态前景色，对应的 `*Surface` 是可承载文字的底色；`DangerHovered` 用于危险项悬停。
- `Border` 用于分隔和网格，`SecondarySurface` 是第二类底色，`Selection` 是半透明选区覆盖色，`SelectionBorder` 是不透明选区边线。

`Metrics` 全部是基准像素：`SpaceSmall`、`SpaceMedium`、`SpaceLarge`、`WindowRounding`、`ControlRounding`、`BorderWidth`。普通 ImGui 控件由宿主统一样式；自绘内容用 `UiScale.Pixels(designTokens.Metrics.X)` 换算一次，颜色直接交给 `ImGui.ColorConvertFloat4ToU32`。

不要在属性 getter 或每帧绘制时新建 tokens 实例；宿主用引用/值相等判断是否需要重新应用样式，频繁变化会反复重建样式。

### `UiScale`

| 成员 | 说明 |
| --- | --- |
| `UiScale.Current` | 当前 UI 缩放比，由宿主设置。 |
| `UiScale.Pixels(n)` | 把基准像素换算成当前屏幕像素。 |
| `UiScale.ForDisplayScale(scale)` | 从操作系统显示缩放得到基础比例，最小 1。宿主使用它。 |
| `UiScale.ForViewport(size)` | 按视口尺寸得到比例，供确实要按窗口大小缩放的调用方使用；宿主默认不用。 |

宿主在 Windows 上读取窗口所在显示器的 DPI，Windows 7 回退到系统 DPI/GDI；macOS 和 Linux 保持逻辑坐标，由 framebuffer 像素密度保证清晰度。窗口变大只会显示更多内容，不会整体放大 UI。用户可用 `Ctrl +`、`Ctrl -` 以 0.05 为步进调整，`Ctrl + 0` 恢复自动值，最终范围限制在 0.50 到 3.00。

自绘坐标、固定尺寸、间距和鼠标命中检测都要过 `UiScale.Pixels`。自动布局的零值（例如 `ImGui.BeginChild(id, Vector2.Zero)` 表示占满剩余空间）不需要换算。`IUiView.MinimumSize` 同样用基准像素，宿主会乘一次；插件再乘就会双重缩放。

字体不需要插件缩放：宿主按 `字号 × UI 比例 × framebuffer 像素密度` 从原始轮廓重建图集。缩放或像素密度变化时字体图集会重建，所以不要跨帧缓存 `ImFontPtr` 或字体纹理 ID。

## 事件

插件间通信使用宿主的事件总线，按精确的 .NET 类型匹配。

```csharp
// 消息类型定义在提供者插件的入口程序集里，消费者引用该程序集。
public sealed record SelectionChanged(string PersonId);

// 发布
context.Events.Publish(new SelectionChanged("person-42"));

// 订阅；Dispose 取消订阅，未取消时随作用域回收。
IDisposable subscription = context.Events.Subscribe<SelectionChanged>(message =>
{
    _selectedPersonId = message.PersonId;
});
```

分发规则：

- **精确类型**：`Subscribe<Base>` 收不到 `Derived` 消息。消息类型必须来自同一个已加载程序集；消费者应声明对提供者的依赖并引用其入口程序集，不能复制一份同名类型。
- **线程**：UI 线程发布同步分发；后台线程发布先入队，宿主在下一帧开始前于 UI 线程按入队顺序分发。所有处理函数都在 UI 线程运行。
- **不回放**：订阅之前的消息不会补发。后台消息在真正分发时匹配当时有效的订阅。
- **异常隔离**：单个处理函数抛异常只记录日志，其他订阅者继续执行。取消订阅不会打断已开始的回调。
- **不可变消息**：发出去之后不要修改消息对象。
- **作用域**：`IPluginContext.Events` 活到插件释放；`IWorkspaceContext.Events` 只在本次激活期间有效，`Deactivate` 结束后（包括抛异常）宿主回收全部订阅。激活失败同样回收，下次激活需要重新订阅。
- 已释放的作用域再发布或订阅会抛 `ObjectDisposedException`，其此前排队但未分发的消息被丢弃。停用工作区不影响插件级订阅。

宿主发布两条消息：

- `ApplicationExitRequested`：请求正常关闭程序。
- `UiViewOpenRequested(string ViewId)`：请求当前工作区显示并置前指定视图。默认工作区会响应；自定义工作区可以自行决定是否响应。

处理函数应该短小，只改状态或转发，不要在里面绘制界面；递归发布同一条消息容易造成无限递归。

## 纹理

契约版本 5 在工作区上下文中提供 `IUiTextureService`，把 PNG/JPEG 编码字节解码成当前渲染后端的纹理：

```csharp
public sealed class LogoWorkspace : IWorkspace
{
    private IUiTexture? _logo;

    public void Activate(IWorkspaceContext context)
    {
        using Stream? stream = typeof(LogoWorkspace).Assembly
            .GetManifestResourceStream("SamplePlugin.Assets.logo.png");
        if (stream is null)
            return;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        _logo = context.Textures.Create(buffer.ToArray());
    }

    public void Deactivate()
    {
        _logo?.Dispose();
        _logo = null;
    }

    // Draw 中用 ImGui.Image(_logo.TextureId, new Vector2(_logo.Width, _logo.Height) * UiScale.Current)
    // ...
}
```

- `Create` 接受 PNG/JPEG 字节；不支持的格式或渲染器不可用时返回 `null`，不会抛异常。嵌入资源缺失应自行判空。
- `IUiTexture` 提供 `TextureId`、`Width`、`Height`。Windows 上 `TextureId` 是 `ID3D11ShaderResourceView*`，Linux 上是 OpenGL 纹理 ID；插件只把它交给 `ImGui.Image` 等接口，不要解引用。
- 纹理归创建方所有，宿主不跟踪业务纹理。必须在 UI 线程、宿主渲染器关闭前调用 `Dispose`；通常放在工作区 `Deactivate` 或 `Dispose`。
- 纹理服务只能从 `IWorkspaceContext` 取得，`IPluginContext` 上没有。不要在 `Initialize` 里创建纹理。
- 不要假设纹理会自动释放或自动失效；工作区隐藏视图时也不需要释放纹理，但程序退出前必须释放。

## 字体

字体在清单的 `fonts` 里申请，宿主在建立字体图集之前统一处理：

```json
"fonts": [
  {
    "family": "HarmonyOS Sans SC",
    "sizePixels": 18,
    "glyphRange": "ChineseSimplifiedCommon"
  }
]
```

- `family` 必填。`HarmonyOS Sans SC` 已内嵌在宿主里，不需要系统安装；其他字族在 Windows、Linux、macOS 的常用字体目录中查找，优先 Regular 字形。
- `sizePixels` 取值 8 到 128，默认 18。
- `glyphRange` 可选 `Default`、`ChineseSimplifiedCommon`、`ChineseFull`、`Cyrillic`、`Greek`、`Japanese`、`Korean`、`Thai`、`Vietnamese`。为了兼容已有清单，`ChineseSimplifiedCommon` 和 `ChineseFull` 当前都按完整中文范围加载（否则 GB2312 中的「帧」等字会缺失）。
- 请求按字族去重，第一个声明某个字族的插件拥有它；第一个成功加载的字体成为 ImGui 默认字体。
- 找不到的字体会记录日志并跳过，不阻止插件启动。清单里的字体请求本身不合法（缺 `family`、字号越界、未知 `glyphRange`）会使整个清单无效。
- 字体文件和字体图集归宿主所有。插件不能提供字体文件、不能调用 `AddFontFromFileTTF`，也不要直接操作 `ImGui.GetIO().Fonts`。

## 默认工作区如何呈现视图

默认工作区 `builtins/pokeseats.default` 的行为可以作为参考，但自定义工作区不必照搬：

- 初始可见（`InitiallyVisible == true`）的视图平铺在可用区域内，按 `id` 排序，左列放 `ceil(n/2)` 个，列内纵向排列。三个视图时左列约占可用宽度的 40%、右列 60%；其他数量两列等宽；只有一个视图时占满。隐藏某个视图后，剩余视图按新数量重新分配空间；主窗口变化时也会重新计算。
- 每个平铺视图外面由工作区画标题栏和关闭按钮，内容放在可滚动区域；`MinimumSize` 决定内容最小宽度，不够时横向滚动。
- `InitiallyVisible == false` 的视图作为独立悬浮窗口打开，初始大小取 `MinimumSize`（没有时为 800×560）乘当前缩放，并限制在工作区内；可以拖动、缩放和关闭。默认工作区给悬浮窗口设置 `NoDocking`，不会把它们停靠到其他窗口。
- 菜单栏「功能面板」列出所有视图并显示当前开关；「开始自动排座」命令通过发布 `UiViewOpenRequested` 打开默认隐藏的排座结果视图。
- 视图的显示/隐藏选择在本次运行内按工作区实例保留，重启后回到 `InitiallyVisible`。

`builtins/pokeseats.defaultpayload` 提供四个视图：`pokeseats.defaultpayload.roster`（学生名单）、`pokeseats.defaultpayload.layout`（教室布局）、`pokeseats.defaultpayload.rules`（规则编辑）、`pokeseats.defaultpayload.seating-result`（排座结果，默认隐藏）。

`builtins/pokeseats.betapayload` 是用于承载 beta 功能的内置插件，入口为 `PokeSeats.UI.Betapayload.BetapayloadPlugin`。构建和发布宿主时会自动携带；目前不注册视图、命令或工作区，不改变现有界面。后续实验功能在该插件的 `Initialize` 中通过现有契约注册，自己的数据使用 `data/plugins/pokeseats.betapayload/`。当前只引用插件契约，没有声明其他插件依赖；需要复用其他插件的类型时，应同时添加项目引用和清单依赖。

## 限制与常见错误

- **不能热重载。** 插件加载上下文不可回收；安装、更新、删除插件后必须重启。运行中也不能通过注册接口增删贡献。
- **没有崩溃隔离。** 插件与宿主同进程，未处理异常会让程序退出。绘制代码应避免抛异常，尤其是 `Draw`/`DrawContent`。
- **不要接管宿主循环。** 不调用 `ImGui.NewFrame`、`ImGui.EndFrame`、渲染器的 `Render`，不创建或销毁 ImGui 上下文、字体图集和宿主纹理。
- **视图只画内容。** 不要在 `DrawContent` 里自行 `Begin` 一个同名窗口；窗口由工作区管理，重复开窗会破坏 ImGui 栈和布局。
- **不要预先缩放 `MinimumSize`。** 工作区会乘一次 `UiScale.Current`。
- **不要在 `Initialize` 里创建纹理或订阅工作区级事件。** 此时没有 `IWorkspaceContext`；工作区事件只在 `Activate` 后有效。
- **命令需要工作区呈现。** 宿主不会自动把 `IUiCommand` 变成菜单项；不写工作区时，默认工作区会负责列出现有命令，但自定义工作区需要自己实现。
- **消息类型必须同源。** 复制同名消息类不会共享类型身份；声明插件依赖并引用提供者程序集。
- **API 版本必须精确匹配。** 宿主只接受 `apiVersion: 5`，不会尝试兼容旧版契约。

## 参考

- `PokeSeats.UI.TestPlugin`：可运行示例，包含停靠工作区、面板切换、命令和性能面板；不随发布包分发。
- `PokeSeats.UI.DefaultWorkspace`：默认工作区实现，含菜单、平铺/悬浮布局和主题。
- `PokeSeats.UI.DefaultPayload`：四个业务视图和 `UiViewOpenRequested` 的实际用法。
- `PokeSeats.UI.Betapayload`：beta 功能的独立插件入口，目前不提供界面贡献。
- `PokeSeats.UI.PluginContracts`：契约、`UiDesignTokens`、`UiScale` 和 API 版本定义。
