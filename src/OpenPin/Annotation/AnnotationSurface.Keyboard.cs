using System.Windows;
using System.Windows.Input;
using OpenPin.Annotation.Model.Items;

namespace OpenPin.Annotation;

public partial class AnnotationSurface
{
    /// <summary>Shift+digit tool shortcuts (Shift+1 toggles rectangle ↔ ellipse).</summary>
    internal static AnnotationTool? ToolForDigit(Key key) => key switch
    {
        Key.D1 or Key.NumPad1 => AnnotationTool.Rectangle,
        Key.D2 or Key.NumPad2 => AnnotationTool.Line,
        Key.D3 or Key.NumPad3 => AnnotationTool.Arrow,
        Key.D4 or Key.NumPad4 => AnnotationTool.Serial,
        Key.D5 or Key.NumPad5 => AnnotationTool.Pencil,
        Key.D6 or Key.NumPad6 => AnnotationTool.Highlighter,
        Key.D7 or Key.NumPad7 => AnnotationTool.Mosaic,
        Key.D8 or Key.NumPad8 => AnnotationTool.Text,
        Key.D9 or Key.NumPad9 => AnnotationTool.Eraser,
        Key.D0 or Key.NumPad0 => AnnotationTool.Spotlight,
        _ => null,
    };

    /// <summary>Toolbar / shortcut activation: the same tool again turns it off (rectangle ↔ ellipse for the geometry tool).</summary>
    internal void ToggleTool(AnnotationTool tool)
    {
        if (tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse)
        {
            if (_tool == AnnotationTool.Rectangle) { Tool = AnnotationTool.Ellipse; return; }
            if (_tool == AnnotationTool.Ellipse) { Tool = AnnotationTool.Rectangle; return; }
            Tool = tool == AnnotationTool.Rectangle ? Options.LastGeometryTool : tool;
            return;
        }
        Tool = _tool == tool ? AnnotationTool.None : tool;
    }

    private bool HandleKeyCore(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.ImeProcessed) return false;
        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0, shift = (mods & ModifierKeys.Shift) != 0, alt = (mods & ModifierKeys.Alt) != 0;

        // Inline text editor: only Esc / Ctrl+Enter are ours, everything else goes to the TextBox.
        if (_editor != null)
        {
            if (key == Key.Escape || (key == Key.Enter && ctrl))
            {
                CommitText();
                return true;
            }
            return false;
        }

        // Another text input (toolbar) has focus: don't steal its keys; Esc gives focus back.
        if (ForeignTextInputFocused())
        {
            if (key == Key.Escape)
            {
                Keyboard.ClearFocus();
                Focus();
                return true;
            }
            return false;
        }

        if (ctrl && !alt && key == Key.Z)
        {
            if (shift) Redo(); else Undo();
            return true;
        }
        if (ctrl && !alt && key == Key.Y)
        {
            Redo();
            return true;
        }

        if (key == Key.Escape)
        {
            if (_gesture != null || _polyline != null) { CancelGesture(); return true; }
            if (Selected != null) { Select(null); return true; }
            return false;
        }

        if (key == Key.Enter && _polyline != null)
        {
            FinishPolyline();
            return true;
        }

        if (_gesture != null) return false;

        if ((key == Key.Delete || key == Key.Back) && !ctrl && !alt && Selected != null)
            return DeleteSelected();

        if (shift && !ctrl && !alt && ToolForDigit(key) is { } tool)
        {
            ToggleTool(tool);
            return true;
        }

        if (Selected is { } sel && !ctrl && !alt)
        {
            double step = shift ? 10 : 1;
            switch (key)
            {
                case Key.Left: MoveSelected(new Vector(-step, 0)); return true;
                case Key.Right: MoveSelected(new Vector(step, 0)); return true;
                case Key.Up: MoveSelected(new Vector(0, -step)); return true;
                case Key.Down: MoveSelected(new Vector(0, step)); return true;
            }
            if (sel is SerialItem s)
            {
                switch (key)
                {
                    case Key.OemPlus or Key.Add: BumpSelectedSerial(1); return true;
                    case Key.OemMinus or Key.Subtract: BumpSelectedSerial(-1); return true;
                    case Key.A when !shift: BeginSerialNote(s); return true;
                }
            }
            if (sel is TextItem t && key == Key.Enter && _tool == AnnotationTool.Text)
            {
                BeginEditText(t, isNew: false);
                return true;
            }
        }
        return false;
    }
}
