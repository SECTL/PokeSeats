# PokeSeats Beta 功能

本文件集中记录 Betapayload 插件及 Beta 工作区的使用方式、实现约定与变更。Beta 功能与正式功能分开维护，原有标准工作区仍然可用。

## 标签向导工作区

### 启用方式

按 `Ctrl + Alt + Shift + X` 打开工作区选择器，选择「标签向导工作区（Beta）」。宿主会记住工作区选择；需要恢复平铺界面时，在同一选择器中切回「标准工作区」。

### 操作流程

界面按以下顺序组织为四个不可关闭的步骤标签，每次只展示当前页面：

1. **学生名单**：导入或编辑名单和学生属性。
2. **教室布局**：设置行列，绘制课桌、讲台与走道。
3. **规则与目标**：配置硬性规则和优化目标，准备好后开始自动排座。
4. **排座结果**：查看排座进度、结果与导出选项。

可以点击标签自由返回修改，也可以使用底部的「上一步」「下一步」。规则页和结果页提供「开始自动排座」按钮，菜单中的同名命令仍可使用。开始排座后自动切到结果标签；检测到冲突规则时自动返回规则标签。其他插件贡献的视图追加为标签，菜单中的视图项用于选择页面。

内容遵循业务视图的最小宽度，空间不足时可横向滚动。当前标签在本次运行的工作区实例内保留。

### 数据与外观

Beta 工作区复用已有业务视图和排座数据，不复制或重置名单、布局、规则与目标。菜单、外观设置和关于窗口复用标准工作区实现；Beta 外观设置单独保存到 `data/plugins/pokeseats.betapayload/workspace-settings.json`。

## GitHub Releases 自动更新

Betapayload 加载后会在后台检查 GitHub Releases，不需要切换到 Beta 工作区。发现新版本时自动打开「软件更新（Beta）」；也可从菜单手动打开并重新检查。检查失败不会阻塞排座。

更新通道可选**稳定版**（默认）或**预发布版**：

- 稳定版只读取 `/releases/latest`，因此 GitHub 上标记为 Pre-release 的版本永远不会被提供。
- 预发布版读取 `/releases` 列表并取版本号最高的一个，标记为 Pre-release 的版本也参与比较；草稿始终排除。
- 两项都按语义化版本比较，`1.2.0-beta.1` 高于 `1.1.9`、低于 `1.2.0`；标签与预发布标识任一体现预发布，稳定版通道都会跳过。
- 切换通道会丢弃已下载的更新包并立即重新检查；下载中或正在启动安装助手时不允许切换。
- 选择保存在 `data/plugins/pokeseats.betapayload/update-settings.json`，下次启动沿用。

版本按语义化版本比较，支持 `1.2.0`、`v1.2.0` 标签，忽略构建元数据，不安装草稿、同版本或旧版本。当前版本来自宿主程序集的 informational version，统一由 `Directory.Build.props` 提供。

更新操作分两步确认：

1. **下载更新**：展示 Release 说明，下载当前系统对应的 ZIP，显示进度；核对 GitHub 提供的文件大小和 SHA-256，再检查压缩包路径与必要文件，在临时目录准备安装。缺少摘要、发布包不完整或校验失败时不会安装。
2. **安装并重启**：先保存所需的座位表或排座数据。确认后请求宿主正常退出，外部更新助手等待进程结束，再备份并覆盖发布包中的文件，随后重新启动。复制失败时尝试回滚并启动旧版本；不会在后台检查时自行退出程序。

自动安装支持 Windows x64 与 Linux x64，安装目录必须可写；Windows 使用系统 PowerShell，Linux 使用 `/bin/sh`。程序根目录包含 `.csproj` 或 `.sln` 时拒绝原地更新。Windows 的实际替换、重启与失败回滚已验证；Linux 更新助手尚未做目标系统运行验证。

发布资产命名沿用 `PokeSeats-{版本}-win-x64.zip` / `PokeSeats-{版本}-linux-x64.zip`，带 `v` 的标签也支持资产版本不带 `v`。ZIP 可以是平铺文件，或由一个 `PokeSeats*` 顶层目录包裹，必须包含宿主及默认插件清单。下载来自 GitHub 官方 Release，不使用镜像或第三方接口。

本地 `data/`、第三方 `plugins/`、根目录配置和布局文件不参与覆盖，发布包若包含这些内容会被拒绝。发布包未包含的已有文件不主动删除。成功后清理暂存包与备份；错误时保留诊断材料。更新日志与结果位于系统临时目录下 `PokeSeats-update-*/package/update-helper/`（特殊权限情况下使用独立临时目录），结果见 `result.txt`，详细过程见 `update.log`。

## 插件与实现约定

- 项目：`PokeSeats.UI.Betapayload`。
- 插件 ID：`pokeseats.betapayload`。
- 插件入口：`PokeSeats.UI.Betapayload.BetapayloadPlugin`。
- 工作区 ID：`pokeseats.betapayload.workspace`。
- 分发目录：`builtins/pokeseats.betapayload/`，随宿主构建和发布自动携带。
- 清单依赖：`pokeseats.default` 和 `pokeseats.defaultpayload`。

`BetaWorkspace` 继承 `DefaultWorkspace`，项目引用默认工作区程序集，加载时由依赖插件共享其类型。默认工作区提供 `DrawViews(IWorkspaceContext, float)`、`IsViewVisible`、`SetViewVisible` 和 `OnViewOpenRequested` 四个受保护的虚方法作为扩展点；标准工作区仍使用原有平铺及悬浮布局。

Beta 工作区仅重写视图呈现与打开请求的处理，通过现有 `UiViewOpenRequested` 事件在下一帧选择对应标签，用于展示排座进度和定位冲突规则。不另建业务数据副本或求解流程。

后续 Beta 功能在 `BetapayloadPlugin.Initialize` 中通过现有插件契约注册；插件自己的数据使用 `data/plugins/pokeseats.betapayload/`。通用插件契约见 [插件开发说明](PokeSeats.UI/PLUGIN.md)。

## Beta 变更记录

### 2026-10-22

- 新增后台 GitHub Releases 正式版检查、更新说明、手动下载、进度显示及安装后重启。
- 新增按平台匹配发布包、大小与 SHA-256 校验、安全解压、退出后替换及复制失败回滚；保留本地数据、第三方插件与配置。
- 宿主退出请求延后到当前界面帧完成再关闭，避免更新按钮触发退出时提前释放 ImGui 资源。

### 2026-10-21

- 新增 Betapayload 内置插件，用于独立承载 Beta 功能。
- 新增标签向导工作区，将原有业务视图组织为四步标签，支持自由切换、前后导航、启动排座及事件驱动的页面跳转。
- 保留标准工作区，复用其菜单、外观设置和关于窗口，并支持其他插件视图作为附加标签。
