using System.Globalization;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Presentation;
using CabinetOS.Core.Settings;
using CabinetOS.Views;

namespace CabinetOS;

// The file panes' column widths (docs/ui.md, "Column widths"): one set for both panes, which the window owns. A grip's
// drag or a fit in either pane changes both at once; the end of a drag and a fit save them in ui.columns through the
// core (Article 6). ui.columns from the configuration and a theme's sizes lay them out again through the same path.
public sealed partial class MainWindow
{
    private const string ColumnsTarget = "cabinetos_ui::columns";

    // Writes of ui.columns on their way, and a drag: a configuration read meanwhile may still hold the widths before them.
    private int _columnWrites;
    private bool _columnsDragging;

    private void SetUpColumns()
    {
        foreach (var view in _paneViews)
        {
            view.ColumnsDragged += OnColumnsDragged;
            view.ColumnsFit += (_, widths) => ChangeColumns(widths, "fit");
        }
    }

    private void RegisterColumnCommands()
    {
        // Name's heading's double-click, from the palette: Modified, Type and Size fitted in the active pane.
        _router.RegisterUiHandler("view.fitColumns", _ => _paneViews[_active].FitColumns(ListColumn.Name));
        _router.RegisterUiHandler("view.resetColumns", _ => ChangeColumns(null, "reset"));
    }

    // While the pointer moves, both panes follow and nothing is written; the release logs and saves once.
    private void OnColumnsDragged(FilePane pane, ColumnWidths widths, bool done)
    {
        _columnsDragging = !done;
        if (WindowMetrics.TakeColumns(widths))
        {
            ApplyColumnsToPanes();
        }
        if (done)
        {
            LogColumns("drag");
            _ = SaveColumnsAsync();
        }
    }

    // A fit or a reset: both panes at once, then the log and the save. One that changes nothing does nothing.
    private void ChangeColumns(ColumnWidths? widths, string how)
    {
        if (!WindowMetrics.TakeColumns(widths))
        {
            return;
        }
        ApplyColumnsToPanes();
        LogColumns(how);
        _ = SaveColumnsAsync();
    }

    private void ApplyColumnsToPanes()
    {
        foreach (var pane in _paneViews)
        {
            pane.ApplyColumns();
        }
    }

    // What the columns are now in the active pane: the three widths in effect and the Name width they leave.
    private void LogColumns(string how)
    {
        var split = WindowMetrics.Columns.Resolve(_paneViews[_active].ColumnsAvailable);
        Diag.Info(ColumnsTarget, "columns changed", new LogField("how", how), new LogField("modified", Round(split.Modified)),
            new LogField("type", Round(split.Type)), new LogField("size", Round(split.Size)), new LogField("name", Round(split.Name)),
            new LogField("user", WindowMetrics.UserColumns is not null));
    }

    // Logged once the layout is done, when the active pane's width is the new one (a theme's sizes, the first start).
    private void LogColumnsAfterLayout(string how) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => LogColumns(how));

    // Refused or not written, the widths stay on screen: the user sees what was dragged, and the next save may land.
    private async Task SaveColumnsAsync()
    {
        var layout = WindowMetrics.Columns;
        _columnWrites++;
        try
        {
            var refusal = await _settingsWriter.SetOrRefusalAsync("ui.columns", layout.ToJson());
            if (refusal is null)
            {
                Diag.Info(ColumnsTarget, "columns saved", new LogField("columns", layout.ToJson().GetRawText()));
            }
            else
            {
                Diag.Warn(ColumnsTarget, "columns not saved", new LogField("error", refusal));
            }
        }
        finally
        {
            _columnWrites--;
        }
    }

    // ui.columns from the configuration, at start and after an edit of the file (or another window's save). The window's
    // own writes on their way, and a drag, are not undone by a configuration read before they landed; the widths on screen
    // are the comparison, so the configuration that follows the window's own save changes nothing.
    private void ApplyColumnSettings(UiSettings settings)
    {
        if (_columnWrites > 0 || _columnsDragging || !WindowMetrics.TakeColumns(settings.Columns))
        {
            return;
        }
        ApplyColumnsToPanes();
        // At start the panes are not laid out yet: the Name width in the log is the one after the layout.
        LogColumnsAfterLayout("config");
    }

    // ----- The snapshot aid's steps (docs/ui.md, "Column widths") -----

    // columns:<label> logs what both panes show; column-drag:<divider>|<dx> drags a grip of the active pane through its own
    // drag steps; column-fit:<column> fits as a double-click on that heading does. The two do not wait: the save they start
    // brings a config_changed, and until:config must be the next step to see it.
    private void RunColumnStep(string kind, string argument)
    {
        switch (kind)
        {
            case "columns":
                LogColumnsShown(argument);
                break;
            case "column-drag":
                var parts = argument.Split('|');
                if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var divider) && divider is >= 1 and <= 3
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dx))
                {
                    _paneViews[_active].DragGripForSnapshot((ColumnDivider)divider, dx);
                }
                else
                {
                    Diag.Info("cabinetos_ui::snapshot", "column-drag needs a divider from 1 to 3 and pixels, as 2|40", new LogField("step", argument));
                }
                break;
            case "column-fit":
                if (Enum.TryParse<ListColumn>(argument, ignoreCase: true, out var column) && Enum.IsDefined(column) && !int.TryParse(argument, out _))
                {
                    _paneViews[_active].FitColumns(column);
                }
                else
                {
                    Diag.Info("cabinetos_ui::snapshot", "column-fit needs name, modified, type or size", new LogField("step", argument));
                }
                break;
        }
    }

    private void LogColumnsShown(string label)
    {
        var fields = new List<LogField> { new("label", label), new("user", WindowMetrics.UserColumns is not null), new("active", _active) };
        for (var i = 0; i < _paneViews.Length; i++)
        {
            var shown = _paneViews[i].ColumnsShown();
            fields.Add(new($"pane{i}_name", Round(shown.Name)));
            fields.Add(new($"pane{i}_modified", Round(shown.Modified)));
            fields.Add(new($"pane{i}_type", Round(shown.Type)));
            fields.Add(new($"pane{i}_size", Round(shown.Size)));
            fields.Add(new($"pane{i}_list", Round(shown.List)));
            fields.Add(new($"pane{i}_row", shown.Row));
            fields.Add(new($"pane{i}_grip_offset", Round(shown.GripOffset)));
        }
        Diag.Info(ColumnsTarget, "columns shown", [.. fields]);
    }

    private static double Round(double pixels) => Math.Round(pixels, 1);
}
