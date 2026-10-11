using System.Windows.Controls;
using OpenPin.Pin;

namespace OpenPin.Tests.Pin;

public class PinTrayMenuTests
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
    public void PopulateTrayMenu_BeforeInitialize_AddsPinSection()
    {
        RunSta(() =>
        {
            var mgr = new PinManager();
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "existing" });
            mgr.PopulateTrayMenu(menu.Items);

            Assert.IsType<Separator>(menu.Items[1]);
            var headers = menu.Items.OfType<MenuItem>().Select(m => m.Header as string).ToList();
            Assert.Contains("Pin.Tray.CloseAll", headers);
            Assert.Contains("Pin.Tray.DestroyAll", headers);
            Assert.Contains("Pin.Tray.BringIntoView", headers);
            Assert.Contains("Pin.Tray.SaveAll", headers);
            // groups submenu lists the default group plus next / new / manage
            var groups = menu.Items.OfType<MenuItem>().First(m => m.Items.Count > 0);
            Assert.True(groups.Items.Count >= 5);
            Assert.Equal(0, mgr.VisibleCount);
        });
    }
}
