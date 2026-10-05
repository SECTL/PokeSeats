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
  "apiVersion": 1,
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

`fonts` 是可选的。每个请求指定一个字族、一个 8 到 128 之间的 `sizePixels` 值，以及一个字形范围（`Default`、`ChineseSimplifiedCommon`、`ChineseFull`、`Cyrillic`、`Greek`、`Japanese`、`Korean`、`Thai` 或 `Vietnamese`）。请求仅从通过宿主验证的清单中读取；宿主在构建 ImGui 字体图集之前解析它们。`HarmonyOS Sans SC` 已嵌入宿主，不需要系统安装。其他字族在 Windows、Linux 和 macOS 的常用系统字体目录中解析，优先选择 Regular 字形。缺失的字体会记录日志并跳过，不会阻止插件启动。请求按字族去重，第一个声明的字族拥有该字族，第一个成功加载的请求会成为默认的 ImGui 字体。宿主拥有图集和字体纹理的生命周期，因此插件不得自行调用 `AddFontFromFileTTF`。

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

## 内置插件与构建

`PokeSeats.UI.DefaultPlugin` 提供默认停靠工作区、通用面板切换菜单、贡献命令和性能面板。它作为单独的 DLL 构建，并在 `dotnet build` 和 `dotnet publish` 期间部署到 `builtins/pokeseats.default`；可执行文件在运行时不会引用其实现。内置插件与外部插件使用完全相同的清单和注册契约。

```text
dotnet build PokeSeats.sln
dotnet run --project PokeSeats.UI
dotnet publish PokeSeats.UI -c Release
```

这次切换将停靠和帧时间 UI 移出了宿主，增加了运行时 DLL 发现和归属贡献注册，并引入了带有记忆选择的完整工作区替换。
