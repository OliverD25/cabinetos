using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Platform;
using CabinetOS.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace CabinetOS;

// The keyboard and the web pages (the terminal, the tools). XAML's focus on a WebView2 does not
// mean Windows sends the keys there (PageKeyboard): the window hands the keys over, checks where
// they went, and hands them over again. It also logs where the keyboard is: XAML's focused element
// and the page whose input window gets the keys.
public sealed partial class MainWindow
{
    private readonly uint _uiThreadId = WindowsPlatform.CurrentThreadId();

    // Each hand-over to a page gets a number; a check that finds a newer one leaves the keyboard alone.
    private int _pageHandOver;

    /// <summary>
    /// Gives <paramref name="view"/> (the page named <paramref name="page"/>)
    /// the keyboard, then checks that Windows sends the keys there, and hands
    /// them over again while <paramref name="stillWanted"/> holds.
    /// <paramref name="afterFocus"/> runs after each hand-over (the terminal
    /// then focuses its shown shell). False when XAML refused the focus.
    /// </summary>
    private bool GiveKeysToPage(WebView2? view, string page, Func<bool> stillWanted, Action? afterFocus = null)
    {
        if (view is null || !view.Focus(FocusState.Programmatic))
        {
            return false;
        }
        afterFocus?.Invoke();
        CheckPageKeysSoon(view, page, stillWanted, afterFocus, ++_pageHandOver, attempt: 0);
        return true;
    }

