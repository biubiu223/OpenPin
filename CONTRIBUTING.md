# 参与贡献

欢迎提交 Issue 和 Pull Request！OpenPin 是一个完全免费的开源项目。

## 环境

- Windows 10 1809+ / Windows 11（x64）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 任意编辑器：Visual Studio 2022、Rider 或 VS Code（C# Dev Kit）

## 构建与运行

```bash
git clone https://github.com/<owner>/OpenPin.git
cd OpenPin
dotnet build
dotnet test
dotnet run --project src/OpenPin
```

## 通过命令行触发功能

程序是单实例的，再次启动会把参数转发给正在运行的实例：

```bash
OpenPin.exe --action capture         # 截图
OpenPin.exe --action pin.clipboard   # 贴出剪贴板内容
OpenPin.exe --action app.exit        # 退出
```

全部动作 id 见 [`src/OpenPin/Core/Actions/ActionIds.cs`](src/OpenPin/Core/Actions/ActionIds.cs)。

## 同时运行多个开发版本

| 环境变量 | 作用 |
|---|---|
| `OPENPIN_INSTANCE=<名字>` | 使用独立的单实例锁 / 命名管道，可与正式版同时运行 |
| `OPENPIN_DATA_DIR=<目录>` | 使用独立的数据目录（设置、贴图、日志） |
| `OPENPIN_NO_HOTKEYS=1` | 不注册全局快捷键（避免和正在使用的版本抢快捷键） |

## 提交规范

- 先阅读 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)（坐标系 / DPI 约定、模块边界）和 [docs/SPEC.md](docs/SPEC.md)（需求编号）。
- Commit 信息使用 [Conventional Commits](https://www.conventionalcommits.org/)：`feat(pin): 支持贴图组`、`fix(capture): …`。
- PR 描述中注明实现的需求编号（如 `PIN-11`）。
- 确保 `dotnet build` 无错误、`dotnet test` 全部通过。
- 新增第三方依赖必须是 MIT / Apache-2.0 / BSD 等宽松许可，并在 PR 中说明理由。
