using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Odyssey.Core;

namespace Odyssey.Desktop;

public sealed record CategoryFilter(string Name, FileCategory? Category);
public sealed record FileOperationHistoryRow(string Operation, string Path, string CompletedAt, bool CanUndo);
public sealed record BrowserEntry(
    string Name, string FullPath, FileEntryType Type, long? Size, DateTimeOffset? ModifiedAt,
    bool IsParent = false, string Extension = "", string Attributes = "");

public sealed class FilePaneViewModel(IDirectoryBrowserService browser) : ObservableObject
{
    private const int PageSize = 400;
    private ScanTarget? _selectedTarget;
    private BrowserEntry? _selectedEntry;
    private IReadOnlyList<BrowserEntry> _selectedEntries = [];
    private string _currentPath = string.Empty;
    private string _spaceDisplay = string.Empty;
    private string? _lastError;
    private bool _isActive;
    private bool _isLoading;
    private bool _hasMore;
    private int _nextOffset;
    private int _totalItems;
    private long _loadGeneration;
    private CancellationTokenSource? _loadCancellation;
    private readonly Stack<string> _backHistory = new();
    private readonly Stack<string> _forwardHistory = new();

    public event EventHandler? Activated;
    public event EventHandler? StateChanged;
    public ObservableCollection<BrowserEntry> Entries { get; } = [];

    public ScanTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;
            Activate();
            _backHistory.Clear();
            _forwardHistory.Clear();
            if (value is not null) BrowseTo(value.RootPath, recordHistory: false);
        }
    }

    public BrowserEntry? SelectedEntry { get => _selectedEntry; private set => SetProperty(ref _selectedEntry, value); }
    public IReadOnlyList<BrowserEntry> SelectedEntries => _selectedEntries;
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value)) NotifyPresentationState();
        }
    }
    public string SpaceDisplay { get => _spaceDisplay; private set => SetProperty(ref _spaceDisplay, value); }
    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (SetProperty(ref _lastError, value)) NotifyPresentationState();
        }
    }
    public bool IsActive { get => _isActive; internal set => SetProperty(ref _isActive, value); }
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value)) NotifyPresentationState();
        }
    }
    public bool HasMore { get => _hasMore; private set => SetProperty(ref _hasMore, value); }
    public int TotalItems { get => _totalItems; private set => SetProperty(ref _totalItems, value); }
    public bool CanGoBack => _backHistory.Count > 0;
    public bool CanGoForward => _forwardHistory.Count > 0;
    public bool ShowInitialLoading => IsLoading && Entries.All(item => item.IsParent);
    public bool ShowEmptyState => !IsLoading && LastError is null && !string.IsNullOrWhiteSpace(CurrentPath)
                                  && Entries.All(item => item.IsParent);
    public bool ShowErrorState => !IsLoading && LastError is not null;
    public string SelectionSummary
    {
        get
        {
            var count = _selectedEntries.Count(item => !item.IsParent);
            var bytes = _selectedEntries.Where(item => !item.IsParent).Sum(item => item.Size ?? 0);
            return count == 0 ? $"{Entries.Count(item => !item.IsParent):N0} / {TotalItems:N0}" : $"{count:N0}  •  {FormatBytes(bytes)}";
        }
    }

    public void Activate() => Activated?.Invoke(this, EventArgs.Empty);

    public void SetSelection(IEnumerable<BrowserEntry> entries)
    {
        _selectedEntries = entries.Distinct().ToArray();
        SelectedEntry = _selectedEntries.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(SelectionSummary));
        if (_selectedEntries.Count > 0) Activate();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NavigateUp()
    {
        if (SelectedTarget is null || string.IsNullOrWhiteSpace(CurrentPath)) return;
        var parent = Directory.GetParent(CurrentPath)?.FullName;
        if (parent is not null && IsInside(parent, SelectedTarget.RootPath)) BrowseTo(parent);
    }

    public void GoBack()
    {
        if (_backHistory.Count == 0) return;
        var destination = _backHistory.Pop();
        if (!string.IsNullOrWhiteSpace(CurrentPath)) _forwardHistory.Push(CurrentPath);
        BrowseTo(destination, recordHistory: false);
    }

    public void GoForward()
    {
        if (_forwardHistory.Count == 0) return;
        var destination = _forwardHistory.Pop();
        if (!string.IsNullOrWhiteSpace(CurrentPath)) _backHistory.Push(CurrentPath);
        BrowseTo(destination, recordHistory: false);
    }

    public void OpenSelectedDirectory() { if (SelectedEntry?.Type == FileEntryType.Directory) BrowseTo(SelectedEntry.FullPath); }

    public void Refresh()
    {
        var path = Directory.Exists(CurrentPath) ? CurrentPath : SelectedTarget?.RootPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        browser.Invalidate(path);
        BrowseTo(path, recordHistory: false);
    }

    public void BrowseTo(string path, bool recordHistory = true)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) throw new DirectoryNotFoundException(path);
        if (SelectedTarget is not null && !IsInside(directory.FullName, SelectedTarget.RootPath))
            throw new UnauthorizedAccessException("The path is outside the selected location.");
        if (recordHistory && !string.IsNullOrWhiteSpace(CurrentPath) && !PathEquals(CurrentPath, directory.FullName))
        {
            _backHistory.Push(CurrentPath);
            _forwardHistory.Clear();
        }
        CurrentPath = directory.FullName;
        Entries.Clear();
        if (SelectedTarget is not null && !PathEquals(directory.FullName, SelectedTarget.RootPath))
            Entries.Add(new BrowserEntry("..", directory.Parent?.FullName ?? directory.FullName, FileEntryType.Directory, null, null, IsParent: true));
        NotifyPresentationState();
        _selectedEntries = [];
        SelectedEntry = null;
        _nextOffset = 0;
        TotalItems = 0;
        HasMore = true;
        LastError = null;
        UpdateSpaceDisplay(directory.FullName);
        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        var generation = Interlocked.Increment(ref _loadGeneration);
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        _ = LoadNextPageCoreAsync(generation, _loadCancellation.Token);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task LoadNextPageAsync() =>
        HasMore && !IsLoading && _loadCancellation is not null
            ? LoadNextPageCoreAsync(_loadGeneration, _loadCancellation.Token)
            : Task.CompletedTask;

    private async Task LoadNextPageCoreAsync(long generation, CancellationToken token)
    {
        if (IsLoading || !HasMore || string.IsNullOrWhiteSpace(CurrentPath)) return;
        IsLoading = true;
        try
        {
            var page = await browser.GetPageAsync(CurrentPath, _nextOffset, PageSize, token);
            if (generation != _loadGeneration || token.IsCancellationRequested) return;
            foreach (var item in page.Items)
                Entries.Add(new BrowserEntry(item.Name, item.FullPath, item.Type, item.Size, item.ModifiedAt,
                    Extension: item.Extension, Attributes: item.Attributes));
            NotifyPresentationState();
            _nextOffset += page.Items.Count;
            TotalItems = page.TotalCount;
            HasMore = page.HasMore;
            OnPropertyChanged(nameof(SelectionSummary));
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LastError = ex.Message;
            HasMore = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { if (generation == _loadGeneration) IsLoading = false; }
    }

    private void NotifyPresentationState()
    {
        OnPropertyChanged(nameof(ShowInitialLoading));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowErrorState));
    }

    private void UpdateSpaceDisplay(string path)
    {
        try
        {
            var drive = DriveInfo.GetDrives().Where(item => IsInside(path, item.RootDirectory.FullName))
                .OrderByDescending(item => item.RootDirectory.FullName.Length).FirstOrDefault();
            SpaceDisplay = drive is { IsReady: true } ? $"{FormatBytes(drive.AvailableFreeSpace)} / {FormatBytes(drive.TotalSize)}" : string.Empty;
        }
        catch { SpaceDisplay = string.Empty; }
    }

    private static bool IsInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedPath = Normalize(path); var normalizedRoot = Normalize(root);
        return string.Equals(normalizedPath, normalizedRoot, comparison) || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
    private static bool PathEquals(string left, string right) => PathComparer.Equals(Normalize(left), Normalize(right));
    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)Math.Max(0, bytes); var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