    private void CheckPageKeysSoon(WebView2 view, string page, Func<bool> stillWanted, Action? afterFocus, int handOver, int attempt)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = PageKeyboard.CheckAfter;
        timer.Tick += (_, _) =>
        {
            if (handOver != _pageHandOver)
            {
                return;
            }
            var xamlOnPage = RootGrid.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is WebView2 focused && focused == view;
            var keys = WindowsPlatform.KeyboardFocus(_uiThreadId);
            switch (PageKeyboard.Next(stillWanted(), xamlOnPage, keys?.Class, attempt))
            {
                case PageKeyboardStep.Done:
                    Diag.Info(Target, "a page has the keyboard", new LogField("page", page), new LogField("hand_overs", attempt + 1));
                    break;
                case PageKeyboardStep.HandOverAgain:
                    Diag.Info(Target, "a page did not get the keyboard; handing it over again", new LogField("page", page),
                        new LogField("attempt", attempt + 1), new LogField("window_class", keys?.Class ?? ""));
                    HandOverAgain(view, afterFocus);
                    CheckPageKeysSoon(view, page, stillWanted, afterFocus, handOver, attempt + 1);
                    break;
                case PageKeyboardStep.GiveUp:
                    Diag.Warn(Target, "a page did not get the keyboard", new LogField("page", page), new LogField("hand_overs", attempt + 1),
                        new LogField("window_class", keys?.Class ?? ""));
                    break;
            }
        };
        timer.Start();
    }

    // WinUI moves the keys into a page only as XAML's focus arrives there (WebView2's GotFocus), and
    // a page that already has XAML's focus gets no second GotFocus: the focus leaves for a moment
    // (the active pane, or the page's own header when an editor covers the pane) and comes back.
    private void HandOverAgain(WebView2 view, Action? afterFocus)
    {
        Control? away = _paneViews[_active].Visibility == Visibility.Visible ? _paneViews[_active]
            : _paneViews[1 - _active].Visibility == Visibility.Visible ? _paneViews[1 - _active]
            : view == Dock.TerminalPage.View ? Dock.HeaderStop
            : Array.Find(_editorViews, v => v.PageView == view)?.HeaderStop;
        if (away is null || !away.Focus(FocusState.Programmatic))
        {
            return;
        }
        if (view.Focus(FocusState.Programmatic))
        {
            afterFocus?.Invoke();
        }
    }

    // A key reached the window while XAML's focus is on a page. Normally the page has the keys and
    // this is its copy (the page passes the window's keys back itself). When Windows sent the keys
    // to the window instead, the page never saw it: its ways out (palette.show,
    // view.toggleTerminal, a terminalFocus binding) run here, and the page gets the keyboard again.
    private void HandleKeyForPage(WebView2 view, KeyRoutedEventArgs e)
    {
        var keys = WindowsPlatform.KeyboardFocus(_uiThreadId);
        if (PageKeyboard.PageSawKey(keys?.Class))
        {
            return;
        }
        var terminal = view == Dock.TerminalPage.View;
        if (KeyNames.ComboFor((int)e.Key, CurrentModifiers())?.ToString() is not { } combo)
        {
            // A modifier alone: the key that follows decides.
            return;
        }
        var inSidebar = _sidebarPages.Values.Any(p => p.Host.Page.View == view);
        var command = terminal ? _terminal.PassKeyCommand(combo) : (inSidebar ? _sidebarPageKeys : _toolKeys).GetValueOrDefault(combo);
        Diag.Info(Target, "a key the page did not get", new LogField("page", terminal ? "terminal" : "tool"), new LogField("key", combo),
            new LogField("command", command ?? ""));
        if (command is not null)
        {
            e.Handled = true;
            _ = _router.ExecuteAsync(command, trigger: "key", traceId: TakeKeyTrace());
            return;
        }
        if (terminal)
        {
            GiveKeysToPage(view, "terminal", () => _dockVisible, _terminal.FocusPage);
        }
        else if (Array.FindIndex(_editorViews, v => v.PageView == view) is var pane and >= 0)
        {
            FocusEditorPage(pane);
        }
        else if (_sidebarPages.FirstOrDefault(p => p.Value.Host.Page.View == view) is { Value: not null } sidebarPage)
        {
            FocusSidebarPage(sidebarPage.Key);
        }
    }

    private void LogKeyboard(string moment)
    {
        var focused = RootGrid.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
        var keys = WindowsPlatform.KeyboardFocus(_uiThreadId);
        Diag.Info(Target, "keyboard owner", new LogField("moment", moment), new LogField("element", focused?.GetType().Name ?? "none"),
            new LogField("keys_to", keys is { } owner ? KeyboardOwnerName(owner.Window, owner.Class, owner.ProcessId) : "none"),
            new LogField("window_class", keys?.Class ?? ""));
    }

    // The page whose input window has the keys: "terminal", "tool:<id>", "a page" when its place
    // cannot be told, "window" for the window's own controls.
    private string KeyboardOwnerName(nint window, string windowClass, int processId)
    {
        if (processId != Environment.ProcessId)
        {
            return $"process {processId}";
        }
        if (!PageKeyboard.IsPageWindow(windowClass))
        {
            return "window";
        }
        if (WindowsPlatform.ScreenRect(window) is { } rect)
        {
            var x = (rect.Left + rect.Right) / 2.0;
            var y = (rect.Top + rect.Bottom) / 2.0;
            if (Dock.TerminalPage.View is { } terminal && Covers(terminal, x, y))
            {
                return "terminal";
            }
            foreach (var host in _toolHosts)
            {
                if (host?.Page.View is { } page && Covers(page, x, y))
                {
                    return $"tool:{host.Tool.Manifest.Id}";
                }
            }
            foreach (var (id, sidebarPage) in _sidebarPages)
            {
                if (sidebarPage.Host.Page.View is { } page && Covers(page, x, y))
                {
                    return $"sidebar:{id}";
                }
            }
        }
        return "a page";
    }

    private bool Covers(FrameworkElement element, double x, double y)
    {
        if (element.ActualWidth <= 0 || element.XamlRoot is not { } root)
        {
            return false;
        }
        var scale = root.RasterizationScale;
        var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var (left, top) = WindowsPlatform.ClientOrigin(WinRT.Interop.WindowNative.GetWindowHandle(this));
        return x >= left + (bounds.X * scale) && x < left + (bounds.Right * scale)
            && y >= top + (bounds.Y * scale) && y < top + (bounds.Bottom * scale);
    }

    // The same report a moment later, once WebView2 has moved the keys (it does so on its own time).
    private void LogKeyboardSoon(string moment)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.Tick += (_, _) => LogKeyboard(moment);
        timer.Start();
    }
}
