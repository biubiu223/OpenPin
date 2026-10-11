# OpenPin 架构说明

## 技术栈

- **C# / WPF**，目标框架 `net10.0-windows10.0.19041.0`（可直接调用 WinRT：`Windows.Media.Ocr`、`Windows.Media.Transcoding` 等）。**计划升级到 .NET 10（LTS）**，见 SPEC PKG-01。
- 为什么用 WPF 而不是 WinUI 3：截图遮罩、贴图、浮动工具栏都是大量**透明、置顶、无边框**的小窗口，WPF 对分层透明窗口支持成熟、开销小；WinUI 3 不正式支持透明窗口。外观上统一做成 **Windows 11 Fluent 风格**（SPEC UI-01），发布用 **MSIX** + 便携 zip（PKG-02/03）。
- 最低系统：Windows 10 1809（`SupportedOSPlatformVersion 10.0.17763.0`），x64。
- 第三方依赖尽量少，且必须是宽松开源许可（MIT / Apache-2.0 / BSD）：
  - `Hardcodet.NotifyIcon.Wpf`（MIT）— 托盘图标
  - `ZXing.Net`（Apache-2.0）— 二维码
- 图标字体使用系统自带的 *Segoe Fluent Icons*（Win11）/ *Segoe MDL2 Assets*（Win10），不打包图片资源。

## 目录结构

```
OpenPin.sln
src/OpenPin/                 # 唯一的应用工程（WinExe）
  App.xaml(.cs)              # 组合根：启动顺序、动作注册、命令行
  app.manifest               # Per-Monitor V2 DPI 感知
  Core/                      # 基础设施（项目主管维护）
    Contracts.cs             # ★ 跨模块接口：ICaptureController / IPinManager / ... / PinContent
    AppServices.cs           # 服务定位器
    Actions/                 # ActionIds（动作 id）+ ActionRegistry
    Hotkeys/                 # Hotkey（解析/格式化）+ HotkeyService（RegisterHotKey）
    Settings/                # AppSettings 根 + SettingsService（JSON）
    Screen/                  # ScreenCapture、ScreenSnapshot、Monitors、WindowEnumerator
    Imaging/                 # BitmapUtil、ColorText
    Native/                  # NativeMethods（Win32）、WindowHelper（物理像素窗口定位）
    Tray/                    # 托盘
    AppPaths.cs Log.cs Loc.cs SingleInstance.cs RelayCommand.cs
  Capture/                   # 截图遮罩、选区、放大镜、窗口识别、历史
  Annotation/                # 标注引擎 + 标注工具栏（截图和贴图共用）
  Pin/                       # 贴图窗口、贴图组、历史、持久化
  Services/                  # 剪贴板、保存、OCR、二维码、通知（静态服务）
  SettingsUI/                # 设置窗口
  LongCapture/               # 长截图
  Recording/                 # 屏幕录制
  Resources/
    Theme.xaml               # 设计令牌（颜色/字体/圆角/按钮样式）
    Styles/<Module>.xaml     # 各模块样式
    Strings/zh-CN/<Module>.xaml  # 各模块界面文字
    AppIcon.ico
tests/OpenPin.Tests/         # xUnit；按模块分子目录 tests/OpenPin.Tests/<Module>/
docs/                        # SPEC.md（需求）、ARCHITECTURE.md（本文件）
assets/                      # README 用图片、logo
.github/workflows/           # CI
```

## 模块所有权（并行开发规则）

每个模块只修改**自己的文件**，这样多人 / 多个分支并行开发不会冲突：

| 模块 | 可以修改的文件 |
|---|---|
| Capture | `src/OpenPin/Capture/**`、`Resources/Styles/Capture.xaml`、`Resources/Strings/zh-CN/Capture.xaml`、`tests/OpenPin.Tests/Capture/**` |
| Annotation | `src/OpenPin/Annotation/**`、对应 Styles / Strings / tests |
| Pin | `src/OpenPin/Pin/**`、对应 Styles / Strings / tests |
| Services | `src/OpenPin/Services/**`、对应 Styles / Strings / tests；**可以**在 `OpenPin.csproj` 中添加 NuGet 包 |
| SettingsUI / LongCapture / Recording | 同上 |
| Core（主管） | `Core/**`、`App.xaml(.cs)`、`OpenPin.csproj`、`Resources/Theme.xaml`、`Resources/Strings/zh-CN/Common.xaml`、docs、CI |

- **不要修改** `Core/`、`App.xaml(.cs)`、`Contracts.cs`、别的模块的文件。需要改契约或 Core 时，在交付报告里写「契约变更请求」，由主管统一修改。
- 需要的小工具函数（Win32 声明、图像算法）放在自己模块里，类设为 `internal`。
- 模块的设置类（如 `Capture/CaptureSettings.cs`）归模块所有，可以自由增加属性（必须有默认值）。

## 坐标系与 DPI（最重要）

进程是 **Per-Monitor V2 DPI 感知**。约定：

