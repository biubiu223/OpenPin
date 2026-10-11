using System.IO;
using OpenPin.Recording;

namespace OpenPin.Tests.Recording;

public class TempCleanupTests
{
    [Fact]
    public void StaleFiles_OfDeadProcesses_AreDeleted_OwnAreKept()
    {
        var root = ScreenRecorder.TempRoot;
        // pid 0x7FFFFFF0 cannot be a running process
        var stale = Path.Combine(root, $"rec-{0x7FFFFFF0}-{Guid.NewGuid():N}-screen.oprec");
        var own = Path.Combine(root, $"rec-{Environment.ProcessId}-{Guid.NewGuid():N}-screen.oprec");
        File.WriteAllText(stale, "x");
        File.WriteAllText(own, "x");
        try
        {
            ScreenRecorder.CleanupStaleTempFiles();
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(own));
        }
        finally
        {
            File.Delete(stale);
            File.Delete(own);
        }
    }
}
