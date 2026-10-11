using System.Windows.Input;
using OpenPin.Core.Hotkeys;

namespace OpenPin.Tests.Core;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+1", ModifierKeys.Control, Key.D1)]
    [InlineData("Alt+1", ModifierKeys.Alt, Key.D1)]
    [InlineData("Ctrl+Shift+A", ModifierKeys.Control | ModifierKeys.Shift, Key.A)]
    [InlineData("ctrl+`", ModifierKeys.Control, Key.Oem3)]
    [InlineData("Ctrl+~", ModifierKeys.Control, Key.Oem3)]
    [InlineData("F1", ModifierKeys.None, Key.F1)]
    [InlineData("Win+Shift+S", ModifierKeys.Windows | ModifierKeys.Shift, Key.S)]
    public void Parse(string text, ModifierKeys mods, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(mods, hk.Modifiers);
        Assert.Equal(key, hk.Key);
    }

    [Theory]
    [InlineData("Ctrl+1")]
    [InlineData("Ctrl+Alt+Shift+F12")]
    [InlineData("Ctrl+`")]
    [InlineData("PrintScreen")]
    public void RoundTrip(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hk));
        Assert.Equal(text, hk.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+NotAKey")]
    public void Invalid(string text) => Assert.False(Hotkey.TryParse(text, out _));
}
