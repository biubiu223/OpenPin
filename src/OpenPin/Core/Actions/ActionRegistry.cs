namespace OpenPin.Core.Actions;

public sealed record AppAction(string Id, string NameKey, Action Execute)
{
    public string DisplayName => Loc.Get(NameKey);
}

/// <summary>
/// Central table of global actions. Hotkeys, tray menu and command line all go through <see cref="Execute"/>.
/// If a modal session (capture overlay, long capture, recorder…) is active it gets the first chance to handle
/// the action — e.g. pressing the "pin" hotkey while the overlay is open pins the current selection.
/// </summary>
public sealed class ActionRegistry
{
    private readonly Dictionary<string, AppAction> _actions = new();

    public IReadOnlyCollection<AppAction> All => _actions.Values;

    public void Register(string id, string nameKey, Action execute) => _actions[id] = new AppAction(id, nameKey, execute);

    public AppAction? Get(string id) => _actions.GetValueOrDefault(id);

    public bool Execute(string id)
    {
        try
        {
            if (AppServices.ActiveSession?.TryHandleAction(id) == true) return true;
            if (!_actions.TryGetValue(id, out var action))
            {
                Log.Warn($"Unknown action '{id}'");
                return false;
            }
            action.Execute();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Action '{id}' failed", ex);
            Services.Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
            return false;
        }
    }
}