public sealed class MainViewModel : ObservableObject
{
    private const int SearchPageSize = 250;
    private const int RescueTipCount = 5;
    private readonly IOdysseyStore _store;
    private readonly IScanCoordinator _scanner;
    private readonly ISearchService _search;
    private readonly ISystemSearchHistoryService _systemSearchHistory;
    private readonly IDesktopInteractionService _desktop;
    private readonly LocalizationService _localization;
    private readonly IStorageVolumeDiscovery _volumeDiscovery;
    private readonly IDirectoryBrowserService _directoryBrowser;
    private readonly IDiskManagementService _diskManagement;
    private readonly IFileOperationService _fileOperations;
    private readonly UserPreferencesService _preferences;
    private readonly IBackgroundAutomationService _automation;
    private readonly IOcrCapability _ocr;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _volumeDiscoveryGate = new(1, 1);
    private readonly SemaphoreSlim _searchPageGate = new(1, 1);
    private readonly HashSet<string> _connectedVolumePaths = new(PathComparer);
    private readonly Dictionary<string, StorageVolume> _volumesByRoot = new(PathComparer);
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _suggestionCancellation;
    private CancellationTokenSource? _volumeMonitorCancellation;
    private CancellationTokenSource? _fileOperationCancellation;
    private int _searchGeneration;
    private int _suggestionGeneration;
    private bool _searchHasMore;
    private bool _searchPageLoading;
    private bool _suppressNextSuggestionRefresh;
    private int _rescueTipIndex;
    private bool _initialized;
    private string _activePage = "Rescue";
    private RescueSession? _workspace;
    private ScanTarget? _selectedTarget;
    private SearchResult? _selectedResult;
    private CategoryFilter? _selectedCategory;
    private bool _targetRecursive = true;
    private string _targetExclusions = string.Join(", ", new[] { ".git", "node_modules", "bin", "obj", ".cache" });
    private string _searchText = string.Empty;
    private string _extensionFilter = string.Empty;
    private string _minimumSize = string.Empty;
    private string _maximumSize = string.Empty;
    private string _modifiedFrom = string.Empty;
    private string _modifiedTo = string.Empty;
    private bool _filterSelectedTarget;
    private bool _showAdvancedFilters;
    private bool _automaticDrives;
    private bool _readOnlyMode;
    private bool _isFileOperationRunning;
    private string _fileOperationProgress = string.Empty;
    private FilePaneViewModel _activePane;
    private bool _isBusy;
    private bool _isSearching;
    private bool _hasSearchError;
    private bool _isScanning;
    private string _statusMessage;
    private string _scanState;
    private ScanStatus? _lastScanStatus;
    private string _currentPath = string.Empty;
    private long _scanFiles;
    private long _scanDirectories;
    private long _scanIndexed;
    private long _scanBytes;
    private long _scanErrors;
    private string _scanElapsed = "00:00:00";
    private BackgroundAutomationStatus _backgroundStatus = new(BackgroundActivity.Idle);
    private string _backgroundStatusMessage = string.Empty;

