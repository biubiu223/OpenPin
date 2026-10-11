using System.Data;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using OpenPin.Core;
using OpenPin.Services;
using OpenPin.Translate;

namespace OpenPin.Recognition;

/// <summary>
/// Table recognition result (PRO-02): editable grid preview, copy as TSV (pastes into Excel / WPS cell by cell),
/// export .xlsx (merged cells kept) and .csv. UI thread.
/// </summary>
public sealed class TableResultWindow : Window
{
    private readonly TableData _table;
    private readonly DataTable _data = new();
    private readonly DataGrid _grid = new();
    private readonly TextBlock _status = TranslateUi.Caption();

    public static TableResultWindow Show(TableData table)
    {
        var w = new TableResultWindow(table);
        w.Show();
        w.Activate();
        return w;
    }

    private TableResultWindow(TableData table)
    {
        _table = table;
        SetResourceReference(TitleProperty, "Recognition.Table.Title");
        TranslateUi.SetupWindow(this, Math.Clamp(160 + table.ColumnCount * 130, 560, 1100), Math.Clamp(200 + table.RowCount * 30, 360, 760));

        for (int c = 0; c < table.ColumnCount; c++) _data.Columns.Add("C" + c, typeof(string));
        for (int r = 0; r < table.RowCount; r++)
        {
            var row = _data.NewRow();
            for (int c = 0; c < table.ColumnCount; c++) row[c] = table[r, c];
            _data.Rows.Add(row);
        }

        _grid.SetResourceReference(StyleProperty, "Style.Recognition.DataGrid");
        _grid.AutoGenerateColumns = false;
        _grid.CanUserAddRows = false;
        _grid.CanUserDeleteRows = false;
        _grid.CanUserSortColumns = false;
        _grid.CanUserReorderColumns = false;
        _grid.SelectionUnit = DataGridSelectionUnit.CellOrRowHeader;
        _grid.RowHeaderWidth = table.RowCount >= 100 ? 48 : 36;
        for (int c = 0; c < table.ColumnCount; c++)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = XlsxWriter.ColumnName(c),
                Binding = new System.Windows.Data.Binding("C" + c) { Mode = System.Windows.Data.BindingMode.TwoWay, UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                MinWidth = 90,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                ElementStyle = WrapStyle(),
            });
        }
        _grid.LoadingRow += (_, e) => e.Row.Header = (e.Row.GetIndex() + 1).ToString();
        _grid.ItemsSource = _data.DefaultView;

        bool csvDefault = string.Equals(Settings?.TableExportFormat, "csv", StringComparison.OrdinalIgnoreCase);
        var copy = TranslateUi.Button("Recognition.Table.Copy", accent: false, CopyTsv, "");
        var xlsx = TranslateUi.Button("Recognition.Table.ExportXlsx", accent: !csvDefault, () => Export("xlsx"), "");
        var csv = TranslateUi.Button("Recognition.Table.ExportCsv", accent: csvDefault, () => Export("csv"), "");
        copy.ToolTip = Loc.Get("Recognition.Table.CopyTip");
        xlsx.Margin = csv.Margin = new Thickness(8, 0, 0, 0);

        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(copy);
        buttons.Children.Add(xlsx);
        buttons.Children.Add(csv);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(_status);
        TranslateUi.SetStatus(_status, Summary());

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);
        root.Children.Add(TranslateUi.Card(_grid, new Thickness(1)));
        Content = root;
    }

    private static RecognitionSettings? Settings
    {
        get
        {
            try { return AppServices.Settings?.Current.Recognition; }
            catch { return null; }
        }
    }

    private static Style WrapStyle()
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        s.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(4, 2, 4, 2)));
        return s;
    }

    private string Summary()
    {
        var text = Loc.Format("Recognition.Table.Summary", _table.RowCount, _table.ColumnCount);
        if (_table.Merges.Count > 0) text += Loc.Format("Recognition.Table.Merged", _table.Merges.Count);
        return text + "  " + Loc.Get("Recognition.Table.EditHint");
    }

    /// <summary>The table with the user's edits.</summary>
    public TableData CurrentTable()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        var t = new TableData(_table.RowCount, _table.ColumnCount, _table.Merges);
        for (int r = 0; r < _table.RowCount && r < _data.Rows.Count; r++)
        for (int c = 0; c < _table.ColumnCount; c++)
            t[r, c] = _data.Rows[r][c] as string ?? "";
        return t;
    }

    private void CopyTsv()
    {
        if (TranslateUi.TryCopyText(CurrentTable().ToTsv())) TranslateUi.SetStatus(_status, Loc.Get("Recognition.Table.Copied"));
    }

    private void Export(string format)
    {
        var settings = Settings;
        var folder = settings?.LastExportFolder is { Length: > 0 } f && Directory.Exists(f)
            ? f : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.Get(format == "xlsx" ? "Recognition.Table.ExportXlsx" : "Recognition.Table.ExportCsv"),
            Filter = format == "xlsx" ? Loc.Get("Recognition.Table.FilterXlsx") + "|*.xlsx" : Loc.Get("Recognition.Table.FilterCsv") + "|*.csv",
            DefaultExt = "." + format,
            AddExtension = true,
            InitialDirectory = folder,
            FileName = $"{Loc.Get("Recognition.Table.FileName")}_{DateTime.Now:yyyyMMdd_HHmmss}.{format}",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        var path = dialog.FileName;
        try
        {
            var table = CurrentTable();
            if (format == "xlsx") XlsxWriter.Save(path, table);
            else File.WriteAllText(path, table.ToCsv(), new UTF8Encoding(true)); // BOM: Excel detects UTF-8
            if (settings != null)
            {
                settings.LastExportFolder = Path.GetDirectoryName(path) ?? "";
                settings.TableExportFormat = format;
                AppServices.Settings?.Save();
            }
            TranslateUi.SetStatus(_status, Loc.Format("Recognition.Table.Exported", path));
            Notify.Show(new NotifyOptions
            {
                Title = Loc.Get("Recognition.Table.Title"),
                Message = Loc.Format("Recognition.Table.Exported", Path.GetFileName(path)),
                Actions = new[]
                {
                    new NotifyAction(Loc.Get("Recognition.Table.Open"), () => OpenFile(path)),
                    new NotifyAction(Loc.Get("Recognition.Table.ShowInFolder"), () => SaveService.RevealInExplorer(path)),
                },
            });
        }
        catch (Exception ex)
        {
            Log.Error("Table export failed", ex);
            TranslateUi.SetStatus(_status, Loc.Format("Recognition.Table.ExportFailed", ex.Message), error: true);
        }
    }

    private static void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Log.Error("Opening exported table failed", ex);
            Notify.Error(Loc.Format("Common.ActionFailed", ex.Message));
        }
    }
}
