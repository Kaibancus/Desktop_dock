# Polaris

Polaris 是一款常驻系统托盘的 Windows 径向应用启动器（Radial Launcher）。按住触发键即可在屏幕中心弹出一个面板，快速启动你常用的应用、文件夹与系统位置，松开触发键即收起。它基于 .NET 9 + WPF 构建，强调流畅的动画与高度可定制的外观。

## 功能特性

- **径向快速启动**：按住触发键唤出面板，点击图标即可启动对应项目。
- **多主题（待更新）**：
  - **土星环（saturn）**：以行星 + 多层圆环的方式排布图标，自带光晕与旋转动画。
  - **液态玻璃（liquidglass）**：半透明的圆角「液态玻璃」面板，支持拖拽排序及相邻图标「让位」动画。
- **多种启动项**：支持普通可执行文件、快捷方式（.lnk，自动解析目标与图标）、以及 Shell 命名空间项（此电脑、回收站等）。
- **运行中应用高亮**：自动追踪正在运行的应用并以光晕标记。
- **窗口预览**：可对正在运行的应用提供窗口预览。
- **图标提取**：自动从可执行文件 / 快捷方式中提取图标。
- **全局触发键**：默认使用右 Alt（虚拟键 `0xA5`）作为「按住显示」的触发键，可在设置中修改。


## 环境要求

- Windows 10 / 11
- [.NET 9 SDK](https://dotnet.microsoft.com/)（目标框架 `net9.0-windows`，使用 WPF + Windows Forms）

## 构建与运行

```powershell
# 还原依赖并运行（Debug）
dotnet run --project Polaris.csproj

# 构建 Release 版本
dotnet build -c Release
```

### 发布（框架依赖单文件）

仓库内提供了发布脚本：

```powershell
./publish-fd.ps1
```

## 使用方法

1. 启动 Polaris 后，它会驻留在系统托盘。
2. **按住触发键**（默认右 Alt），在鼠标位置弹出环形面板。
3. 通过将快捷方式图标拖拽进/出面板来添加/删除启动项。
4. 点击图标启动对应应用 / 文件夹 / 系统位置。
5. **松开触发键**收起面板。
6. 右键点击托盘图标可打开**设置**窗口，在此添加 / 删除启动项、切换主题、调整外观与触发键、配置开机自启等。

## 配置

应用配置以 JSON 形式持久化保存。设置项涵盖：启动项列表、当前主题、面板透明度 / 颜色 / 强调色 / 字体色、图标尺寸、单环最大图标数、内环图标数、触发键、开机自启等。每个主题的透明度与图标尺寸会被单独记忆，切换主题时自动恢复。

## 项目结构

```
Polaris/
├─ App.xaml(.cs)            # 应用入口、托盘、全局热键与异常处理
├─ Polaris.csproj           # 项目文件（net9.0-windows, WPF + WinForms）
├─ app.manifest             # 应用清单
├─ publish-fd.ps1           # 框架依赖发布脚本
├─ Assets/                  # 图标等资源
├─ Interop/
│  └─ KeyboardHook.cs       # 全局键盘钩子（触发键）
├─ Models/
│  ├─ AppConfig.cs          # 根配置对象
│  ├─ AppEntry.cs           # 单个启动项模型
│  └─ AppSettings.cs        # 外观与行为设置
├─ Services/
│  ├─ ConfigStore.cs        # 配置读写
│  ├─ IconExtractor.cs      # 图标提取
│  ├─ PanelTheme.cs         # 主题定义与注册表
│  ├─ RunningAppTracker.cs  # 运行中应用追踪
│  ├─ ShellNamespace.cs     # Shell 命名空间项解析
│  ├─ ShortcutResolver.cs   # 快捷方式(.lnk)解析
│  ├─ StartupManager.cs     # 开机自启管理
│  └─ WindowPreviewService.cs # 窗口预览
└─ Views/
   ├─ RadialWindow.xaml(.cs)    # 径向面板主窗口
   ├─ RadialWindow.Saturn.cs    # 土星环主题绘制
   ├─ RadialWindow.Glass.cs     # 玻璃主题绘制
   ├─ RadialWindow.Planet.cs    # 行星 / 圆环绘制
   ├─ RadialIcon.xaml(.cs)      # 单个图标控件
   └─ SettingsWindow.xaml(.cs)  # 设置窗口
```

## 许可证

本项目暂未声明许可证。
