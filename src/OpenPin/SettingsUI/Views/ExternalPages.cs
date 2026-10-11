using System.Windows;
using System.Windows.Controls;

namespace OpenPin.SettingsUI.Views;

/// <summary>
/// Pages whose settings UI is built by other modules. INTEGRATION POINT for the project lead: replace the placeholder
/// in <see cref="CreateTranslateContent"/> / <see cref="CreateRecognitionContent"/> with the module's view, e.g.
/// <code>
/// return new OpenPin.Translate.TranslateSettingsView { DataContext = session.Draft.Translate };
/// </code>
/// Bind to <c>session.Draft.&lt;Section&gt;</c> (the draft, applied by 确定/应用); if the view writes to properties without
/// WPF bindings (NotifyOnSourceUpdated), call <c>session.NotifyEdited()</c> so the Apply button lights up.
/// The draft object is replaced on import / reset / apply — subscribe to <c>session.DraftReplaced</c> to re-bind.
/// </summary>
internal static class ExternalPages
{
    /// <summary>Content of the "翻译" page (id "translate").</summary>
    public static FrameworkElement CreateTranslateContent(SettingsSession session, SettingsWindow window) =>
        Host(session, () =>
        {
            var view = new OpenPin.Translate.TranslateSettingsView(session.Draft.Translate);
            view.Changed += (_, _) => session.NotifyEdited();
            UseWindowControlStyles(view, window);
            return view;
        });

    /// <summary>Content of the "识别" page (id "recognition": table / formula recognition).</summary>
    public static FrameworkElement CreateRecognitionContent(SettingsSession session, SettingsWindow window) =>
        Host(session, () => Placeholder("SettingsUI.Recognition.Placeholder"));

    /// <summary>A ContentControl whose content is re-created whenever the draft object is replaced.</summary>
    private static ContentControl Host(SettingsSession session, Func<FrameworkElement> create)
    {
        var host = new ContentControl { Focusable = false, Content = create() };
        EventHandler handler = (_, _) => host.Content = create();
        host.Loaded += (_, _) => { session.DraftReplaced -= handler; session.DraftReplaced += handler; };
        host.Unloaded += (_, _) => session.DraftReplaced -= handler;
        return host;
    }

    /// <summary>
    /// Embedded module views style their controls with their own keyed styles (made for standalone popups).
    /// Inside the settings window we want the window's Fluent controls instead, so map those keys to the
    /// Fluent implicit styles available here.
    /// </summary>
    private static void UseWindowControlStyles(FrameworkElement view, FrameworkElement window)
    {
        var map = new (string Key, Type Control)[]
        {
            ("Style.Translate.ComboBox", typeof(ComboBox)),
            ("Style.Translate.TextBox", typeof(TextBox)),
            ("Style.Translate.TextArea", typeof(TextBox)),
            ("Style.Translate.PasswordBox", typeof(PasswordBox)),
            ("Style.Translate.RadioButton", typeof(RadioButton)),
            ("Style.Translate.Button", typeof(Button)),
        };
        foreach (var (key, type) in map)
            if (window.TryFindResource(type) is Style fluent)
                view.Resources[key] = fluent;
    }

    private static FrameworkElement Placeholder(string key) =>
        Kit.Card(Logic.SettingsCatalog.Icons.Info, key, null, null);
}
