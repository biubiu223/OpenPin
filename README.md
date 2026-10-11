<p align="center">
  <img src="assets/logo.png" width="96" alt="OpenPin logo" />
</p>

<h1 align="center">OpenPin</h1>

<p align="center">
  完全免费、开源的 Windows 截图 / 贴图工具<br/>
  <sub>Free &amp; open-source screenshot and pin-to-screen tool for Windows</sub>
</p>

<p align="center">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-MIT-blue.svg" alt="MIT License" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6" alt="Windows" />
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4" alt=".NET 8" />
</p>

---

> 🚧 **开发中**（v0.1 尚未发布）。功能路线图见 [docs/SPEC.md](docs/SPEC.md)。

## 特性

- **截图**：自动识别窗口、像素级选区调整、放大镜取色、截图历史
- **贴图**：把截图 / 剪贴板里的图片、文字、文件、颜色贴在屏幕上；缩放、透明度、锁定、鼠标穿透、贴图分组
- **标注**：矩形 / 椭圆、直线 / 折线、箭头、序号、铅笔、荧光笔、马赛克 / 模糊、文字、橡皮擦、聚光灯、水印、放大镜
- **文字识别（OCR）**：基于 Windows 自带离线 OCR，不上传任何数据
- **二维码识别**
- **长截图**、**屏幕录制**（GIF / MP4）
- 所有快捷键可自定义，默认快捷键与 PixPin 相同：截图 `Ctrl+1`，贴图 `Ctrl+2`
- 100% 免费：没有会员、没有账号、没有广告、不联网

## 安装

发布后可在 [Releases](../../releases) 页面下载。

便携模式：在 `OpenPin.exe` 同目录创建一个名为 `portable` 的空文件，所有数据会保存在程序目录下的 `data` 文件夹。

## 从源码构建

需要 Windows 10 1809+ 和 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)：

```bash
dotnet build
dotnet run --project src/OpenPin
```

更多信息见 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 致谢

功能设计参考了 [PixPin](https://pixpin.cn/)、[Snipaste](https://www.snipaste.com/) 等优秀的截图工具。OpenPin 是独立实现的开源项目，与它们没有任何关联。

第三方组件：

- [Hardcodet.NotifyIcon.Wpf](https://github.com/hardcodet/wpf-notifyicon) — MIT
- [ZXing.Net](https://github.com/micjahn/ZXing.Net) — Apache-2.0

## 许可

[MIT](LICENSE)
