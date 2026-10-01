using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using CabinetOS.Core.Diagnostics;
using CabinetOS.Core.Keys;
using CabinetOS.Core.Platform;
using CabinetOS.Core.Presentation;
using CabinetOS.Services;
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

    // Checks of a hand-over that have not run yet: the snapshot aid's until:keyboard waits until there is none.
    private int _pageChecksPending;

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

    // The check of one hand-over, PageKeyboard.CheckAfter after it. It waits on a task's timer and not on a DispatcherQueueTimer
    // that nothing refers to: such a timer may be collected before it ticks (the rail's tree look had the same fault), and the
    // check then never runs. The live check of 2026-10-01 on the laptop lost the second check of a hand-over that way: the log
    // had "handing it over again" and no later line, and the window never said whether the page had the keyboard. The
    // window test that hands the terminal the keyboard 20 times lost one check in 20 rounds on a quiet laptop.
    // Every check now ends with a line: the page has the keyboard, the window gave up, or the hand-over ended for a reason.
    private async void CheckPageKeysSoon(WebView2 view, string page, Func<bool> stillWanted, Action? afterFocus, int handOver, int attempt)
    {
        _pageChecksPending++;
        await Task.Delay(PageKeyboard.CheckAfter);
        _pageChecksPending--;
        if (handOver != _pageHandOver)
        {
            Diag.Info(Target, "a page's hand-over ended: a newer hand-over took over", new LogField("page", page), new LogField("hand_over", handOver),
                new LogField("attempt", attempt + 1));
            return;
        }
        var wanted = stillWanted();
        var xamlOnPage = RootGrid.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is WebView2 focused && focused == view;
        var keys = WindowsPlatform.KeyboardFocus(_uiThreadId);
        switch (PageKeyboard.Next(wanted, xamlOnPage, keys?.Class, attempt))
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
            default:
                // Nobody wants the keys in the page any more: the user went on, as they may.
                Diag.Info(Target, "a page's hand-over ended: the keyboard is wanted elsewhere", new LogField("page", page), new LogField("attempt", attempt + 1),
                    new LogField("page_wanted", wanted), new LogField("xaml_focus_on_page", xamlOnPage));
                break;
        }
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
        var editorPane = Array.FindIndex(_editorViews, v => v.PageView == view);
        var command = terminal ? _terminal.PassKeyCommand(combo) : (inSidebar ? _sidebarPageKeys : _toolKeys).GetValueOrDefault(combo);
        Diag.Info(Target, "a key the page did not get", new LogField("page", terminal ? "terminal" : "tool"), new LogField("key", combo),
            new LogField("command", command ?? ""));
        if (command is not null)
        {
            e.Handled = true;
            if (!e.KeyStatus.WasKeyDown || ChordStateMachine.RepeatingCommands.Contains(command))
            {
                _ = _router.ExecuteAsync(command, PageKeyArguments(command, combo, editorPane >= 0 ? editorPane : null), "key", TakeKeyTrace());
            }
            return;
        }
        if (terminal)
        {
            GiveKeysToPage(view, "terminal", () => _dockVisible, _terminal.FocusPage);
        }
        else if (editorPane >= 0)
        {
            FocusEditorPage(editorPane);
        }
        else if (_sidebarPages.FirstOrDefault(p => p.Value.Host.Page.View == view) is { Value: not null } sidebarPage)
        {
            FocusSidebarPage(sidebarPage.Key);
        }
    }

    // The arguments of a command a page passed back. Go to Tab's key names its tab by its digit (KeyArguments). A tab command
    // from a page in a pane's tab is about that pane's tabs: the keyboard is in that pane's page even when the other pane is the active one.
    private static JsonElement? PageKeyArguments(string command, string combo, int? pane)
    {
        var digit = KeySequence.TryParse(combo, out var keys) ? CommandArgs.Number(KeyArguments(command, keys), "tab") : null;
        if (pane is null || !command.StartsWith("tab.", StringComparison.Ordinal))
        {
            return digit is { } place ? CommandArgs.Object(("tab", (int)place)) : null;
        }
        return digit is { } tab ? CommandArgs.Object(("pane", pane.Value), ("tab", (int)tab)) : CommandArgs.Object(("pane", pane.Value));
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

    // The snapshot aid's key:<keys> step. Each combination goes the way a real key comes: as key messages to the
    // window's input window, so WinUI routes it as it routes a real one (the window's PreviewKeyDown, the focused
    // control, Tab's move between controls, a dialog's buttons). The modifiers are down in the UI thread's key state
    // while the messages are handled, as held keys would be, so the window need not be in front and no key reaches
    // another program. "key:ctrl+k ctrl+t" presses a chord's two halves.
    private async Task PressKeysForSnapshotAsync(string keys)
    {
        var site = WindowsPlatform.FindDescendant(WinRT.Interop.WindowNative.GetWindowHandle(this), "InputSiteWindowClass");
        foreach (var part in keys.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (site == 0 || !KeyCombo.TryParse(part, out var parsed) || KeyNames.VirtualKeyFor(parsed.Value.Key) is not { } key)
            {
                Diag.Info("cabinetos_ui::snapshot", "key: cannot press that", new LogField("keys", part), new LogField("input_window", site != 0));
                return;
            }
            var combo = parsed.Value;
            if (RootGrid.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is WebView2 { CoreWebView2: { } page })
            {
                // A page has the keyboard: a real key goes to the page, whose script passes the window's keys back as messages
                // (the window's input window would drop it as the page's). DevTools' key events run the page's own key path.
                await PressKeyInPageAsync(page, combo, key);
                continue;
            }
            var modifiers = new List<int>(4);
            if ((combo.Modifiers & KeyModifiers.Ctrl) != 0) modifiers.Add(0x11);
            if ((combo.Modifiers & KeyModifiers.Shift) != 0) modifiers.Add(0x10);
            if ((combo.Modifiers & KeyModifiers.Alt) != 0) modifiers.Add(0x12);
            if ((combo.Modifiers & KeyModifiers.Win) != 0) modifiers.Add(0x5B);
            var saved = WindowsPlatform.KeyState();
            var held = (byte[])saved.Clone();
            foreach (var modifier in modifiers)
            {
                held[modifier] = 0x80;
                // The left key too: Windows reports a held modifier as the generic key and the side it is on.
                if (modifier is 0x11 or 0x10 or 0x12)
                {
                    held[modifier switch { 0x11 => 0xA2, 0x10 => 0xA0, _ => 0xA4 }] = 0x80;
                }
            }
            WindowsPlatform.SetKeyState(held);
            var alt = false;
            foreach (var modifier in modifiers)
            {
                WindowsPlatform.PostKey(site, modifier, up: false, alt);
                alt |= modifier == 0x12;
            }
            WindowsPlatform.PostKey(site, key, up: false, alt);
            WindowsPlatform.PostKey(site, key, up: true, alt);
            for (var i = modifiers.Count - 1; i >= 0; i--)
            {
                alt &= modifiers[i] != 0x12;
                WindowsPlatform.PostKey(site, modifiers[i], up: true, alt);
            }
            // The messages are handled on this thread while it waits; then the keys are up again.
            await Task.Delay(150);
            WindowsPlatform.SetKeyState(saved);
            Diag.Info("cabinetos_ui::snapshot", "key sent", new LogField("keys", combo.ToString()));
        }
    }

    private static async Task PressKeyInPageAsync(Microsoft.Web.WebView2.Core.CoreWebView2 page, KeyCombo combo, int virtualKey)
    {
        // DevTools' modifier bits: Alt 1, Ctrl 2, Meta 4, Shift 8.
        var bits = ((combo.Modifiers & KeyModifiers.Alt) != 0 ? 1 : 0) | ((combo.Modifiers & KeyModifiers.Ctrl) != 0 ? 2 : 0)
            | ((combo.Modifiers & KeyModifiers.Win) != 0 ? 4 : 0) | ((combo.Modifiers & KeyModifiers.Shift) != 0 ? 8 : 0);
        foreach (var type in new[] { "rawKeyDown", "keyUp" })
        {
            try
            {
                var sent = page.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                    $"{{\"type\":\"{type}\",\"modifiers\":{bits},\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey}}}").AsTask();
                if (await Task.WhenAny(sent, Task.Delay(1000)) != sent)
                {
                    // The key closed the page (Ctrl+W on a tool's tab): the answer never comes.
                    _ = sent.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    break;
                }
                await sent;
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or ObjectDisposedException)
            {
                break;
            }
        }
        await Task.Delay(150);
        Diag.Info("cabinetos_ui::snapshot", "key sent to a page", new LogField("keys", combo.ToString()));
    }

    // The same report a moment later, once WebView2 has moved the keys (it does so on its own time).
    // A task's timer, for the reason given at CheckPageKeysSoon.
    private async void LogKeyboardSoon(string moment)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        LogKeyboard(moment);
    }
}
