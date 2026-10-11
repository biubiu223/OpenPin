using System.Windows;

namespace OpenPin.Core;

/// <summary>
/// Localization. Strings live in <c>Resources/Strings/zh-CN/&lt;Module&gt;.xaml</c> as <c>sys:String</c> resources
/// keyed <c>Module.Name</c> (e.g. <c>Pin.Close</c>). XAML: <c>{DynamicResource Pin.Close}</c>; code: <c>Loc.Get("Pin.Close")</c>.
/// A future language just provides the same keys in <c>Resources/Strings/&lt;culture&gt;/</c>.
/// </summary>
public static class Loc
{
    public static string Get(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;

    public static string Format(string key, params object?[] args)
    {
        try { return string.Format(Get(key), args); }
        catch (FormatException) { return Get(key); }
    }
}
