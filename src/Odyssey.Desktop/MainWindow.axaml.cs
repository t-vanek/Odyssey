using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Odyssey.Core;

namespace Odyssey.Desktop;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private bool _suggestionAcceptanceQueued;
    private readonly DispatcherTimer _rescueTipTimer;
    private bool _windowOpen;
    private bool _searchHasFocus;
    private bool _tipPointerOver;
    private bool _tipTransitioning;
    private bool _applyingPaneSelection;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        _rescueTipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _rescueTipTimer.Tick += RotateRescueTip;
        this.FindControl<ListBox>("LeftFileList")?.AddHandler(ScrollViewer.ScrollChangedEvent, LeftListScrolled);
        this.FindControl<ListBox>("RightFileList")?.AddHandler(ScrollViewer.ScrollChangedEvent, RightListScrolled);
        this.FindControl<ListBox>("SearchResultList")?.AddHandler(ScrollViewer.ScrollChangedEvent, SearchListScrolled);
        this.FindControl<ListBox>("RescueResultList")?.AddHandler(ScrollViewer.ScrollChangedEvent, SearchListScrolled);
        if (this.FindControl<TextBox>("RescueSearchBox") is { } rescueSearch)
        {
            rescueSearch.KeyDown += RescueSearchKeyDown;
            rescueSearch.GotFocus += (_, _) => { _searchHasFocus = true; UpdateTipTimer(); };
            rescueSearch.LostFocus += (_, _) => { _searchHasFocus = false; UpdateTipTimer(); };
        }
        if (this.FindControl<Border>("RescueTipPanel") is { } tipPanel)
        {
            tipPanel.PointerEntered += (_, _) => { _tipPointerOver = true; UpdateTipTimer(); };
            tipPanel.PointerExited += (_, _) => { _tipPointerOver = false; UpdateTipTimer(); };
        }
        Opened += MainWindowOpened;
        AddHandler(KeyDownEvent, MainWindowKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        Closed += (_, _) => { _windowOpen = false; _rescueTipTimer.Stop(); };
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.LeftPane.SelectionRequested += PaneSelectionRequested;
        viewModel.RightPane.SelectionRequested += PaneSelectionRequested;
        Closing += (_, _) => viewModel.CancelActiveWork();
    }

    private void LeftSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdatePaneSelectionUnlessApplying(sender, _viewModel?.LeftPane);

    private void RightSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdatePaneSelectionUnlessApplying(sender, _viewModel?.RightPane);

    private void UpdatePaneSelectionUnlessApplying(object? sender, FilePaneViewModel? pane)
    {
        if (!_applyingPaneSelection) UpdatePaneSelection(sender, pane);
    }

    private void PaneSelectionRequested(object? sender, IReadOnlyList<BrowserEntry> selection)
    {
        if (_viewModel is null || sender is not FilePaneViewModel pane) return;
        var list = this.FindControl<ListBox>(ReferenceEquals(pane, _viewModel.LeftPane)
            ? "LeftFileList"
            : "RightFileList");
        if (list?.SelectedItems is null) return;
        _applyingPaneSelection = true;
        try
        {
            list.SelectedItems.Clear();
            foreach (var item in selection.Where(pane.Entries.Contains)) list.SelectedItems.Add(item);
        }
        finally { _applyingPaneSelection = false; }
    }

    private void ComparisonSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || _viewModel is null) return;
        _viewModel.SetComparisonSelection(list.SelectedItems?.OfType<DirectoryComparisonRow>() ?? []);
    }

    private void RemoteSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list || _viewModel is null) return;
        _viewModel.SetRemoteSelection(list.SelectedItems?.OfType<RemoteBrowserRow>() ?? []);
    }

    private async void RemoteListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || sender is not ListBox { SelectedItem: RemoteBrowserRow row }) return;
        await _viewModel.OpenRemoteEntryAsync(row);
    }

    private void LeftListDoubleTapped(object? sender, TappedEventArgs e) => OpenFromPane(_viewModel?.LeftPane);
    private void RightListDoubleTapped(object? sender, TappedEventArgs e) => OpenFromPane(_viewModel?.RightPane);

    private void LeftListScrolled(object? sender, ScrollChangedEventArgs e) => LoadNearEnd(e, _viewModel?.LeftPane);
    private void RightListScrolled(object? sender, ScrollChangedEventArgs e) => LoadNearEnd(e, _viewModel?.RightPane);
    private void SearchListScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || e.Source is not ScrollViewer viewer) return;
        if (viewer.Extent.Height - viewer.Offset.Y - viewer.Viewport.Height < 320)
            _ = _viewModel.LoadNextSearchPageAsync();
    }

    private void RescueSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.Key == Key.Down && this.FindControl<ListBox>("SearchSuggestionList") is { ItemCount: > 0 } suggestions)
        {
            suggestions.SelectedIndex = 0;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            _viewModel.DismissSearchSuggestions();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || _viewModel.RescueSearchCommand.CanExecute(null) != true) return;
        _viewModel.RescueSearchCommand.Execute(null);
        e.Handled = true;
    }

    private void MainWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.Handled) return;
        if (_viewModel.MultiRename?.IsOpen == true)
        {
            if (e.Key == Key.Escape) e.Handled = Execute(_viewModel.MultiRename.CloseCommand);
            return;
        }
        if (_viewModel.QuickView?.IsOpen == true)
        {
            var modalCommand = e.Key switch
            {
                Key.Escape => _viewModel.QuickView.CloseCommand,
                Key.PageUp => _viewModel.QuickView.PreviousCommand,
                Key.PageDown => _viewModel.QuickView.NextCommand,
                Key.Home when e.KeyModifiers.HasFlag(KeyModifiers.Control) => _viewModel.QuickView.FirstCommand,
                Key.End when e.KeyModifiers.HasFlag(KeyModifiers.Control) => _viewModel.QuickView.LastCommand,
                _ => null
            };
            if (modalCommand is not null) e.Handled = Execute(modalCommand);
            return;
        }
        if (e.Key == Key.F9)
        {
            e.Handled = Execute(_viewModel.ShowPageCommand, "Settings");
            return;
        }
        if (!_viewModel.IsFilesPage)
        {
            if (_viewModel.IsRemotePage && e.Key == Key.F3)
            {
                e.Handled = Execute(_viewModel.OpenQuickViewCommand);
                return;
            }
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
                e.Handled = Execute(_viewModel.ShowPageCommand, "Search");
            return;
        }
        var control = e.Source as Control;
        var activeFilter = this.FindControl<TextBox>(ReferenceEquals(_viewModel.ActivePane, _viewModel.LeftPane)
            ? "LeftQuickFilterBox"
            : "RightQuickFilterBox");
        var activeList = this.FindControl<ListBox>(ReferenceEquals(_viewModel.ActivePane, _viewModel.LeftPane)
            ? "LeftFileList"
            : "RightFileList");
        var controlKey = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shiftKey = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (controlKey && e.Key == Key.F)
        {
            activeFilter?.Focus();
            activeFilter?.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _viewModel.ActivePane.HasActiveFilter)
        {
            _viewModel.ActivePane.FilterText = string.Empty;
            activeList?.Focus();
            e.Handled = true;
            return;
        }
        var tabShortcut = controlKey && (e.Key is Key.T or Key.W or Key.D);
        var multiRenameShortcut = controlKey && shiftKey && e.Key == Key.M;
        if (control is TextBox
            && e.Key is not (Key.F2 or Key.F3 or Key.F4 or Key.F5 or Key.F6 or Key.F7 or Key.F8)
            && !tabShortcut && !multiRenameShortcut)
            return;

        System.Windows.Input.ICommand? command = e.Key switch
        {
            Key.F2 => _viewModel.RenameEntryCommand,
            Key.F3 => _viewModel.OpenQuickViewCommand,
            Key.F4 => _viewModel.EditEntryCommand,
            Key.F5 => _viewModel.CopyEntryCommand,
            Key.F6 => _viewModel.MoveEntryCommand,
            Key.F7 => _viewModel.CreateFolderCommand,
            Key.F8 => _viewModel.TrashEntryCommand,
            Key.A when controlKey => _viewModel.SelectAllCommand,
            Key.I when controlKey => _viewModel.InvertSelectionCommand,
            Key.E when controlKey => _viewModel.SelectByExtensionCommand,
            Key.M when controlKey && shiftKey => _viewModel.OpenMultiRenameCommand,
            Key.M when controlKey => _viewModel.SelectByMaskCommand,
            Key.R when controlKey && shiftKey => _viewModel.RestorePreviousSelectionCommand,
            Key.T when controlKey && shiftKey => _viewModel.ReopenClosedTabCommand,
            Key.T when controlKey => _viewModel.NewTabCommand,
            Key.W when controlKey => _viewModel.CloseTabCommand,
            Key.D when controlKey && shiftKey => _viewModel.DuplicateTabCommand,
            _ => null
        };
        var parameter = command == _viewModel.NewTabCommand
                        || command == _viewModel.CloseTabCommand
                        || command == _viewModel.DuplicateTabCommand
                        || command == _viewModel.ReopenClosedTabCommand
            ? ReferenceEquals(_viewModel.ActivePane, _viewModel.RightPane) ? "Right" : "Left"
            : null;
        e.Handled = Execute(command, parameter);
    }

    private void SearchSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        // Clearing the bound collection from inside SelectionChanged re-enters Avalonia's
        // selection model while it is still enumerating the old selection. Capture the item
        // now and update the collection on the next dispatcher cycle instead.
        if (_viewModel is null
            || _suggestionAcceptanceQueued
            || sender is not ListBox { SelectedItem: SearchSuggestion suggestion }) return;

        _suggestionAcceptanceQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                _viewModel?.AcceptSearchSuggestion(suggestion);
                if (this.FindControl<TextBox>("RescueSearchBox") is not { } searchBox) return;
                searchBox.Focus();
                searchBox.CaretIndex = searchBox.Text?.Length ?? 0;
            }
            finally
            {
                _suggestionAcceptanceQueued = false;
            }
        }, DispatcherPriority.Background);
    }

    private async void MainWindowOpened(object? sender, EventArgs e)
    {
        _windowOpen = true;
        UpdateTipTimer();
        if (this.FindControl<StackPanel>("RescueHero") is not { } hero) return;
        hero.Opacity = 0;
        await Task.Delay(45);
        if (_windowOpen) hero.Opacity = 1;
    }

    private async void RotateRescueTip(object? sender, EventArgs e)
    {
        if (_tipTransitioning || _viewModel?.ShowRescueWelcome != true) return;
        if (this.FindControl<TextBlock>("RescueTipText") is not { } tipText) return;
        var quoteText = this.FindControl<TextBlock>("OdysseyQuoteText");
        _tipTransitioning = true;
        try
        {
            tipText.Opacity = 0;
            if (quoteText is not null) quoteText.Opacity = 0;
            await Task.Delay(190);
            if (!_windowOpen || _viewModel?.ShowRescueWelcome != true) return;
            _viewModel.AdvanceRescueTip();
            tipText.Opacity = 1;
            if (quoteText is not null) quoteText.Opacity = 1;
        }
        finally
        {
            _tipTransitioning = false;
        }
    }

    private void UpdateTipTimer()
    {
        if (_windowOpen && !_searchHasFocus && !_tipPointerOver) _rescueTipTimer.Start();
        else _rescueTipTimer.Stop();
    }

    private static void LoadNearEnd(ScrollChangedEventArgs e, FilePaneViewModel? pane)
    {
        if (pane is null || e.Source is not ScrollViewer viewer) return;
        if (viewer.Extent.Height - viewer.Offset.Y - viewer.Viewport.Height < 320)
            _ = pane.LoadNextPageAsync();
    }

    private static void UpdatePaneSelection(object? sender, FilePaneViewModel? pane)
    {
        if (sender is not ListBox list || pane is null) return;
        pane.SetSelection(list.SelectedItems?.OfType<BrowserEntry>() ?? []);
    }

    private void OpenFromPane(FilePaneViewModel? pane)
    {
        if (_viewModel is null || pane is null) return;
        pane.Activate();
        if (_viewModel.OpenResultCommand.CanExecute(null)) _viewModel.OpenResultCommand.Execute(null);
    }

    private void OpenMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.OpenResultCommand);
    private void EditMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.EditEntryCommand);
    private void RenameMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.RenameEntryCommand);
    private void CopyMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.CopyEntryCommand);
    private void MoveMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.MoveEntryCommand);
    private void TrashMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.TrashEntryCommand);
    private void CopyPathMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.CopyPathCommand);
    private void SelectAllMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.SelectAllCommand);
    private void InvertSelectionMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.InvertSelectionCommand);
    private void SelectExtensionMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.SelectByExtensionCommand);
    private void SelectMaskMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.SelectByMaskCommand);
    private void RestoreSelectionMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.RestorePreviousSelectionCommand);

    private static bool Execute(System.Windows.Input.ICommand? command, object? parameter = null)
    {
        if (command?.CanExecute(parameter) != true) return false;
        command.Execute(parameter);
        return true;
    }
}