    public MainViewModel(
        IOdysseyStore store,
        IScanCoordinator scanner,
        ISearchService search,
        ISystemSearchHistoryService systemSearchHistory,
        IDesktopInteractionService desktop,
        LocalizationService localization,
        IStorageVolumeDiscovery volumeDiscovery,
        IDirectoryBrowserService directoryBrowser,
        IDiskManagementService diskManagement,
        IFileOperationService fileOperations,
        UserPreferencesService preferences,
        IBackgroundAutomationService automation,
        IOcrCapability ocr,
        UpdateViewModel? updater = null)
    {
        _store = store;
        _scanner = scanner;
        _search = search;
        _systemSearchHistory = systemSearchHistory;
        _desktop = desktop;
        _localization = localization;
        _volumeDiscovery = volumeDiscovery;
        _directoryBrowser = directoryBrowser;
        _diskManagement = diskManagement;
        _fileOperations = fileOperations;
        _preferences = preferences;
        _automation = automation;
        _ocr = ocr;
        Updater = updater;
        _automation.StatusChanged += OnBackgroundStatusChanged;
        _readOnlyMode = preferences.ReadOnlyMode;
        _fileOperations.AccessMode = _readOnlyMode ? FileAccessMode.ReadOnly : FileAccessMode.ManageFiles;
        _automaticDrives = preferences.AutomaticDrives && !string.Equals(
            Environment.GetEnvironmentVariable("ODYSSEY_DISABLE_AUTOMATIC_DRIVES"), "1", StringComparison.Ordinal);
        LeftPane = new FilePaneViewModel(directoryBrowser);
        RightPane = new FilePaneViewModel(directoryBrowser);
        _activePane = LeftPane;
        LeftPane.IsActive = true;
        LeftPane.Activated += (_, _) => SetActivePane(LeftPane);
        RightPane.Activated += (_, _) => SetActivePane(RightPane);
        LeftPane.StateChanged += (_, _) => OnPaneStateChanged(LeftPane);
        RightPane.StateChanged += (_, _) => OnPaneStateChanged(RightPane);
        _statusMessage = localization["Starting"];
        _scanState = localization["Ready"];
        Targets.CollectionChanged += (_, _) => RaiseRescueState();
        SearchResults.CollectionChanged += (_, _) => RaiseRescueState();
        SearchSuggestions.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasSearchSuggestions));
        RebuildCategoryFilters();
        _localization.LanguageChanged += OnLanguageChanged;

        ShowPageCommand = new RelayCommand<string?>(ShowPage);
        RescueSearchCommand = new AsyncRelayCommand(SearchFromRescueAsync);
        AddTargetCommand = new AsyncRelayCommand(AddTargetAsync, () => _workspace is not null && !IsScanning);
        UpdateTargetCommand = new AsyncRelayCommand(UpdateTargetAsync, () => SelectedTarget is not null && !IsScanning);
        RemoveTargetCommand = new AsyncRelayCommand(RemoveTargetAsync, () => SelectedTarget is not null && !IsScanning);
        StartScanCommand = new AsyncRelayCommand(StartScanAsync, () => SelectedTarget is not null && !IsScanning);
        CancelScanCommand = new RelayCommand(CancelScan, () => IsScanning);
        SearchNowCommand = new AsyncRelayCommand(SearchFromUiAsync);
        CopyPathCommand = new AsyncRelayCommand(CopyPathAsync, HasSelectedEntry);
        OpenResultCommand = new AsyncRelayCommand(OpenResultAsync, HasAvailableSelectedEntry);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync, HasSelectedEntry);
        NavigateUpCommand = new RelayCommand(NavigateUp, () => IsFilesPage && !string.IsNullOrWhiteSpace(CurrentDirectoryPath));
        ActivatePaneCommand = new RelayCommand<string?>(side => SetActivePane(side == "Right" ? RightPane : LeftPane));
        NavigateLeftUpCommand = new RelayCommand(() => NavigatePaneUp(LeftPane), () => LeftPane.SelectedTarget is not null);
        NavigateRightUpCommand = new RelayCommand(() => NavigatePaneUp(RightPane), () => RightPane.SelectedTarget is not null);
        NavigateLeftBackCommand = new RelayCommand(() => NavigatePane(LeftPane, back: true), () => LeftPane.CanGoBack);
        NavigateLeftForwardCommand = new RelayCommand(() => NavigatePane(LeftPane, back: false), () => LeftPane.CanGoForward);
        NavigateRightBackCommand = new RelayCommand(() => NavigatePane(RightPane, back: true), () => RightPane.CanGoBack);
        NavigateRightForwardCommand = new RelayCommand(() => NavigatePane(RightPane, back: false), () => RightPane.CanGoForward);
        RefreshLeftPaneCommand = new RelayCommand(() => TryRefreshPane(LeftPane));
        RefreshRightPaneCommand = new RelayCommand(() => TryRefreshPane(RightPane));
        ToggleAccessModeCommand = new AsyncRelayCommand(ToggleAccessModeAsync, () => !IsFileOperationRunning);
        CreateFolderCommand = new AsyncRelayCommand(CreateFolderAsync, CanCreateFolder);
        RenameEntryCommand = new AsyncRelayCommand(RenameEntryAsync, CanRenameSelectedEntry);
        CopyEntryCommand = new AsyncRelayCommand(CopyEntryAsync, CanManageSelectedEntry);
        MoveEntryCommand = new AsyncRelayCommand(MoveEntryAsync, CanManageSelectedEntry);
        TrashEntryCommand = new AsyncRelayCommand(TrashEntryAsync, CanManageSelectedEntry);
        UndoFileOperationCommand = new AsyncRelayCommand(UndoFileOperationAsync, CanUndoFileOperation);
        CancelFileOperationCommand = new RelayCommand(CancelFileOperation, () => IsFileOperationRunning);
        UnmountDriveCommand = new AsyncRelayCommand(() => DisconnectDriveAsync(eject: false), CanDisconnectDrive);
        EjectDriveCommand = new AsyncRelayCommand(() => DisconnectDriveAsync(eject: true), CanDisconnectDrive);
    }

    public ObservableCollection<ScanTarget> Targets { get; } = [];
    public ObservableCollection<SearchResult> SearchResults { get; } = [];
    public ObservableCollection<SearchSuggestion> SearchSuggestions { get; } = [];
    public ObservableCollection<CategoryFilter> CategoryFilters { get; } = [];
    public ObservableCollection<FileOperationHistoryRow> FileOperationHistory { get; } = [];
    public FilePaneViewModel LeftPane { get; }
    public FilePaneViewModel RightPane { get; }
    public FilePaneViewModel ActivePane => _activePane;
    public FilePaneViewModel PassivePane => ReferenceEquals(_activePane, LeftPane) ? RightPane : LeftPane;
    public LocalizationService Localization => _localization;
    public UpdateViewModel? Updater { get; }

    public ICommand ShowPageCommand { get; }
    public IAsyncRelayCommand RescueSearchCommand { get; }
    public IAsyncRelayCommand AddTargetCommand { get; }
    public IAsyncRelayCommand UpdateTargetCommand { get; }
    public IAsyncRelayCommand RemoveTargetCommand { get; }
    public IAsyncRelayCommand StartScanCommand { get; }
    public IRelayCommand CancelScanCommand { get; }
    public IAsyncRelayCommand SearchNowCommand { get; }
    public IAsyncRelayCommand CopyPathCommand { get; }
    public IAsyncRelayCommand OpenResultCommand { get; }
    public IAsyncRelayCommand OpenFolderCommand { get; }
    public IRelayCommand NavigateUpCommand { get; }
    public IRelayCommand<string?> ActivatePaneCommand { get; }
    public IRelayCommand NavigateLeftUpCommand { get; }
    public IRelayCommand NavigateRightUpCommand { get; }
    public IRelayCommand NavigateLeftBackCommand { get; }
    public IRelayCommand NavigateLeftForwardCommand { get; }
    public IRelayCommand NavigateRightBackCommand { get; }
    public IRelayCommand NavigateRightForwardCommand { get; }
    public IRelayCommand RefreshLeftPaneCommand { get; }
    public IRelayCommand RefreshRightPaneCommand { get; }
    public IAsyncRelayCommand ToggleAccessModeCommand { get; }
    public IAsyncRelayCommand CreateFolderCommand { get; }
    public IAsyncRelayCommand RenameEntryCommand { get; }
    public IAsyncRelayCommand CopyEntryCommand { get; }
    public IAsyncRelayCommand MoveEntryCommand { get; }
    public IAsyncRelayCommand TrashEntryCommand { get; }
    public IAsyncRelayCommand UndoFileOperationCommand { get; }
    public IRelayCommand CancelFileOperationCommand { get; }
    public IAsyncRelayCommand UnmountDriveCommand { get; }
    public IAsyncRelayCommand EjectDriveCommand { get; }

    public ScanTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;
            if (value is not null)
            {
                TargetRecursive = value.Recursive;
                TargetExclusions = string.Join(", ", value.ExcludedPatterns);
            }
            NotifyCommandStates();
            OnPropertyChanged(nameof(CanUnmountSelectedDrive));
            if (_initialized && IsFilesPage && value is not null && !ReferenceEquals(ActivePane.SelectedTarget, value))
                ActivePane.SelectedTarget = value;
            _ = DebounceSearchAsync();
        }
    }

    public SearchResult? SelectedResult
    {
        get => _selectedResult;
        set
        {
            if (!SetProperty(ref _selectedResult, value)) return;
            OnPropertyChanged(nameof(HasNoSelectedResult));
            OnPropertyChanged(nameof(HasSelectedResult));
            OnPropertyChanged(nameof(HasSelectedMatch));
            OnPropertyChanged(nameof(SelectedResultSizeDisplay));
            OnPropertyChanged(nameof(SelectedResultProbabilityDisplay));
            OnPropertyChanged(nameof(SelectedResultConfidenceDetail));
            NotifyCommandStates();
        }
    }

    public string CurrentDirectoryPath => ActivePane.CurrentPath;

    public CategoryFilter? SelectedCategory
    {
        get => _selectedCategory;
        set { if (SetProperty(ref _selectedCategory, value)) _ = DebounceSearchAsync(); }
    }

    public bool TargetRecursive { get => _targetRecursive; set => SetProperty(ref _targetRecursive, value); }
    public string TargetExclusions { get => _targetExclusions; set => SetProperty(ref _targetExclusions, value); }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            SearchResults.Clear();
            SelectedResult = null;
            HasSearchError = false;
            RaiseRescueState();
            if (_suppressNextSuggestionRefresh) _suppressNextSuggestionRefresh = false;
            else _ = DebounceSuggestionsAsync();
            _ = DebounceSearchAsync();
        }
    }
    public string ExtensionFilter { get => _extensionFilter; set { if (SetProperty(ref _extensionFilter, value)) _ = DebounceSearchAsync(); } }
    public string MinimumSize { get => _minimumSize; set { if (SetProperty(ref _minimumSize, value)) _ = DebounceSearchAsync(); } }
    public string MaximumSize { get => _maximumSize; set { if (SetProperty(ref _maximumSize, value)) _ = DebounceSearchAsync(); } }
    public string ModifiedFrom { get => _modifiedFrom; set { if (SetProperty(ref _modifiedFrom, value)) _ = DebounceSearchAsync(); } }
    public string ModifiedTo { get => _modifiedTo; set { if (SetProperty(ref _modifiedTo, value)) _ = DebounceSearchAsync(); } }
    public bool FilterSelectedTarget { get => _filterSelectedTarget; set { if (SetProperty(ref _filterSelectedTarget, value)) _ = DebounceSearchAsync(); } }
    public bool ShowAdvancedFilters { get => _showAdvancedFilters; set => SetProperty(ref _showAdvancedFilters, value); }
    public bool AutomaticDrives
    {
        get => _automaticDrives;
        set
        {
            if (!SetProperty(ref _automaticDrives, value)) return;
            _preferences.AutomaticDrives = value;
            if (!value || !_initialized) return;
            _ = DiscoverVolumesAsync(initial: false, _volumeMonitorCancellation?.Token ?? CancellationToken.None);
        }
    }

    public bool ReadOnlyMode
    {
        get => _readOnlyMode;
        private set
        {
            if (!SetProperty(ref _readOnlyMode, value)) return;
            _fileOperations.AccessMode = value ? FileAccessMode.ReadOnly : FileAccessMode.ManageFiles;
            _preferences.ReadOnlyMode = value;
            OnPropertyChanged(nameof(IsFileManagementMode));
            OnPropertyChanged(nameof(AccessModeDisplay));
            OnPropertyChanged(nameof(AccessModeHelp));
            OnPropertyChanged(nameof(AccessModeAction));
            NotifyCommandStates();
        }
    }
    public bool IsFileManagementMode => !ReadOnlyMode;
    public string AccessModeDisplay => $"{(ReadOnlyMode ? "🔒" : "🔓")} {_localization[ReadOnlyMode ? "ReadOnly" : "FileManagement"]}";
    public string AccessModeHelp => _localization[ReadOnlyMode ? "ReadOnlyHelp" : "FileManagementHelp"];
    public string AccessModeAction => _localization[ReadOnlyMode ? "EnableFileManagement" : "EnableReadOnly"];
    public bool IsFileOperationRunning
    {
        get => _isFileOperationRunning;
        private set { if (SetProperty(ref _isFileOperationRunning, value)) NotifyCommandStates(); }
    }
    public string FileOperationProgress { get => _fileOperationProgress; private set => SetProperty(ref _fileOperationProgress, value); }
    public bool CanUnmountSelectedDrive => ResolveSelectedVolume()?.CanUnmount == true;

    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) NotifyCommandStates(); } }
    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (!SetProperty(ref _isSearching, value)) return;
            OnPropertyChanged(nameof(RescueSearchButtonText));
            RaiseRescueState();
        }
    }
    public bool HasSearchError
    {
        get => _hasSearchError;
        private set
        {
            if (!SetProperty(ref _hasSearchError, value)) return;
            RaiseRescueState();
        }
    }
    public bool IsScanning { get => _isScanning; private set { if (SetProperty(ref _isScanning, value)) NotifyCommandStates(); } }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string ScanState { get => _scanState; private set => SetProperty(ref _scanState, value); }
    public string CurrentPath { get => _currentPath; private set => SetProperty(ref _currentPath, value); }
    public long ScanFiles { get => _scanFiles; private set => SetProperty(ref _scanFiles, value); }
    public long ScanDirectories { get => _scanDirectories; private set => SetProperty(ref _scanDirectories, value); }
    public long ScanIndexed { get => _scanIndexed; private set => SetProperty(ref _scanIndexed, value); }
    public long ScanBytes { get => _scanBytes; private set { if (SetProperty(ref _scanBytes, value)) OnPropertyChanged(nameof(ScanBytesDisplay)); } }
    public long ScanErrors { get => _scanErrors; private set => SetProperty(ref _scanErrors, value); }
    public string ScanElapsed { get => _scanElapsed; private set => SetProperty(ref _scanElapsed, value); }
    public string ScanBytesDisplay => FormatBytes(ScanBytes);
    public string BackgroundStatusMessage { get => _backgroundStatusMessage; private set => SetProperty(ref _backgroundStatusMessage, value); }
    public string OcrStatusDisplay => _ocr.IsAvailable
        ? _localization.Format("OcrReady", _localization["OcrLanguages"])
        : _localization["OcrUnavailable"];

    public bool HasTargets => Targets.Count > 0;
    public bool HasNoTargets => Targets.Count == 0;
    public bool HasNoSelectedResult => SelectedResult is null;
    public bool HasSelectedResult => SelectedResult is not null;
    public bool HasSelectedMatch => !string.IsNullOrWhiteSpace(SelectedResult?.MatchSnippet);
    public bool HasSearchSuggestions => SearchSuggestions.Count > 0;
    public string SelectedResultSizeDisplay => SelectedResult?.Size is long size ? FormatBytes(size) : "—";
    public string SelectedResultProbabilityDisplay => SelectedResult is null
        ? string.Empty
        : _localization.Format("MatchProbabilityValue", Math.Round(SelectedResult.Score * 100));
    public string SelectedResultConfidenceDetail => SelectedResult is null
        ? string.Empty
        : ExplainConfidence(SelectedResult.MatchEvidence);
    public bool HasSearchQuery => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasSearchResults => SearchResults.Count > 0;
    public bool ShowRescueWelcome => HasTargets && !HasSearchQuery;
    public bool ShowRescueNoResults => HasTargets && HasSearchQuery && !HasSearchResults && !IsSearching && !HasSearchError;
    public bool ShowRescueResults => HasSearchQuery && HasSearchResults;
    public string LocationsSummary => _localization.Format("RescueLocations", Targets.Count);
    public string RescueTipText => _localization[$"RescueRotatingTip{_rescueTipIndex + 1}"];
    public string RescueSearchButtonText => IsSearching
        ? _localization["SearchingButton"]
        : _localization["StartSearching"];
    public string RescueStatusTitle => IsSearching
        ? _localization["RescueSearchingTitle"]
        : HasSearchError
            ? _localization["RescueSearchFailedTitle"]
        : ShowRescueResults
            ? _localization.Format("RescueFoundTitle", SearchResults.Count)
            : ShowRescueNoResults
                ? _localization["RescueNoResultsTitle"]
                : HasNoTargets
                    ? _localization["RescueAddLocationTitle"]
                    : _localization["RescueWelcomeTitle"];
    public string RescueStatusDetail => IsSearching
        ? _localization["RescueSearchingDetail"]
        : HasSearchError
            ? _localization["RescueSearchFailedDetail"]
        : ShowRescueResults
            ? _localization["RescueFoundDetail"]
            : ShowRescueNoResults
                ? _localization["RescueNoResultsDetail"]
                : HasNoTargets
                    ? _localization["RescueAddLocationDetail"]
                    : _localization["RescueWelcomeDetail"];
    public bool IsRescuePage => _activePage == "Rescue";
    public bool IsExpertPage => !IsRescuePage;
    public bool IsFilesPage => _activePage == "Files";
    public bool IsSearchPage => _activePage == "Search";
    public bool IsTargetsPage => _activePage == "Targets";
    public bool IsSettingsPage => _activePage == "Settings";

    public void AdvanceRescueTip()
    {
        _rescueTipIndex = (_rescueTipIndex + 1) % RescueTipCount;
        OnPropertyChanged(nameof(RescueTipText));
    }
    public string DatabasePath => _store.DatabasePath;

    public async Task InitializeAsync(Action<string>? reportStatus = null)
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            IsBusy = true;
            reportStatus?.Invoke(_localization["SplashOpeningIndex"]);
            await _store.InitializeAsync();
            await _search.InitializeAsync();
            reportStatus?.Invoke(_localization["SplashLoadingWorkspace"]);
            var sessions = await _store.GetSessionsAsync();
            if (sessions.Count == 0)
            {
                var workspace = await _store.SaveSessionAsync(new RescueSession
                {
                    Id = Guid.NewGuid(),
                    Name = "Odyssey workspace",
                    Mode = SessionMode.ForensicReadOnly,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                sessions = [workspace];
            }
            _workspace = sessions.First();
            NotifyCommandStates();
            StatusMessage = _localization["IndexLoaded"];
            await LoadWorkspaceAsync(reportStatus);
            reportStatus?.Invoke(_localization["SplashStartingServices"]);
            await _automation.StartAsync(_workspace.Id, Targets.ToArray());
            _volumeMonitorCancellation = new CancellationTokenSource();
            _ = MonitorVolumesAsync(_volumeMonitorCancellation.Token);
            reportStatus?.Invoke(_localization["SplashReady"]);
        }
        catch (Exception ex) { StatusMessage = _localization.Format("InitializationFailed", ex.Message); }
        finally { IsBusy = false; }
    }

    public void CancelActiveWork()
    {
        _scanCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _suggestionCancellation?.Cancel();
        _volumeMonitorCancellation?.Cancel();
        _fileOperationCancellation?.Cancel();
        _ = _automation.StopAsync();
    }

    private void SetActivePane(FilePaneViewModel pane)
    {
        _automation.NotifyUserActivity();
        if (ReferenceEquals(_activePane, pane))
        {
            pane.IsActive = true;
            return;
        }
        _activePane.IsActive = false;
        _activePane = pane;
        pane.IsActive = true;
        if (pane.SelectedTarget is not null)
        {
            _selectedTarget = pane.SelectedTarget;
            OnPropertyChanged(nameof(SelectedTarget));
        }
        OnPropertyChanged(nameof(ActivePane));
        OnPropertyChanged(nameof(PassivePane));
        OnPropertyChanged(nameof(CurrentDirectoryPath));
        NotifyCommandStates();
    }

    private void OnPaneStateChanged(FilePaneViewModel pane)
    {
        if (!string.IsNullOrWhiteSpace(pane.LastError))
            StatusMessage = _localization.Format("BrowseFailed", pane.LastError);
        if (!ReferenceEquals(pane, _activePane)) return;
        if (pane.SelectedTarget is not null && !ReferenceEquals(_selectedTarget, pane.SelectedTarget))
        {
            _selectedTarget = pane.SelectedTarget;
            OnPropertyChanged(nameof(SelectedTarget));
        }
        OnPropertyChanged(nameof(CurrentDirectoryPath));
        NotifyCommandStates();
    }

    private void NavigatePaneUp(FilePaneViewModel pane)
    {
        try { pane.NavigateUp(); }
        catch (Exception ex) { StatusMessage = _localization.Format("BrowseFailed", ex.Message); }
    }

    private void NavigatePane(FilePaneViewModel pane, bool back)
    {
        try { if (back) pane.GoBack(); else pane.GoForward(); }
        catch (Exception ex) { StatusMessage = _localization.Format("BrowseFailed", ex.Message); }
    }

    private void ShowPage(string? page)
    {
        _activePage = page is "Files" or "Search" or "Targets" or "Settings" or "Rescue" ? page : "Rescue";
        OnPropertyChanged(nameof(IsRescuePage));
        OnPropertyChanged(nameof(IsExpertPage));
        OnPropertyChanged(nameof(IsFilesPage));
        OnPropertyChanged(nameof(IsSearchPage));
        OnPropertyChanged(nameof(IsTargetsPage)); OnPropertyChanged(nameof(IsSettingsPage));
        if (IsFilesPage && SelectedTarget is not null && string.IsNullOrWhiteSpace(CurrentDirectoryPath))
            ActivePane.SelectedTarget = SelectedTarget;
        NotifyCommandStates();
    }

    private async Task LoadWorkspaceAsync(Action<string>? reportStatus = null)
    {
        _searchCancellation?.Cancel();
        Targets.Clear(); SelectedTarget = null;
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasNoTargets));
        if (_workspace is null) return;
        try
        {
            var targets = await _store.GetTargetsAsync(_workspace.Id);
            foreach (var target in targets) Targets.Add(target);
            OnPropertyChanged(nameof(HasTargets));
            OnPropertyChanged(nameof(HasNoTargets));
            SelectedTarget = Targets.FirstOrDefault();

            reportStatus?.Invoke(_localization["SplashWarmingSearch"]);
            var warmSearch = IgnoreWarmupFailureAsync(() => _search.WarmupAsync(_workspace.Id));
            var warmSystemHistory = IgnoreWarmupFailureAsync(() => _systemSearchHistory.WarmupAsync());
            var firstResults = SearchNowAsync();

            reportStatus?.Invoke(_localization["SplashPreparingFolders"]);
            var warmDirectories = Targets
                .Select(target => target.RootPath)
                .Where(Directory.Exists)
                .Distinct(PathComparer)
                .Take(2)
                .Select(path => IgnoreWarmupFailureAsync(() =>
                    _directoryBrowser.GetPageAsync(path, 0, 400)))
                .ToArray();

            await Task.WhenAll(warmDirectories.Append(warmSearch).Append(warmSystemHistory)
                .Append(firstResults));

            // File panes now consume the already populated bounded directory cache.
            LeftPane.SelectedTarget = Targets.FirstOrDefault();
            RightPane.SelectedTarget = Targets.Skip(1).FirstOrDefault() ?? Targets.FirstOrDefault();
            SetActivePane(LeftPane);
        }
        catch (Exception ex) { StatusMessage = _localization.Format("LoadTargetsFailed", ex.Message); }
    }

    private static async Task IgnoreWarmupFailureAsync(Func<Task> warmup)
    {
        try { await warmup(); }
        catch (OperationCanceledException) { }
        catch { /* Optional warmup must never prevent the rescue UI from opening. */ }
    }

    private static async Task IgnoreWarmupFailureAsync<T>(Func<Task<T>> warmup)
    {
        try { await warmup(); }
        catch (OperationCanceledException) { }
        catch { /* Optional warmup must never prevent the rescue UI from opening. */ }
    }

    private async Task AddTargetAsync()
    {
        if (_workspace is null) return;
        var returnToRescue = IsRescuePage;
        try
        {
            var path = await _desktop.PickFolderAsync();
            if (string.IsNullOrWhiteSpace(path)) return;
            var existing = Targets.FirstOrDefault(target => PathComparer.Equals(NormalizePath(target.RootPath), NormalizePath(path)));
            if (existing is not null)
            {
                SelectedTarget = existing;
                ActivePane.SelectedTarget = existing;
                if (!returnToRescue) ShowPage("Files");
                await ScanTargetAsync(existing, CancellationToken.None);
                return;
            }
            var exclusions = TargetExclusions.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var target = await _store.SaveTargetAsync(new ScanTarget
            {
                Id = Guid.NewGuid(),
                SessionId = _workspace.Id,
                RootPath = path,
                Recursive = TargetRecursive,
                ExcludedPatterns = exclusions
            });
            Targets.Add(target); SelectedTarget = target;
            _automation.UpdateTargets(Targets.ToArray());
            OnPropertyChanged(nameof(HasTargets));
            OnPropertyChanged(nameof(HasNoTargets));
            StatusMessage = _localization["TargetAdded"];
            ActivePane.SelectedTarget = target;
            if (!returnToRescue) ShowPage("Files");
            await ScanTargetAsync(target, CancellationToken.None);
        }
        catch (Exception ex) { StatusMessage = _localization.Format("TargetAddFailed", ex.Message); }
    }

    private async Task RemoveTargetAsync()
    {
        if (SelectedTarget is null) return;
        var target = SelectedTarget;
        await _store.RemoveTargetAsync(target.Id);
        Targets.Remove(target); SelectedTarget = Targets.FirstOrDefault();
        _automation.UpdateTargets(Targets.ToArray());
        if (LeftPane.SelectedTarget?.Id == target.Id) LeftPane.SelectedTarget = Targets.FirstOrDefault();
        if (RightPane.SelectedTarget?.Id == target.Id) RightPane.SelectedTarget = Targets.Skip(1).FirstOrDefault() ?? Targets.FirstOrDefault();
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(HasNoTargets));
        StatusMessage = _localization["TargetRemoved"];
        await SearchNowAsync();
    }

    private async Task UpdateTargetAsync()
    {
        if (SelectedTarget is null) return;
        try
        {
            var exclusions = TargetExclusions.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var updated = await _store.SaveTargetAsync(SelectedTarget with
            {
                Recursive = TargetRecursive,
                ExcludedPatterns = exclusions
            });
            var index = Targets.IndexOf(SelectedTarget);
            Targets[index] = updated;
            if (LeftPane.SelectedTarget?.Id == updated.Id) LeftPane.SelectedTarget = updated;
            if (RightPane.SelectedTarget?.Id == updated.Id) RightPane.SelectedTarget = updated;
            _selectedTarget = updated;
            _automation.UpdateTargets(Targets.ToArray());
            OnPropertyChanged(nameof(SelectedTarget));
            StatusMessage = _localization["TargetSaved"];
        }
        catch (Exception ex) { StatusMessage = _localization.Format("TargetSaveFailed", ex.Message); }
    }

    private async Task StartScanAsync()
    {
        if (SelectedTarget is null) return;
        await ScanTargetAsync(SelectedTarget, CancellationToken.None);
    }

    private async Task ScanTargetAsync(ScanTarget target, CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);
        SelectedTarget = target;
        _scanCancellation?.Dispose();
        _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IsScanning = true; _lastScanStatus = ScanStatus.Running; ScanState = _localization.TranslateEnum(_lastScanStatus);
        ScanFiles = ScanDirectories = ScanIndexed = ScanBytes = ScanErrors = 0;
        var progress = new Progress<ScanProgress>(value =>
        {
            ScanFiles = value.FilesDiscovered; ScanDirectories = value.DirectoriesDiscovered;
            ScanIndexed = value.EntriesIndexed; ScanBytes = value.BytesObserved; ScanErrors = value.Errors;
            ScanElapsed = value.Elapsed.ToString(@"hh\:mm\:ss"); CurrentPath = value.CurrentPath ?? string.Empty;
        });
        try
        {
            var completed = await _scanner.ScanAsync(target, progress, _scanCancellation.Token);
            _lastScanStatus = completed.Status;
            ScanState = _localization.TranslateEnum(completed.Status);
            StatusMessage = completed.Status == ScanStatus.Completed
                ? _localization["ScanCompletedMessage"]
                : _localization.Format("ScanEnded", _localization.TranslateEnum(completed.Status));
            if (completed.Status == ScanStatus.Completed) _automation.NotifyTargetScanned(target);
            await SearchNowAsync();
        }
        catch (Exception ex)
        {
            _lastScanStatus = ScanStatus.Failed;
            ScanState = _localization.TranslateEnum(_lastScanStatus);
            StatusMessage = _localization.Format("ScanFailedMessage", ex.Message);
        }
        finally
        {
            IsScanning = false;
            _scanGate.Release();
        }
    }

    private void CancelScan() { _scanCancellation?.Cancel(); ScanState = _localization["Cancelling"]; }

    private async Task MonitorVolumesAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (AutomaticDrives) await DiscoverVolumesAsync(initial: true, cancellationToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (AutomaticDrives) await DiscoverVolumesAsync(initial: false, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = _localization.Format("DriveDiscoveryFailed", ex.Message);
        }
    }

    private async Task DiscoverVolumesAsync(bool initial, CancellationToken cancellationToken)
    {
        if (_workspace is null || !AutomaticDrives) return;
        await _volumeDiscoveryGate.WaitAsync(cancellationToken);
        try
        {
            var volumes = await _volumeDiscovery.DiscoverAsync(cancellationToken);
            _volumesByRoot.Clear();
            foreach (var volume in volumes) _volumesByRoot[NormalizePath(volume.RootPath)] = volume;
            OnPropertyChanged(nameof(CanUnmountSelectedDrive));
            NotifyCommandStates();
            var currentPaths = volumes.Select(volume => NormalizePath(volume.RootPath)).ToHashSet(PathComparer);
            var connectedNow = volumes.Where(volume => !_connectedVolumePaths.Contains(NormalizePath(volume.RootPath))).ToArray();
            _connectedVolumePaths.Clear();
            foreach (var path in currentPaths) _connectedVolumePaths.Add(path);

            foreach (var volume in connectedNow)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = NormalizePath(volume.RootPath);
                var target = Targets.FirstOrDefault(item => PathComparer.Equals(NormalizePath(item.RootPath), normalized));
                var created = target is null;
                if (target is null)
                {
                    target = await _store.SaveTargetAsync(new ScanTarget
                    {
                        Id = Guid.NewGuid(),
                        SessionId = _workspace.Id,
                        RootPath = volume.RootPath,
                        Recursive = true,
                        ExcludedPatterns = TargetExclusions.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    }, cancellationToken);
                    Targets.Add(target);
                    _automation.UpdateTargets(Targets.ToArray());
                    OnPropertyChanged(nameof(HasTargets));
                    OnPropertyChanged(nameof(HasNoTargets));
                    StatusMessage = _localization.Format("DriveDetected", volume.Name);
                    if (LeftPane.SelectedTarget is null) LeftPane.SelectedTarget = target;
                    else RightPane.SelectedTarget = target;
                    if (IsExpertPage) ShowPage("Files");
                }

                var hasCompletedScan = await _store.HasCompletedScanAsync(target.Id, cancellationToken);
                if (created || !hasCompletedScan || !initial)
                    await ScanTargetAsync(target, cancellationToken);
            }
        }
        finally
        {
            _volumeDiscoveryGate.Release();
        }
    }

    private async Task DebounceSearchAsync()
    {
        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        try { await Task.Delay(280, cancellation.Token); await ExecuteSearchAsync(cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            HasSearchError = true;
            StatusMessage = _localization.Format("SearchFailed", ex.Message);
        }
    }

    private async Task DebounceSuggestionsAsync()
    {
        _suggestionCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _suggestionCancellation = cancellation;
        var generation = Interlocked.Increment(ref _suggestionGeneration);
        var query = SearchText;
        if (query.Trim().Length < 2)
        {
            SearchSuggestions.Clear();
            return;
        }

        try
        {
            await Task.Delay(140, cancellation.Token);
            var suggestions = await _search.SuggestAsync(new SearchSuggestionRequest
            {
                Query = query,
                SessionId = _workspace?.Id,
                Limit = 7
            }, cancellation.Token);
            if (generation != Volatile.Read(ref _suggestionGeneration)) return;
            SearchSuggestions.Clear();
            var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var suggestion in suggestions)
            {
                if (seen.Add(suggestion.Text)) SearchSuggestions.Add(suggestion);
                if (SearchSuggestions.Count >= 7) break;
            }
            foreach (var text in _systemSearchHistory.Suggest(query, 7 - SearchSuggestions.Count))
            {
                if (seen.Add(text)) SearchSuggestions.Add(new SearchSuggestion(text, SearchSuggestionKind.SystemHistory));
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (generation == Volatile.Read(ref _suggestionGeneration)) SearchSuggestions.Clear();
        }
    }

    private async Task SearchNowAsync()
    {
        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        try { await ExecuteSearchAsync(cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            HasSearchError = true;
            StatusMessage = _localization.Format("SearchFailed", ex.Message);
        }
    }

    private async Task SearchFromUiAsync()
    {
        ShowPage("Search");
        HideSearchSuggestions();
        await SearchNowAsync();
        if (!HasSearchError) await RememberCurrentSearchAsync();
    }

    private async Task SearchFromRescueAsync()
    {
        ShowPage("Rescue");
        HideSearchSuggestions();
        await SearchNowAsync();
        if (!HasSearchError) await RememberCurrentSearchAsync();
    }

    public void AcceptSearchSuggestion(SearchSuggestion? suggestion)
    {
        if (suggestion is null) return;
        HideSearchSuggestions();
        _suppressNextSuggestionRefresh = true;
        SearchText = suggestion.Text;
    }

    public void DismissSearchSuggestions() => HideSearchSuggestions();

    private void HideSearchSuggestions()
    {
        _suggestionCancellation?.Cancel();
        Interlocked.Increment(ref _suggestionGeneration);
        SearchSuggestions.Clear();
    }

    private async Task RememberCurrentSearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        try { await _search.RememberSearchAsync(SearchText, _workspace?.Id); }
        catch { /* Search history must never prevent the actual search. */ }
    }

    private async Task ExecuteSearchAsync(CancellationToken cancellationToken, bool append = false)
    {
        _automation.NotifyUserActivity();
        var generation = append ? Volatile.Read(ref _searchGeneration) : Interlocked.Increment(ref _searchGeneration);
        await _searchPageGate.WaitAsync(cancellationToken);
        try
        {
            if (generation != Volatile.Read(ref _searchGeneration)) return;
            HasSearchError = false;
            IsSearching = true;
            var request = new SearchRequest
            {
                Query = SearchText,
                Extension = string.IsNullOrWhiteSpace(ExtensionFilter) ? null : ExtensionFilter,
                Category = SelectedCategory?.Category,
                MinimumSize = TryLong(MinimumSize),
                MaximumSize = TryLong(MaximumSize),
                ModifiedFrom = TryDate(ModifiedFrom),
                ModifiedTo = TryDate(ModifiedTo),
                SessionId = _workspace?.Id,
                TargetId = FilterSelectedTarget ? SelectedTarget?.Id : null,
                Limit = SearchPageSize,
                Offset = append ? SearchResults.Count : 0
            };
            var response = await _search.SearchAsync(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _searchGeneration)) return;
            if (!append) SearchResults.Clear();
            foreach (var result in response.Results) SearchResults.Add(result);
            _searchHasMore = response.HasMore;
            if (!append) SelectedResult = SearchResults.FirstOrDefault();
            StatusMessage = _localization.Format("SearchResults", SearchResults.Count, response.Elapsed.TotalMilliseconds);
            RaiseRescueState();
        }
        finally
        {
            if (generation == Volatile.Read(ref _searchGeneration)) IsSearching = false;
            _searchPageGate.Release();
        }
    }

    public async Task LoadNextSearchPageAsync()
    {
        if (!_searchHasMore || _searchPageLoading || _searchCancellation is null) return;
        _searchPageLoading = true;
        try { await ExecuteSearchAsync(_searchCancellation.Token, append: true); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { StatusMessage = _localization.Format("SearchFailed", ex.Message); }
        finally { _searchPageLoading = false; }
    }

    private Task CopyPathAsync()
    {
        var entry = GetSelectedEntry() ?? throw new InvalidOperationException(_localization["NoItemSelected"]);
        return RunDesktopAction(() => _desktop.CopyTextAsync(entry.Path), _localization["PathCopied"]);
    }

    private Task OpenResultAsync()
    {
        var entry = GetSelectedEntry() ?? throw new InvalidOperationException(_localization["NoItemSelected"]);
        if (IsFilesPage && entry.Type == FileEntryType.Directory)
        {
            try { ActivePane.BrowseTo(entry.Path); }
            catch (Exception ex) { StatusMessage = _localization.Format("BrowseFailed", ex.Message); }
            return Task.CompletedTask;
        }
        return RunDesktopAction(() => _desktop.OpenAsync(entry.Path), _localization["Opened"]);
    }

    private Task OpenFolderAsync()
    {
        var entry = GetSelectedEntry() ?? throw new InvalidOperationException(_localization["NoItemSelected"]);
        return RunDesktopAction(() => _desktop.OpenContainingFolderAsync(entry.Path), _localization["FolderOpened"]);
    }

    private void NavigateUp()
    {
        NavigatePaneUp(ActivePane);
    }

    private async Task ToggleAccessModeAsync()
    {
        if (!ReadOnlyMode)
        {
            ReadOnlyMode = true;
            StatusMessage = _localization["ReadOnlyEnabled"];
            return;
        }

        var confirmed = await _desktop.ConfirmAsync(
            _localization["EnableFileManagementTitle"],
            _localization["EnableFileManagementWarning"],
            _localization["EnableFileManagement"]);
        if (!confirmed) return;
        ReadOnlyMode = false;
        StatusMessage = _localization["FileManagementEnabled"];
    }

    private async Task CreateFolderAsync()
    {
        var selected = GetSelectedEntry();
        var parent = IsFilesPage && !string.IsNullOrWhiteSpace(CurrentDirectoryPath)
            ? CurrentDirectoryPath
            : selected is not null
                ? (selected.Value.Type == FileEntryType.Directory ? selected.Value.Path : Path.GetDirectoryName(selected.Value.Path))
                : SelectedTarget?.RootPath;
        if (string.IsNullOrWhiteSpace(parent)) return;
        var name = await _desktop.PromptTextAsync(
            _localization["CreateFolderTitle"], _localization["CreateFolderPrompt"]);
        if (name is null) return;
        await RunFileOperationAsync(
            token => _fileOperations.CreateDirectoryAsync(parent, name, token),
            _localization["FolderCreated"]);
    }

    private async Task RenameEntryAsync()
    {
        var selection = GetSelectedEntries();
        if (selection.Count != 1 || selection[0].IsParent) return;
        var source = selection[0].Path;
        var name = await _desktop.PromptTextAsync(
            _localization["RenameTitle"], _localization["RenamePrompt"], Path.GetFileName(source));
        if (name is null) return;
        await RunFileOperationAsync(
            token => _fileOperations.RenameAsync(source, name, token),
            _localization["RenameCompleted"]);
    }

    private async Task CopyEntryAsync()
    {
        var selected = GetSelectedEntries().Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0) return;
        var destination = Directory.Exists(PassivePane.CurrentPath)
            ? PassivePane.CurrentPath
            : await _desktop.PickDestinationFolderAsync();
        if (destination is null) return;
        if (!ValidateBatchDestination(selected.Select(item => item.Path), destination)) return;
        await RunFileOperationAsync(
            token => RunBatchAsync(selected.Select(item => item.Path),
                (source, cancellation) => _fileOperations.CopyAsync(source, destination, CreateFileProgress(), cancellation), token),
            _localization.Format("CopyBatchCompleted", selected.Length));
    }

    private async Task MoveEntryAsync()
    {
        var selected = GetSelectedEntries().Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0) return;
        var destination = Directory.Exists(PassivePane.CurrentPath)
            ? PassivePane.CurrentPath
            : await _desktop.PickDestinationFolderAsync();
        if (destination is null) return;
        if (!ValidateBatchDestination(selected.Select(item => item.Path), destination)) return;
        var confirmed = await _desktop.ConfirmAsync(
            _localization["MoveTitle"],
            _localization.Format("MoveBatchConfirmation", selected.Length, destination),
            _localization["Move"]);
        if (!confirmed) return;
        await RunFileOperationAsync(
            token => RunBatchAsync(selected.Select(item => item.Path),
                (source, cancellation) => _fileOperations.MoveAsync(source, destination, CreateFileProgress(), cancellation), token),
            _localization.Format("MoveBatchCompleted", selected.Length));
    }

    private async Task TrashEntryAsync()
    {
        var selected = GetSelectedEntries().Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0) return;
        var confirmed = await _desktop.ConfirmAsync(
            _localization["TrashTitle"],
            _localization.Format("TrashBatchConfirmation", selected.Length),
            _localization["MoveToTrash"]);
        if (!confirmed) return;
        await RunFileOperationAsync(
            token => RunBatchAsync(selected.Select(item => item.Path),
                (source, cancellation) => _fileOperations.TrashAsync(source, cancellation), token),
            _localization.Format("TrashBatchCompleted", selected.Length));
    }

    private static async Task<FileOperationRecord> RunBatchAsync(
        IEnumerable<string> sources,
        Func<string, CancellationToken, Task<FileOperationRecord>> operation,
        CancellationToken cancellationToken)
    {
        FileOperationRecord? last = null;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await operation(source, cancellationToken);
        }
        return last ?? throw new InvalidOperationException("No item was selected.");
    }

    private bool ValidateBatchDestination(IEnumerable<string> sources, string destination)
    {
        var planned = new HashSet<string>(PathComparer);
        foreach (var source in sources)
        {
            var target = Path.Combine(destination, Path.GetFileName(source));
            if (!planned.Add(target) || File.Exists(target) || Directory.Exists(target))
            {
                StatusMessage = _localization.Format("DestinationConflict", target);
                return false;
            }
        }
        return true;
    }

    private async Task UndoFileOperationAsync()
    {
        await RunFileOperationAsync(
            async token => await _fileOperations.UndoLastAsync(token)
                           ?? throw new InvalidOperationException(_localization["NothingToUndo"]),
            _localization["UndoCompleted"]);
    }

    private async Task RunFileOperationAsync(
        Func<CancellationToken, Task<FileOperationRecord>> operation,
        string successMessage)
    {
        _automation.NotifyUserActivity();
        _fileOperationCancellation?.Dispose();
        _fileOperationCancellation = new CancellationTokenSource();
        IsFileOperationRunning = true;
        FileOperationProgress = _localization["OperationStarting"];
        var previousOperationIds = _fileOperations.History.Select(item => item.Id).ToHashSet();
        try
        {
            var record = await operation(_fileOperationCancellation.Token);
            await SynchronizeCompletedOperationsAsync(previousOperationIds, record.IsUndo ? record : null);
            FileOperationProgress = string.Empty;
            StatusMessage = successMessage;
        }
        catch (OperationCanceledException)
        {
            await SynchronizeCompletedOperationsAsync(previousOperationIds, null);
            StatusMessage = _localization["OperationCancelled"];
        }
        catch (Exception ex)
        {
            await SynchronizeCompletedOperationsAsync(previousOperationIds, null);
            StatusMessage = _localization.Format("OperationFailed", ex.Message);
        }
        finally
        {
            FileOperationProgress = string.Empty;
            IsFileOperationRunning = false;
        }
    }

    private async Task SynchronizeCompletedOperationsAsync(HashSet<Guid> previousOperationIds, FileOperationRecord? extra)
    {
        var completed = _fileOperations.History
            .Where(item => !previousOperationIds.Contains(item.Id))
            .Reverse()
            .ToList();
        if (extra?.IsUndo == true) completed.Add(extra);
        if (completed.Count == 0) return;
        await _store.ApplyFileOperationsAsync(completed, Targets.ToArray(), CancellationToken.None);
        RebuildFileOperationHistory();
        TryRefreshPane(LeftPane);
        TryRefreshPane(RightPane);
        await SearchNowAsync();
    }

    private IProgress<FileOperationProgress> CreateFileProgress() => new Progress<FileOperationProgress>(progress =>
    {
        FileOperationProgress = progress.TotalBytes is > 0
            ? _localization.Format("OperationProgress", FormatBytes(progress.BytesCompleted), FormatBytes(progress.TotalBytes.Value))
            : _localization.Format("OperationProgressUnknown", progress.CurrentPath);
    });

    private void CancelFileOperation() => _fileOperationCancellation?.Cancel();

    private void TryRefreshPane(FilePaneViewModel pane)
    {
        try { pane.Refresh(); }
        catch (Exception ex) { StatusMessage = _localization.Format("BrowseFailed", ex.Message); }
    }

    private async Task DisconnectDriveAsync(bool eject)
    {
        var volume = ResolveSelectedVolume();
        if (volume is null || !volume.CanUnmount) return;
        var confirmed = await _desktop.ConfirmAsync(
            _localization[eject ? "EjectDriveTitle" : "UnmountDriveTitle"],
            _localization.Format(eject ? "EjectDriveConfirmation" : "UnmountDriveConfirmation", volume.Name),
            _localization[eject ? "EjectDrive" : "UnmountDrive"]);
        if (!confirmed) return;
        try
        {
            if (eject) await _diskManagement.EjectAsync(volume);
            else await _diskManagement.UnmountAsync(volume);
            _connectedVolumePaths.Remove(NormalizePath(volume.RootPath));
            _volumesByRoot.Remove(NormalizePath(volume.RootPath));
            OnPropertyChanged(nameof(CanUnmountSelectedDrive));
            NotifyCommandStates();
            StatusMessage = _localization[eject ? "DriveEjected" : "DriveUnmounted"];
        }
        catch (Exception ex) { StatusMessage = _localization.Format("DriveOperationFailed", ex.Message); }
    }

    private StorageVolume? ResolveSelectedVolume()
    {
        if (SelectedTarget is null) return null;
        return _volumesByRoot.GetValueOrDefault(NormalizePath(SelectedTarget.RootPath));
    }

    private (string Path, FileEntryType Type, bool Available, bool IsParent)? GetSelectedEntry()
    {
        if (IsFilesPage && ActivePane.SelectedEntry is not null)
            return (ActivePane.SelectedEntry.FullPath, ActivePane.SelectedEntry.Type, true, ActivePane.SelectedEntry.IsParent);
        if ((IsSearchPage || IsRescuePage) && SelectedResult is not null)
            return (SelectedResult.FullPath, SelectedResult.Type, !SelectedResult.IsMissing, false);
        return null;
    }


    private IReadOnlyList<(string Path, FileEntryType Type, bool Available, bool IsParent)> GetSelectedEntries()
    {
        if (IsFilesPage)
            return ActivePane.SelectedEntries
                .Select(item => (item.FullPath, item.Type, true, item.IsParent))
                .ToArray();
        var selected = GetSelectedEntry();
        return selected is null ? [] : [selected.Value];
    }

    private bool HasSelectedEntry() => GetSelectedEntry() is not null;
    private bool HasAvailableSelectedEntry() => GetSelectedEntry() is { Available: true };
    private bool CanManageSelectedEntry() => !ReadOnlyMode && !IsFileOperationRunning
                                             && GetSelectedEntries().Any(item => item is { Available: true, IsParent: false });
    private bool CanRenameSelectedEntry() => !ReadOnlyMode && !IsFileOperationRunning
                                             && GetSelectedEntries() is { Count: 1 }
                                             && GetSelectedEntries()[0] is { Available: true, IsParent: false };
    private bool CanCreateFolder() => !ReadOnlyMode && !IsFileOperationRunning
                                      && (!string.IsNullOrWhiteSpace(CurrentDirectoryPath) || SelectedTarget is not null);
    private bool CanUndoFileOperation() => !ReadOnlyMode && !IsFileOperationRunning && _fileOperations.History.Any(item => item.CanUndo);
    private bool CanDisconnectDrive() => !IsScanning && !IsFileOperationRunning && ResolveSelectedVolume()?.CanUnmount == true;

    private void RebuildFileOperationHistory()
    {
        FileOperationHistory.Clear();
        foreach (var item in _fileOperations.History.Take(20))
        {
            FileOperationHistory.Add(new FileOperationHistoryRow(
                _localization.TranslateEnum(item.Kind),
                item.DestinationPath ?? item.SourcePath,
                item.CompletedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                item.CanUndo));
        }
        NotifyCommandStates();
    }

    private async Task RunDesktopAction(Func<Task> action, string success)
    {
        try { await action(); StatusMessage = success; }
        catch (Exception ex) { StatusMessage = ex.Message; }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildCategoryFilters();
        ScanState = _lastScanStatus is null
            ? _localization["Ready"]
            : _localization.TranslateEnum(_lastScanStatus.Value);
        StatusMessage = _localization["IndexLoaded"];
        BackgroundStatusMessage = FormatBackgroundStatus(_backgroundStatus);
        OnPropertyChanged(nameof(AccessModeDisplay));
        OnPropertyChanged(nameof(AccessModeHelp));
        OnPropertyChanged(nameof(AccessModeAction));
        OnPropertyChanged(nameof(OcrStatusDisplay));
        OnPropertyChanged(nameof(SelectedResultProbabilityDisplay));
        OnPropertyChanged(nameof(SelectedResultConfidenceDetail));
        OnPropertyChanged(nameof(RescueTipText));
        OnPropertyChanged(nameof(RescueSearchButtonText));
        RaiseRescueState();
        RebuildFileOperationHistory();

        var selectedId = SelectedResult?.FileId;
        var results = SearchResults.ToArray();
        SearchResults.Clear();
        foreach (var result in results) SearchResults.Add(result);
        SelectedResult = SearchResults.FirstOrDefault(result => result.FileId == selectedId);
    }

    private void OnBackgroundStatusChanged(object? sender, BackgroundAutomationStatus status) =>
        Dispatcher.UIThread.Post(() =>
        {
            _backgroundStatus = status;
            BackgroundStatusMessage = FormatBackgroundStatus(status);
            if (status.Activity == BackgroundActivity.IndexingContent && (IsSearchPage || IsRescuePage) && !string.IsNullOrWhiteSpace(SearchText))
                _ = DebounceSearchAsync();
        });

    private void RaiseRescueState()
    {
        OnPropertyChanged(nameof(HasSearchQuery));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowRescueWelcome));
        OnPropertyChanged(nameof(ShowRescueNoResults));
        OnPropertyChanged(nameof(ShowRescueResults));
        OnPropertyChanged(nameof(LocationsSummary));
        OnPropertyChanged(nameof(RescueStatusTitle));
        OnPropertyChanged(nameof(RescueStatusDetail));
    }

    private string FormatBackgroundStatus(BackgroundAutomationStatus status) => status.Activity switch
    {
        BackgroundActivity.Watching => _localization["BackgroundWatching"],
        BackgroundActivity.ScanningChanges => _localization.Format("BackgroundScanning", status.Path ?? string.Empty),
        BackgroundActivity.IndexingContent => _localization.Format("BackgroundContent", status.Completed),
        BackgroundActivity.MaintainingIndex => _localization["BackgroundMaintenance"],
        BackgroundActivity.WaitingForTarget => _localization.Format("BackgroundWaiting", status.Path ?? string.Empty),
        BackgroundActivity.Failed => _localization.Format("BackgroundFailed", status.Detail ?? string.Empty),
        _ => string.Empty
    };

    private void RebuildCategoryFilters()
    {
        var selected = _selectedCategory?.Category;
        CategoryFilters.Clear();
        CategoryFilters.Add(new CategoryFilter(_localization["AllCategories"], null));
        foreach (var category in Enum.GetValues<FileCategory>())
            CategoryFilters.Add(new CategoryFilter(_localization.TranslateEnum(category), category));
        _selectedCategory = CategoryFilters.FirstOrDefault(option => option.Category == selected) ?? CategoryFilters[0];
        OnPropertyChanged(nameof(SelectedCategory));
    }

    private void NotifyCommandStates()
    {
        AddTargetCommand.NotifyCanExecuteChanged();
        UpdateTargetCommand.NotifyCanExecuteChanged(); RemoveTargetCommand.NotifyCanExecuteChanged(); StartScanCommand.NotifyCanExecuteChanged(); CancelScanCommand.NotifyCanExecuteChanged();
        CopyPathCommand.NotifyCanExecuteChanged(); OpenResultCommand.NotifyCanExecuteChanged(); OpenFolderCommand.NotifyCanExecuteChanged();
        NavigateUpCommand.NotifyCanExecuteChanged();
        NavigateLeftUpCommand.NotifyCanExecuteChanged(); NavigateRightUpCommand.NotifyCanExecuteChanged();
        NavigateLeftBackCommand.NotifyCanExecuteChanged(); NavigateLeftForwardCommand.NotifyCanExecuteChanged();
        NavigateRightBackCommand.NotifyCanExecuteChanged(); NavigateRightForwardCommand.NotifyCanExecuteChanged();
        ToggleAccessModeCommand.NotifyCanExecuteChanged(); CreateFolderCommand.NotifyCanExecuteChanged(); RenameEntryCommand.NotifyCanExecuteChanged();
        CopyEntryCommand.NotifyCanExecuteChanged(); MoveEntryCommand.NotifyCanExecuteChanged(); TrashEntryCommand.NotifyCanExecuteChanged();
        UndoFileOperationCommand.NotifyCanExecuteChanged(); CancelFileOperationCommand.NotifyCanExecuteChanged();
        UnmountDriveCommand.NotifyCanExecuteChanged(); EjectDriveCommand.NotifyCanExecuteChanged();
    }

    private static long? TryLong(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value) ? value : null;

    private string ExplainConfidence(SearchMatchEvidence evidence)
    {
        if (evidence.HasFlag(SearchMatchEvidence.ExactName)) return _localization["ConfidenceExactName"];
        if (evidence.HasFlag(SearchMatchEvidence.Name) && evidence.HasFlag(SearchMatchEvidence.Content))
            return _localization["ConfidenceNameAndContent"];
        if (evidence.HasFlag(SearchMatchEvidence.Name)) return _localization["ConfidenceName"];
        if (evidence.HasFlag(SearchMatchEvidence.Content)) return _localization["ConfidenceContent"];
        if (evidence.HasFlag(SearchMatchEvidence.Path)) return _localization["ConfidencePath"];
        return _localization["ConfidenceGeneral"];
    }

    private static DateTimeOffset? TryDate(string text) => DateTimeOffset.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var value) ? value : null;
    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var value = (double)Math.Max(0, bytes); var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsPathInsideTarget(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(root);
        return string.Equals(normalizedPath, normalizedRoot, comparison)
               || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
