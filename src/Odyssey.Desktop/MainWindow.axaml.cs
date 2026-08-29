using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Odyssey.Core;

namespace Odyssey.Desktop;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _viewModel;
    private bool _suggestionAcceptanceQueued;

    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<ListBox>("LeftFileList")?.AddHandler(ScrollViewer.ScrollChangedEvent, LeftListScrolled);
        this.FindControl<ListBox>("RightFileList")?.AddHandler(ScrollViewer.ScrollChangedEvent, RightListScrolled);
        this.FindControl<ListBox>("SearchResultList")?.AddHandler(ScrollViewer.ScrollChangedEvent, SearchListScrolled);
        this.FindControl<ListBox>("RescueResultList")?.AddHandler(ScrollViewer.ScrollChangedEvent, SearchListScrolled);
        if (this.FindControl<TextBox>("RescueSearchBox") is { } rescueSearch)
            rescueSearch.KeyDown += RescueSearchKeyDown;
    }

    public MainWindow(MainViewModel viewModel) : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += (_, _) => viewModel.CancelActiveWork();
    }

    private void LeftSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdatePaneSelection(sender, _viewModel?.LeftPane);

    private void RightSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        UpdatePaneSelection(sender, _viewModel?.RightPane);

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
    private void RenameMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.RenameEntryCommand);
    private void CopyMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.CopyEntryCommand);
    private void MoveMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.MoveEntryCommand);
    private void TrashMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.TrashEntryCommand);
    private void CopyPathMenuClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Execute(_viewModel?.CopyPathCommand);

    private static void Execute(System.Windows.Input.ICommand? command)
    {
        if (command?.CanExecute(null) == true) command.Execute(null);
    }
}