1. **屏幕坐标一律用物理像素**，变量 / 属性名以 `Px` 结尾（`Int32Rect selectionPx`）。Win32 API 在 PMv2 下返回的就是物理像素。
2. 位图统一为 **96 DPI**（`BitmapUtil.Normalize96Dpi`），这样「1 图像像素 = 1 WPF 单位」。
3. 屏幕锚定的窗口（截图遮罩、贴图、浮动工具栏）用 `WindowHelper.SetBoundsPx / SetPositionPx` 按物理像素定位，不要用 `Window.Left/Top`（多显示器不同 DPI 时有歧义）。
4. 截图遮罩：**一个**窗口覆盖整个虚拟屏幕。根内容挂一个 `ScaleTransform(1/dpiScale)` 的「像素层」，这样层内 1 单位 = 1 物理像素，快照图像 1:1 显示、选区直接用像素坐标；工具栏等 UI 放在不缩放的层里保持正常大小。
5. 贴图：缩放 100% 时 1 图像像素 = 1 物理像素；收到 `DpiChanged` 时重新计算窗口大小，保持物理尺寸不变。

`ScreenSnapshot` 提供 `ScreenToImage / ImageToScreen` 在「屏幕物理像素」与「快照图像像素」之间换算（多显示器时虚拟屏幕原点可能是负数）。

## 模块之间如何协作

```
热键 / 托盘 / 命令行 ──► ActionRegistry.Execute(id)
                           │  ① AppServices.ActiveSession?.TryHandleAction(id)（截图界面打开时优先）
                           ▼  ② 注册的处理函数
   ICaptureController ──(选区图像)──► IPinManager.PinImage(image, rectPx)
         │                         ├─► ClipboardService / SaveService / OcrService / QrService / Notify
         ├─► AnnotationSurface + AnnotationToolbar（标注）
         ├─► ILongCaptureController.Start(rectPx)
         └─► IScreenRecorder.Start(rectPx)
   IPinManager ──► AnnotationSurface + AnnotationToolbar、Services
   Tray ──► IPinManager.PopulateTrayMenu(items)
```

- 全局快捷键被 `RegisterHotKey` 占用后，**本程序自己的窗口也收不到这个按键**。所以截图界面里「按贴图快捷键贴出选区」要通过实现 `IModalSession.TryHandleAction` 完成；会话打开时设置 `AppServices.ActiveSession = this`，关闭时清空。
- 窗口内的局部快捷键（`Ctrl+C`、`Space`、`R`…）在窗口自己的 `KeyDown` 里处理。

### 标注契约（Annotation ↔ Capture / Pin）

`Annotation/AnnotationSurface.cs` 里的公共 API 是契约（Phase 0 的桩代码已定义好签名）：

- `new AnnotationSurface(BitmapSource baseImage)`：Surface 自己把 `Width/Height` 设为图像像素尺寸；**1 单位 = 1 图像像素**。宿主在父元素上做缩放变换，鼠标坐标自然就是图像像素。
- Surface **只画标注**（透明背景），底图由宿主绘制；马赛克 / 模糊 / 聚光灯 / 放大镜从 `BaseImage` 取像素。
- `Tool == None` 时 Surface 不参与命中测试，鼠标交给宿主（移动选区、拖动贴图）。
- `EditBounds`：限制绘制范围（截图时 = 选区）。`SnapRectProvider`：右键贴合元素。
- `HandleKeyDown(e)`：宿主先把按键交给 Surface，返回 true 表示已处理。`IsBusy` 为 true 时宿主不要把 Esc/Enter 当成关闭/复制。
- `Render(cropPx)`：底图 + 标注合成（Bgra32、96 DPI），与屏幕显示逐像素一致。
- `AnnotationToolbar.Surface` 绑定 Surface；`HostActions` 是宿主追加在右侧的按钮（贴图、保存、复制…）。宿主只负责摆放位置。

## 编码约定

- 可空引用类型开启；构建 **0 错误**，尽量 0 警告。
- 界面文字全部走资源：XAML `{DynamicResource Pin.Close}`，代码 `Loc.Get("Pin.Close")`；key 以模块名开头。
- 颜色 / 字体 / 圆角使用 `Theme.xaml` 里的令牌（`Brush.Accent`、`Font.Icon`、`Style.IconButton`、`Style.ToolbarBorder`…）。
- UI 线程之外不碰 WPF 对象；耗时操作 `Task.Run`，位图要 `Freeze()` 后再跨线程。
- 出错写日志：`Log.Error("…", ex)`；面向用户的失败用 `Notify.Error`。不允许静默吞异常。
- 新功能尽量附带单元测试（纯逻辑：拼接算法、路径模板、颜色解析、OCR 文本后处理…）。
- 注释用英文，文档用中文。

## 开发 / 调试

见 [CONTRIBUTING.md](../CONTRIBUTING.md)。常用：

```bash
dotnet build
dotnet test
src/OpenPin/bin/Debug/net10.0-windows10.0.19041.0/OpenPin.exe --action capture
```

开发用环境变量：`OPENPIN_INSTANCE`（独立单实例名）、`OPENPIN_DATA_DIR`（独立数据目录）、`OPENPIN_NO_HOTKEYS=1`（不注册全局快捷键）。
