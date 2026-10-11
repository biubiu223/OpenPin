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
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
</p>

---

> 🚧 **开发中**（v0.1 尚未发布）。功能路线图见 [docs/SPEC.md](docs/SPEC.md)。

## 特性

- **截图**：自动识别窗口和界面元素、像素级选区微调、放大镜取色、圆角 / 阴影 / 边框、截图历史、自定义区域与延时截图
- **贴图**：把截图或剪贴板里的图片、文字、文件、颜色贴在屏幕上；缩放、透明度、锁定、鼠标穿透、缩略图、旋转 / 翻转 / 灰度、裁切、贴图分组、重启后自动恢复
- **标注**：矩形 / 椭圆、直线 / 折线、箭头、序号、铅笔、荧光笔、马赛克 / 模糊 / **智能擦除**、文字、橡皮擦、聚光灯、水印、放大镜，可撤销、可二次编辑
- **文字识别（OCR）**：基于 Windows 自带的离线 OCR，多语言，贴图上的文字可直接选中复制
- **翻译**：图片 / 文字翻译，支持 OpenAI 兼容接口（DeepSeek、通义千问、Kimi、智谱、Ollama…）、百度、有道、DeepL（需自备 API Key）
- **表格识别**：图片表格 → Excel / CSV
- **二维码 / 条码识别**：QR、DataMatrix、PDF417、Code128、EAN 等
- **长截图**：滚动自动拼接，横向 / 纵向，自动滚动，反向滚动自动裁剪
- **屏幕录制**：GIF / MP4，回放剪辑，鼠标点击动画、按键显示、麦克风 / 系统声音、摄像头画中画
- **保存**：PNG / JPG / BMP / TIFF / PDF，实时预览与文件大小估算，文件名模板，快速保存，自动保存
- Windows 11 风格界面，所有快捷键可自定义，默认快捷键与 PixPin 相同：截图 `Ctrl+1`，贴图 `Ctrl+2`，恢复贴图 `Ctrl+3`
- **100% 免费**：PixPin 的会员功能在这里全部免费；没有账号、没有广告、没有遥测。除非你自己配置了翻译服务，否则程序不会联网

## 安装

可在 [Releases](../../releases) 页面下载：

- `OpenPin-x.y.z-win-x64-selfcontained.zip`：解压即用
- `OpenPin-x.y.z-win-x64.zip`：体积更小，需要 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)
- `OpenPin-x.y.z-win-x64.msix`：安装包

便携模式：在 `OpenPin.exe` 同目录创建一个名为 `portable` 的空文件，所有数据会保存在程序目录下的 `data` 文件夹。打包与发布说明见 [docs/PACKAGING.md](docs/PACKAGING.md)。

## 从源码构建

需要 Windows 10 1809+ 和 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)：

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
