namespace OpenPin.Core.Actions;

/// <summary>Stable ids of global actions (used in settings.json hotkey bindings, tray menu and command line).</summary>
public static class ActionIds
{
    public const string Capture = "capture";                         // 截图 (select + edit)
    public const string CaptureCustom = "capture.custom";            // 自定义区域/延时截图
    public const string CaptureCopy = "capture.copy";                // 截图并复制
    public const string CapturePin = "capture.pin";                  // 截图并贴图
    public const string CaptureOcr = "capture.ocr";                  // 截图并 OCR 复制
    public const string CaptureSave = "capture.save";                // 截图并保存
    public const string CaptureQuickSave = "capture.quicksave";      // 截图并快速保存
    public const string CaptureFullScreenCopy = "capture.fullscreen.copy"; // 截取屏幕并复制
    public const string CaptureLastRegionCopy = "capture.lastregion.copy"; // 截取上次区域并复制
    public const string CaptureLong = "capture.long";                // 长截图
    public const string CaptureRecord = "capture.record";            // 屏幕录制
    public const string TranslateMirror = "translate.mirror";        // 透视魔镜

    public const string PinClipboard = "pin.clipboard";              // 贴图 (from clipboard)
    public const string PinFromFile = "pin.fromfile";                // 从文件贴图
    public const string PinRestore = "pin.restore";                  // 恢复上次关闭的贴图
    public const string PinCloseAll = "pin.closeall";                // 关闭所有贴图
    public const string PinDestroyAll = "pin.destroyall";            // 销毁所有贴图
    public const string PinToggleHideAll = "pin.togglehideall";      // 隐藏/显示所有贴图
    public const string PinToggleClickThrough = "pin.toggleclickthrough"; // 开启/关闭鼠标穿透
    public const string PinNextGroup = "pin.nextgroup";              // 切换贴图组

    public const string ToggleHotkeys = "app.togglehotkeys";         // 禁用/启用快捷键
    public const string OpenSettings = "app.settings";               // 设置
    public const string Exit = "app.exit";                           // 退出
}
