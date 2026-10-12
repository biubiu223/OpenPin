using System.Threading;
using System.Windows;
using OpenPin.Core.Actions;
using OpenPin.Translate;

namespace OpenPin.Tests.Translate;

public class MirrorWindowTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException("STA action failed: " + error);
    }

    [Fact]
    public void ActionId_TranslateMirror_IsDefined()
    {
        Assert.Equal("translate.mirror", ActionIds.TranslateMirror);
    }

    [Fact]
    public void MirrorWindow_Instantiation_SetsBasicProperties()
    {
        RunSta(() =>
        {
            var bounds = new Int32Rect(50, 60, 400, 300);
            var win = new MirrorWindow(bounds, "en", "zh-CN");
            try
            {
                Assert.Equal("DeskMirror", win.Title);
                Assert.True(win.AllowsTransparency);
                Assert.True(win.Topmost);
                Assert.False(win.ShowInTaskbar);
                Assert.Equal(WindowStyle.None, win.WindowStyle);

                // Toggle click through
                win.ToggleClickThrough();
                win.ToggleClickThrough();
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void ShowMirror_CanBeCalledWithoutThrowing()
    {
        RunSta(() =>
        {
            var win = MirrorWindow.ShowMirror(new Int32Rect(100, 100, 320, 240));
            try
            {
                Assert.NotNull(win);
                Assert.Equal("DeskMirror", win.Title);
            }
            finally
            {
                win.Close();
            }
        });
    }

    [Fact]
    public void TranslateWorkflow_OpenMirror_DoesNotThrow()
    {
        RunSta(() =>
        {
            TranslateWorkflow.OpenMirror(new Int32Rect(20, 20, 300, 200));
            var active = MirrorWindow.ActiveInstance;
            try
            {
                Assert.NotNull(active);
            }
            finally
            {
                active?.Close();
            }
        });
    }
}
