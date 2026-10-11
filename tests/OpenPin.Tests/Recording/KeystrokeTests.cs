using OpenPin.Recording.Codec;

namespace OpenPin.Tests.Recording;

public class KeystrokeTests
{
    private const int Ctrl = Keystrokes.VK_LCONTROL, Shift = Keystrokes.VK_LSHIFT, C = 0x43, A = 0x41, B = 0x42;

    private static KeyEvent D(double t, int vk) => new(t, vk, true);
    private static KeyEvent U(double t, int vk) => new(t, vk, false);

    [Fact]
    public void Combo_ShowsModifiersPlusKey_AndHoldsAfterRelease()
    {
        var ev = new[] { D(0, Ctrl), D(100, C), D(150, C) /* repeat */, U(200, C), U(300, Ctrl) };
        var seg = Keystrokes.BuildTimeline(ev, KeystrokeFilter.All);
        Assert.Equal(2, seg.Count);
        Assert.Equal("Ctrl", seg[0].Label);
        Assert.Equal((0.0, 100.0), (seg[0].Start, seg[0].End));
        Assert.Equal("Ctrl + C", seg[1].Label);
        Assert.Equal(100, seg[1].Start);
        Assert.Equal(200 + Keystrokes.HoldMs, seg[1].End);
    }

    [Fact]
    public void CombosOnly_IgnoresPlainTyping_AndShiftLetters()
    {
        var ev = new[] { D(0, A), U(50, A), D(100, Shift), D(120, B), U(140, B), U(160, Shift), D(500, Ctrl), D(520, C), U(540, C), U(560, Ctrl) };
        var seg = Keystrokes.BuildTimeline(ev, KeystrokeFilter.CombosOnly);
        Assert.Single(seg);
        Assert.Equal("Ctrl + C", seg[0].Label);
    }

    [Fact]
    public void NextKey_ReplacesPrevious()
    {
        var ev = new[] { D(0, A), U(50, A), D(300, B), U(350, B) };
        var seg = Keystrokes.BuildTimeline(ev, KeystrokeFilter.All);
        Assert.Equal(2, seg.Count);
        Assert.Equal(("A", 0.0, 300.0), (seg[0].Label, seg[0].Start, seg[0].End));
        Assert.Equal("B", seg[1].Label);
        Assert.Equal("A", Keystrokes.At(seg, 10)?.Label);
        Assert.Equal("B", Keystrokes.At(seg, 400)?.Label);
        Assert.Null(Keystrokes.At(seg, 350 + Keystrokes.HoldMs + 1));
    }

    [Fact]
    public void KeyNames()
    {
        Assert.Equal("A", Keystrokes.KeyName(0x41));
        Assert.Equal("F5", Keystrokes.KeyName(0x74));
        Assert.Equal("Enter", Keystrokes.KeyName(0x0D));
        Assert.Equal("↑", Keystrokes.KeyName(0x26));
        Assert.Equal("Ctrl", Keystrokes.KeyName(Ctrl));
        Assert.Equal("X", Keystrokes.KeyName(0xE2, _ => "X"));
    }
}
