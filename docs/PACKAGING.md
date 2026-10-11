# 打包与发布

OpenPin 有三种发布形式，都由 GitHub Actions 在推送 `v*` 标签时自动构建（[`.github/workflows/release.yml`](../.github/workflows/release.yml)），产物上传到 Release 草稿：

| 文件 | 说明 |
|---|---|
| `OpenPin-x.y.z-win-x64.zip` | 体积小，需要已安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |
| `OpenPin-x.y.z-win-x64-selfcontained.zip` | 自包含单文件，解压即用 |
| `OpenPin-x.y.z-win-x64.msix` | MSIX 安装包（可上架微软商店） |

便携模式：在 `OpenPin.exe` 同目录放一个名为 `portable` 的空文件，数据会保存在 `.\data`。

## 发布新版本

```bash
git tag v0.1.0
git push origin v0.1.0
```

然后到 GitHub 的 Releases 页面检查草稿并发布。

## MSIX

打包脚本：[`packaging/msix/build-msix.ps1`](../packaging/msix/build-msix.ps1)，清单模板：[`packaging/msix/AppxManifest.xml`](../packaging/msix/AppxManifest.xml)，图标：`packaging/msix/Assets/`（由 `make-assets.ps1` 生成）。

本地打包需要安装 [Windows SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/)（提供 `makeappx.exe` / `makepri.exe` / `signtool.exe`）：

```bash
powershell -ExecutionPolicy Bypass -File packaging/msix/build-msix.ps1 -Version 0.1.0
```

### 上架微软商店

1. 在 [Partner Center](https://partner.microsoft.com/dashboard) 保留应用名称。
2. 在「产品标识」页面找到 **Package/Identity/Name**、**Package/Identity/Publisher**、**Package/Properties/PublisherDisplayName**。
3. 在 GitHub 仓库 Settings → Secrets and variables → Actions → **Variables** 中设置
   `MSIX_IDENTITY_NAME`、`MSIX_PUBLISHER`、`MSIX_PUBLISHER_DISPLAY_NAME`。
4. 推送标签，把生成的 `.msix` 上传到商店提交（商店会重新签名，无需证书）。

### 直接安装（不经过商店）

Windows 只允许安装已签名的 MSIX。可在仓库 **Secrets** 中提供代码签名证书：
`MSIX_CERT_BASE64`（.pfx 文件的 base64）和 `MSIX_CERT_PASSWORD`，证书主题必须与 `MSIX_PUBLISHER` 一致。
自签名证书需要用户先把证书导入「受信任的根证书颁发机构」，一般更推荐商店或 zip 版本。

### 打包版本的差异

- 开机自启使用 MSIX 的 `StartupTask`（`OpenPinStartup`），而不是注册表 Run 键（见 `Core/AutoStart.cs`）。
- `%AppData%\OpenPin` 的读写由系统透明重定向到包的私有目录，卸载时一并清除。
- 已声明 `microphone`、`webcam`（录屏的录音 / 摄像头画中画）和 `internetClient`（仅在用户配置翻译服务后使用）。
