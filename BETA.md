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

### 2026-10-21

- 新增 Betapayload 内置插件，用于独立承载 Beta 功能。
- 新增标签向导工作区，将原有业务视图组织为四步标签，支持自由切换、前后导航、启动排座及事件驱动的页面跳转。
- 保留标准工作区，复用其菜单、外观设置和关于窗口，并支持其他插件视图作为附加标签。
