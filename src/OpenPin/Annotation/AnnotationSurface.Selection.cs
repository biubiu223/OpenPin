using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using OpenPin.Annotation.Model;
using OpenPin.Annotation.Model.Items;

namespace OpenPin.Annotation;

public partial class AnnotationSurface
{
    private Guid? _selectedId;
    private bool _suppressApply;
    private int? _serialOverride;

    internal AnnotationItem? Selected => _selectedId is { } id ? _doc.Find(id) : null;

    internal void Select(AnnotationItem? item)
    {
        var id = item?.Id;
        if (_selectedId == id) return;
        _selectedId = id;
        if (item != null)
        {
            // Reflect the item's style in the toolbar (and as defaults for the next item of this tool).
            var tool = item.Tool;
            var o = Options.Get(tool);
            item.ReadOptions(o);
            _suppressApply = true;
            try { Options.Commit(tool); }
            finally { _suppressApply = false; }
        }
        RedrawOverlay();
        UpdateCursor();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Topmost item matching the current tool under p (secondary edit, ANN-15).</summary>
    private AnnotationItem? HitItem(Point p, double tol)
    {
        if (!ToolOptionsStore.Settings.AllowSecondaryEdit) return null;
        if (_editBounds is { } eb && !eb.Contains(p)) return null;
        for (int i = _doc.Items.Count - 1; i >= 0; i--)
        {
            var it = _doc.Items[i];
            if (it.MatchesTool(_tool) && it.HitTest(p, tol)) return it;
        }
        return null;
    }

    private Handle? HitHandle(AnnotationItem item, Point p)
    {
        double r = HandleRadius() + 3 / Math.Max(0.01, ScreenScale());
        Handle? best = null;
        double bestD = double.MaxValue;
        foreach (var h in item.GetHandles())
        {
            double d = (h.Position - p).Length;
            if (d <= r && d < bestD) { best = h; bestD = d; }
        }
        return best;
    }

    /// <summary>Delete the selected item (undoable).</summary>
    internal bool DeleteSelected()
    {
        var sel = Selected;
        if (sel == null) return false;
        _doc.Checkpoint();
        int firstSerial = FirstSerialValue();
        _doc.Items.Remove(sel);
        if (sel is SerialItem) RenumberSerials(firstSerial);
        Select(null);
        SyncVisuals();
        RaiseChanged();
        return true;
    }

    internal void DeleteItems(IReadOnlyCollection<Guid> ids)
    {
        if (ids.Count == 0) return;
        _doc.Checkpoint();
        bool serial = _doc.Items.Any(i => ids.Contains(i.Id) && i is SerialItem);
        int firstSerial = FirstSerialValue();
        _doc.Items.RemoveAll(i => ids.Contains(i.Id));
        if (serial) RenumberSerials(firstSerial);
        if (_selectedId is { } s && ids.Contains(s)) Select(null);
        SyncVisuals();
        RaiseChanged();
    }

    // ------------------------------------------------------------------ serial numbering (ANN-05)

    /// <summary>Value of the next serial marker. Setting it overrides the automatic "max + 1".</summary>
    internal int NextSerialNumber
    {
        get => _serialOverride ?? (_doc.Items.OfType<SerialItem>().Select(s => s.Value).DefaultIfEmpty(0).Max() + 1);
        set
        {
            _serialOverride = Math.Clamp(value, 1, 9999);
            SerialChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private int TakeSerialNumber()
    {
        int v = NextSerialNumber;
        if (_serialOverride != null) _serialOverride = v + 1;
        return v;
    }

    /// <summary>After deleting markers: renumber the remaining ones consecutively (setting, default on).</summary>
    private int FirstSerialValue() => _doc.Items.OfType<SerialItem>().Select(s => s.Value).DefaultIfEmpty(1).Min();

    private void RenumberSerials(int start)
    {
        if (!ToolOptionsStore.Settings.SerialRenumberOnDelete) { SerialChanged?.Invoke(this, EventArgs.Empty); return; }
        RenumberSerials(_doc.Items, start);
        _serialOverride = null;
        SerialChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Renumber serial items consecutively (ordered by current value) starting at <paramref name="start"/>.</summary>
    internal static void RenumberSerials(IList<AnnotationItem> items, int start)
    {
        var serials = items.OfType<SerialItem>().OrderBy(s => s.Value).ToList();
        if (serials.Count == 0) return;
        start = Math.Max(1, start);
        for (int i = 0; i < serials.Count; i++)
        {
            if (serials[i].Value != start + i)
            {
                serials[i].Value = start + i;
                serials[i].Touch();
            }
        }
    }

    // ------------------------------------------------------------------ options → selection (ANN-15 restyle)

    private void OnOptionsChanged(AnnotationTool tool)
    {
        if (_suppressApply) return;
        var o = Options.Get(tool);
        if (_editingItem != null && tool == AnnotationTool.Text)
        {
            _editingItem.ApplyOptions(o);
            _editingItem.Touch();
            UpdateEditorStyle();
            SyncVisuals();
            return;
        }
        if (tool == AnnotationTool.Watermark)
        {
            var wm = _doc.Items.OfType<WatermarkItem>().LastOrDefault();
            if (wm != null)
            {
                _doc.CheckpointCoalesced("wm");
                wm.ApplyOptions(o);
                wm.Touch();
                SyncVisuals();
                RaiseChanged();
            }
            return;
        }
        var sel = Selected;
        if (sel == null || !sel.MatchesTool(tool)) return;
        _doc.CheckpointCoalesced("opt:" + sel.Id);
        sel = Selected!; // checkpoint does not replace live items, but be explicit
        sel.ApplyOptions(o);
        sel.Touch();
        SyncVisuals();
        RedrawOverlay();
        RaiseChanged();
    }

    /// <summary>Watermark tool: make sure one watermark exists (created with the current options, undoable).</summary>
    private void EnsureWatermark()
    {
        if (_doc.Items.OfType<WatermarkItem>().Any()) return;
        _doc.Checkpoint();
        var wm = new WatermarkItem();
        wm.ApplyOptions(Options.Get(AnnotationTool.Watermark));
        wm.Timestamp = DateTime.Now;
        _doc.Items.Add(wm);
        SyncVisuals();
        RaiseChanged();
    }

    internal bool HasWatermark => _doc.Items.OfType<WatermarkItem>().Any();

    internal void RemoveWatermark()
    {
        var ids = _doc.Items.OfType<WatermarkItem>().Select(w => w.Id).ToList();
        DeleteItems(ids);
    }

    internal void AddWatermark() => EnsureWatermark();

    /// <summary>Adjust the selected serial marker's value (+/-, ANN-05 P1).</summary>
    internal void BumpSelectedSerial(int delta)
    {
        if (Selected is not SerialItem s) return;
        _doc.CheckpointCoalesced("serial:" + s.Id);
        s = (SerialItem)Selected!;
        s.Value = Math.Max(1, s.Value + delta);
        s.Touch();
        SyncVisuals();
        SerialChanged?.Invoke(this, EventArgs.Empty);
        RaiseChanged();
    }

    private void MoveSelected(Vector v)
    {
        var s = Selected;
        if (s == null) return;
        _doc.CheckpointCoalesced("move:" + s.Id);
        s = Selected!;
        s.MoveBy(0, v);
        s.Touch();
        SyncVisuals();
        RedrawOverlay();
        RaiseChanged();
    }
}
