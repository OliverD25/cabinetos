using System.Text.Json;
using CabinetOS.Core.Commands;
using CabinetOS.Core.Search;
using CabinetOS.Services;
using CabinetOS.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace CabinetOS;

// The search field and the pane's search results (docs/ui.md, "Search").
public sealed partial class MainWindow
{
    private SearchModel _search = null!;
    private DispatcherQueueTimer _searchTimer = null!;
    private int _searchPane = -1;
    private bool _settingSearchText;

    private void SetUpSearch()
    {
        _search = new SearchModel(_session, () => Environment.TickCount64);
        _search.Changed += ShowSearch;
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _ = SearchWhenDueAsync(now: false);
        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.KeyDown += OnSearchKeyDown;
    }

    private void RegisterSearchCommands()
    {
        _router.RegisterUiHandler("search.focus", _ =>
        {
            EndAddressEdit();
            SearchBox.Focus(FocusState.Keyboard);
            SearchBox.SelectAll();
        });
        // The pane's "Whole volume" box passes its state; from the palette or a key it toggles.
        _router.RegisterUiHandler("search.scope", invocation =>
        {
            var wanted = invocation.Args is { ValueKind: JsonValueKind.Object } args
                && args.TryGetProperty("wholeVolume", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean()
                    : !_search.WholeVolume;
            _search.SetWholeVolume(wanted);
            return SearchWhenDueAsync(now: true);
        });
    }

    // Typing searches the active pane's folder; typing while the other pane is
    // active moves the results there.
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        // The Search view's field shows the same text.
        SearchPanelView.Query = SearchBox.Text;
        if (_settingSearchText)
        {
            return;
        }
        if (_searchPane >= 0 && _searchPane != _active)
        {
            _panes[_searchPane].Search = null;
        }
        if (_editorViews[_active].IsOpen && SearchBox.Text.Trim().Length > 0)
        {
            // One pane shown with a tool in it: the hits need the pane, so its folder tab comes to the front (the tool tab stays).
            ShowFolderTabBehindTool(_active);
        }
        _searchPane = _active;
        _search.SetText(SearchBox.Text, Active.Path.Length > 0 ? Active.Path : null);
        ScheduleSearch();
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                // Enter searches at once and hands the keyboard to the hits; Enter there opens one.
                e.Handled = true;
                _ = SearchNowAndFocusAsync();
                break;
            case VirtualKey.Down when _searchPane >= 0 && _panes[_searchPane].ShownCount > 0:
                e.Handled = true;
                _paneViews[_searchPane].Focus(FocusState.Keyboard);
                break;
        }
    }

    private async Task SearchNowAndFocusAsync()
    {
        await SearchWhenDueAsync(now: true);
        if (_searchPane >= 0 && _panes[_searchPane].ShownCount > 0)
        {
            _paneViews[_searchPane].Focus(FocusState.Keyboard);
        }
    }

    private void ScheduleSearch()
    {
        var wait = _search.MillisecondsUntilDue();
        _searchTimer.Stop();
        if (wait >= 0)
        {
            _searchTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, wait));
            _searchTimer.Start();
        }
    }

    private async Task SearchWhenDueAsync(bool now)
    {
        if (!await _search.SearchIfDueAsync(now))
        {
            // Typed again meanwhile: wait for the rest of the pause.
            ScheduleSearch();
        }
    }

    private void ShowSearch()
    {
        if (_searchPane < 0)
        {
            return;
        }
        var pane = _panes[_searchPane];
        if (!_search.IsActive)
        {
            pane.Search = null;
            _searchPane = -1;
            SearchPanelView.Show(null);
            UpdateStatus();
            return;
        }
        var rows = _search.Results is { } results
            ? ReferenceEquals(results.Hits, pane.Search?.Rows?.Hits) ? pane.Search!.Rows : new SearchRows(results.Hits, pane.KnownDetails, _search.Shown?.Root)
            : null;
        pane.Search = new PaneSearch(_search.Header, _search.Scope, _search.Note, _search.WholeVolume, rows);
        SearchPanelView.SetWholeVolume(_search.WholeVolume);
        SearchPanelView.Show(pane.Search);
        UpdateStatus();
    }

    /// <summary>Leaves the search: the field empties and the pane shows its folder again.</summary>
    private bool EndSearch(bool focusPane)
    {
        if (!_search.IsActive && SearchBox.Text.Length == 0)
        {
            return false;
        }
        var pane = _searchPane;
        _settingSearchText = true;
        SearchBox.Text = "";
        _settingSearchText = false;
        SearchPanelView.Query = "";
        _searchTimer.Stop();
        _search.Clear();
        if (focusPane)
        {
            _paneViews[pane >= 0 ? pane : _active].Focus(FocusState.Programmatic);
        }
        return true;
    }

    // Enter on a hit: its folder opens in the pane with the hit selected.
    private async Task OpenHitAsync(CommandInvocation invocation)
    {
        if (Active.FocusedHit is not { } hit)
        {
            return;
        }
        await GoToHitAsync(hit, invocation.RequestId);
    }

    // The same from the Search view of the sidebar: a click or Enter on one of its hits.
    private async Task GoToHitAsync(SearchRowItem hit, string? requestId)
    {
        var folder = hit.Folder.Length > 0 ? hit.Folder : hit.Hit.Path;
        var name = hit.Folder.Length > 0 ? hit.Name : null;
        var paneIndex = _active;
        EndSearch(focusPane: false);
        await _panes[paneIndex].NavigateAsync(folder, requestId, NavigationKind.New, name);
        // The hit may be in the folder the pane showed already, which keeps its scroll position.
        _paneViews[paneIndex].ScrollToFocus();
        _paneViews[paneIndex].Focus(FocusState.Programmatic);
    }

    // In search results the listing's selection is out of sight: a file command must not act on it.
    private Func<CommandInvocation, Task> ListingOnly(Func<CommandInvocation, Task> handler) => invocation =>
    {
        if (Active.Search is null)
        {
            return handler(invocation);
        }
        ShowNotice("These are search results: Enter goes to a hit, Esc back to the folder.");
        return Task.CompletedTask;
    };

    private Func<CommandInvocation, Task> ListingOnly(Action<CommandInvocation> handler) => ListingOnly(invocation =>
    {
        handler(invocation);
        return Task.CompletedTask;
    });
}
