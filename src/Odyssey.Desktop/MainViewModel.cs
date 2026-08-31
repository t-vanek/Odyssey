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
public sealed record FileTransferQueueRow(
    Guid Id,
    string Operation,
    string Source,
    string Destination,
    string State,
    string Progress,
    string? Error,
    FileTransferState RawState);
public sealed record DirectoryComparisonRow(
    DirectoryComparisonEntry Entry,
    string RelativePath,
    string Difference,
    string LeftDetails,
    string RightDetails,
    string? Error);
public sealed record RemoteBrowserRow(
    FileLocationEntry Entry,
    string Name,
    string Type,
    string Size,
    string Modified);
public sealed record BrowserEntry(
    string Name, string FullPath, FileEntryType Type, long? Size, DateTimeOffset? ModifiedAt,
    bool IsParent = false, string Extension = "", string Attributes = "",
    FileTransferEndpointKind Endpoint = FileTransferEndpointKind.Local,
    string? ContainerPath = null,
    string? EntryPath = null,
    bool IsSymbolicLink = false)
{
    public string ModifiedDisplay => ModifiedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
}

public sealed record FilePaneTabSnapshot(
    Guid Id,
    string Path,
    string? TargetRoot,
    string FilterText,
    FileNameFilterMode FilterMode,
    bool FilterMatchCase,
    IReadOnlyList<string> BackHistory,
    IReadOnlyList<string> ForwardHistory,
    string? ArchivePath = null,
    string ArchiveDirectory = "");

public sealed record FilePaneWorkspaceSnapshot(
    IReadOnlyList<FilePaneTabSnapshot> Tabs,
    Guid? ActiveTabId);

public sealed class FilePaneTabViewModel : ObservableObject
{
    private string _path;
    private string? _archivePath;

    public FilePaneTabViewModel(Guid id, string path)
    {
        Id = id;
        _path = path;
    }

    public Guid Id { get; }
    public string Path
    {
        get => _path;
        internal set
        {
            if (!SetProperty(ref _path, value)) return;
            OnPropertyChanged(nameof(Title));
        }
    }
    public string Title
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(ArchivePath)) return System.IO.Path.GetFileName(ArchivePath);
            if (string.IsNullOrWhiteSpace(Path)) return "+";
            var trimmed = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            return System.IO.Path.GetFileName(trimmed) is { Length: > 0 } name
                ? name
                : System.IO.Path.GetPathRoot(Path) ?? Path;
        }
    }
    internal string? TargetRoot { get; set; }
    internal string FilterText { get; set; } = string.Empty;
    internal FileNameFilterMode FilterMode { get; set; }
    internal bool FilterMatchCase { get; set; }
    internal IReadOnlyList<string> BackHistory { get; set; } = [];
    internal IReadOnlyList<string> ForwardHistory { get; set; } = [];
    internal string? ArchivePath
    {
        get => _archivePath;
        set
        {
            if (!SetProperty(ref _archivePath, value)) return;
            OnPropertyChanged(nameof(Title));
        }
    }
    internal string ArchiveDirectory { get; set; } = string.Empty;
}

public sealed class FilePaneViewModel : ObservableObject
{
    private readonly IDirectoryBrowserService _browser;
    private readonly IArchiveService? _archives;
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
    private IReadOnlyList<BrowserEntry> _previousSelection = [];
    private HashSet<string> _selectionPathsToRestore = new(PathComparer);
    private string _filterText = string.Empty;
    private FileNameFilterMode _filterMode;
    private bool _filterMatchCase;
    private string? _filterError;
    private FilePaneTabViewModel? _selectedTab;
    private readonly Stack<FilePaneTabSnapshot> _closedTabs = new();
    private readonly object _archiveLoadSync = new();
    private bool _switchingTab;
    private IReadOnlyList<ScanTarget> _knownTargets = [];
    private string? _selectedHotlistPath;
    private string? _archivePath;
    private string _archiveDirectory = string.Empty;
    private IReadOnlyList<ArchiveEntry> _archiveEntries = [];

    public FilePaneViewModel(IDirectoryBrowserService browser, IArchiveService? archives = null)
    {
        _browser = browser;
        _archives = archives;
        var tab = new FilePaneTabViewModel(Guid.NewGuid(), string.Empty);
        Tabs.Add(tab);
        _selectedTab = tab;
    }

    public event EventHandler? Activated;
    public event EventHandler? StateChanged;
    public event EventHandler? WorkspaceChanged;
    public event EventHandler<IReadOnlyList<BrowserEntry>>? SelectionRequested;
    public ObservableCollection<BrowserEntry> Entries { get; } = [];
    public ObservableCollection<FilePaneTabViewModel> Tabs { get; } = [];
    public IReadOnlyList<FileNameFilterMode> FilterModes { get; } = Enum.GetValues<FileNameFilterMode>();

    public ScanTarget? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;
            Activate();
            _backHistory.Clear();
            _forwardHistory.Clear();
            if (_selectedTab is not null) _selectedTab.TargetRoot = value?.RootPath;
            if (value is not null) BrowseTo(value.RootPath, recordHistory: false);
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public FilePaneTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (value is null || ReferenceEquals(_selectedTab, value) || !Tabs.Contains(value)) return;
            SaveActiveTab();
            if (!SetProperty(ref _selectedTab, value)) return;
            ActivateTab(value);
        }
    }
    public bool CanCloseTab => Tabs.Count > 1;
    public bool CanReopenClosedTab => _closedTabs.Count > 0;
    public bool IsArchive => _archivePath is not null;
    public string? ArchivePath => _archivePath;
    public string ArchiveDirectory => _archiveDirectory;
    public string? SelectedHotlistPath
    {
        get => _selectedHotlistPath;
        set
        {
            if (SetProperty(ref _selectedHotlistPath, value)) WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public BrowserEntry? SelectedEntry { get => _selectedEntry; private set => SetProperty(ref _selectedEntry, value); }
    public IReadOnlyList<BrowserEntry> SelectedEntries => _selectedEntries;
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (!SetProperty(ref _currentPath, value)) return;
            if (!_switchingTab && _selectedTab is not null && !IsArchive) _selectedTab.Path = value;
            NotifyPresentationState();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
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
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (!SetProperty(ref _filterText, value ?? string.Empty)) return;
            if (!_switchingTab && _selectedTab is not null) _selectedTab.FilterText = _filterText;
            OnPropertyChanged(nameof(HasActiveFilter));
            ReloadForFilter();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public FileNameFilterMode FilterMode
    {
        get => _filterMode;
        set
        {
            if (!SetProperty(ref _filterMode, value)) return;
            if (!_switchingTab && _selectedTab is not null) _selectedTab.FilterMode = value;
            ReloadForFilter();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public bool FilterMatchCase
    {
        get => _filterMatchCase;
        set
        {
            if (!SetProperty(ref _filterMatchCase, value)) return;
            if (!_switchingTab && _selectedTab is not null) _selectedTab.FilterMatchCase = value;
            ReloadForFilter();
            WorkspaceChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string? FilterError
    {
        get => _filterError;
        private set
        {
            if (!SetProperty(ref _filterError, value)) return;
            OnPropertyChanged(nameof(HasFilterError));
            NotifyPresentationState();
        }
    }
    public bool HasActiveFilter => !string.IsNullOrEmpty(FilterText);
    public bool HasFilterError => FilterError is not null;
    public bool ShowInitialLoading => IsLoading && Entries.All(item => item.IsParent);
    public bool ShowEmptyState => !IsLoading && LastError is null && !HasActiveFilter
                                  && !string.IsNullOrWhiteSpace(CurrentPath) && TotalItems == 0;
    public bool ShowFilterEmptyState => !IsLoading && LastError is null && HasActiveFilter
                                        && !HasFilterError && TotalItems == 0;
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

    public void NewTab()
    {
        SaveActiveTab();
        var tab = new FilePaneTabViewModel(Guid.NewGuid(), IsArchive
            ? Path.GetDirectoryName(_archivePath!) ?? SelectedTarget?.RootPath ?? string.Empty
            : CurrentPath)
        {
            TargetRoot = SelectedTarget?.RootPath
        };
        Tabs.Add(tab);
        OnPropertyChanged(nameof(CanCloseTab));
        SelectedTab = tab;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DuplicateTab()
    {
        SaveActiveTab();
        if (_selectedTab is null) return;
        var tab = CreateTab(CaptureTab(_selectedTab) with { Id = Guid.NewGuid() });
        Tabs.Add(tab);
        OnPropertyChanged(nameof(CanCloseTab));
        SelectedTab = tab;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void CloseTab()
    {
        if (_selectedTab is null || Tabs.Count <= 1) return;
        SaveActiveTab();
        var closing = _selectedTab;
        var index = Tabs.IndexOf(closing);
        _closedTabs.Push(CaptureTab(closing));
        Tabs.Remove(closing);
        _selectedTab = null;
        OnPropertyChanged(nameof(SelectedTab));
        SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        OnPropertyChanged(nameof(CanCloseTab));
        OnPropertyChanged(nameof(CanReopenClosedTab));
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;
        SaveActiveTab();
        var tab = CreateTab(_closedTabs.Pop() with { Id = Guid.NewGuid() });
        Tabs.Add(tab);
        OnPropertyChanged(nameof(CanCloseTab));
        OnPropertyChanged(nameof(CanReopenClosedTab));
        SelectedTab = tab;
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public FilePaneWorkspaceSnapshot CaptureWorkspace()
    {
        SaveActiveTab();
        return new FilePaneWorkspaceSnapshot(Tabs.Select(CaptureTab).ToArray(), SelectedTab?.Id);
    }

    public void RestoreWorkspace(FilePaneWorkspaceSnapshot? workspace, IReadOnlyList<ScanTarget> targets)
    {
        _knownTargets = targets;
        if (workspace?.Tabs.Count is not > 0) return;
        _loadCancellation?.Cancel();
        Tabs.Clear();
        foreach (var snapshot in workspace.Tabs.Take(32)) Tabs.Add(CreateTab(snapshot));
        var selected = Tabs.FirstOrDefault(tab => tab.Id == workspace.ActiveTabId) ?? Tabs[0];
        _selectedTab = selected;
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(CanCloseTab));
        ActivateTab(selected, targets);
    }

    public void SetKnownTargets(IReadOnlyList<ScanTarget> targets) => _knownTargets = targets;

    public void SetSelection(IEnumerable<BrowserEntry> entries)
    {
        var next = entries.Where(item => Entries.Contains(item)).Distinct().ToArray();
        if (SelectionsEqual(_selectedEntries, next)) return;
        _previousSelection = _selectedEntries;
        _selectedEntries = next;
        SelectedEntry = _selectedEntries.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanRestorePreviousSelection));
        if (_selectedEntries.Count > 0) Activate();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CanRestorePreviousSelection => _previousSelection.Count > 0;

    public void SelectAll() => RequestSelection(Entries.Where(item => !item.IsParent));

    public void InvertSelection()
    {
        var selected = _selectedEntries.Select(item => item.FullPath).ToHashSet(PathComparer);
        RequestSelection(Entries.Where(item => !item.IsParent && !selected.Contains(item.FullPath)));
    }

    public void SelectByExtension()
    {
        var extension = SelectedEntry?.Extension;
        if (string.IsNullOrEmpty(extension)) return;
        RequestSelection(Entries.Where(item => !item.IsParent
            && string.Equals(item.Extension, extension, StringComparison.OrdinalIgnoreCase)));
    }

    public bool SelectByMask(string pattern, FileNameFilterMode mode, bool matchCase = false)
    {
        if (!FileNamePatternMatcher.TryCreate(new DirectoryNameFilter(pattern, mode, matchCase),
                out var matcher, out var error))
        {
            FilterError = error;
            return false;
        }
        FilterError = null;
        RequestSelection(Entries.Where(item => !item.IsParent && matcher(item.Name)));
        return true;
    }

    public void RestorePreviousSelection()
    {
        var visiblePaths = Entries.Select(item => item.FullPath).ToHashSet(PathComparer);
        var restore = _previousSelection.Where(item => visiblePaths.Contains(item.FullPath)).ToArray();
        if (restore.Length == 0) return;
        var current = _selectedEntries;
        _selectedEntries = restore;
        _previousSelection = current;
        SelectedEntry = _selectedEntries.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanRestorePreviousSelection));
        SelectionRequested?.Invoke(this, _selectedEntries);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void NavigateUp()
    {
        if (IsArchive)
        {
            if (_archiveDirectory.Length == 0)
            {
                var archiveParent = Path.GetDirectoryName(_archivePath!);
                if (archiveParent is not null) BrowseTo(archiveParent);
                return;
            }
            var separator = _archiveDirectory.LastIndexOf('/');
            OpenArchiveDirectory(separator < 0 ? string.Empty : _archiveDirectory[..separator]);
            return;
        }
        if (SelectedTarget is null || string.IsNullOrWhiteSpace(CurrentPath)) return;
        var parent = Directory.GetParent(CurrentPath)?.FullName;
        if (parent is not null && IsInside(parent, SelectedTarget.RootPath)) BrowseTo(parent);
    }

    public void GoBack()
    {
        if (_backHistory.Count == 0) return;
        var destination = _backHistory.Pop();
        if (!string.IsNullOrWhiteSpace(CurrentPath)) _forwardHistory.Push(GetLocationToken());
        NavigateToToken(destination);
    }

    public void GoForward()
    {
        if (_forwardHistory.Count == 0) return;
        var destination = _forwardHistory.Pop();
        if (!string.IsNullOrWhiteSpace(CurrentPath)) _backHistory.Push(GetLocationToken());
        NavigateToToken(destination);
    }

    public void OpenSelectedDirectory()
    {
        if (SelectedEntry?.Type != FileEntryType.Directory) return;
        if (SelectedEntry.Endpoint == FileTransferEndpointKind.Archive)
            OpenArchiveDirectory(SelectedEntry.EntryPath ?? string.Empty);
        else
            BrowseTo(SelectedEntry.FullPath);
    }

    public void Refresh()
    {
        if (IsArchive)
        {
            _ = LoadArchiveDirectoryAsync();
            return;
        }
        var path = Directory.Exists(CurrentPath) ? CurrentPath : SelectedTarget?.RootPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        _browser.Invalidate(path);
        BrowseTo(path, recordHistory: false);
    }

    public void BrowseTo(string path, bool recordHistory = true, bool preserveSelection = false)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) throw new DirectoryNotFoundException(path);
        if (SelectedTarget is not null && !IsInside(directory.FullName, SelectedTarget.RootPath))
            throw new UnauthorizedAccessException("The path is outside the selected location.");
        if (recordHistory && !string.IsNullOrWhiteSpace(CurrentPath)
            && (IsArchive || !PathEquals(CurrentPath, directory.FullName)))
        {
            _backHistory.Push(GetLocationToken());
            _forwardHistory.Clear();
        }
        _archivePath = null;
        _archiveDirectory = string.Empty;
        _archiveEntries = [];
        if (_selectedTab is not null)
        {
            _selectedTab.ArchivePath = null;
            _selectedTab.ArchiveDirectory = string.Empty;
        }
        OnPropertyChanged(nameof(IsArchive));
        OnPropertyChanged(nameof(ArchivePath));
        OnPropertyChanged(nameof(ArchiveDirectory));
        CurrentPath = directory.FullName;
        _selectionPathsToRestore = preserveSelection
            ? _selectedEntries.Select(item => item.FullPath).ToHashSet(PathComparer)
            : new HashSet<string>(PathComparer);
        Entries.Clear();
        if (SelectedTarget is not null && !PathEquals(directory.FullName, SelectedTarget.RootPath))
            Entries.Add(new BrowserEntry("..", directory.Parent?.FullName ?? directory.FullName, FileEntryType.Directory, null, null, IsParent: true));
        NotifyPresentationState();
        _selectedEntries = [];
        if (!preserveSelection) _previousSelection = [];
        SelectedEntry = null;
        _nextOffset = 0;
        TotalItems = 0;
        HasMore = true;
        LastError = null;
        UpdateSpaceDisplay(directory.FullName);
        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanRestorePreviousSelection));
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
        IsArchive
            ? LoadNextArchivePageAsync()
            : HasMore && !IsLoading && _loadCancellation is not null
            ? LoadNextPageCoreAsync(_loadGeneration, _loadCancellation.Token)
            : Task.CompletedTask;

    public bool CanOpenArchive(string path) => _archives?.CanOpen(path) == true;

    public void OpenArchive(string path)
    {
        if (_archives is null || !_archives.CanOpen(path)) throw new NotSupportedException();
        var archivePath = Path.GetFullPath(path);
        if (SelectedTarget is null || !IsInside(archivePath, SelectedTarget.RootPath))
            throw new UnauthorizedAccessException("The archive is outside the selected location.");
        if (!string.IsNullOrWhiteSpace(CurrentPath)) _backHistory.Push(GetLocationToken());
        _forwardHistory.Clear();
        _archivePath = archivePath;
        _archiveDirectory = string.Empty;
        if (_selectedTab is not null)
        {
            _selectedTab.ArchivePath = archivePath;
            _selectedTab.ArchiveDirectory = string.Empty;
        }
        OnPropertyChanged(nameof(IsArchive));
        OnPropertyChanged(nameof(ArchivePath));
        OnPropertyChanged(nameof(ArchiveDirectory));
        _ = LoadArchiveDirectoryAsync();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    public void OpenArchiveDirectory(string directory)
    {
        if (!IsArchive) return;
        var normalized = directory.Trim('/');
        if (!string.Equals(normalized, _archiveDirectory, StringComparison.Ordinal))
        {
            _backHistory.Push(GetLocationToken());
            _forwardHistory.Clear();
        }
        _archiveDirectory = normalized;
        if (_selectedTab is not null) _selectedTab.ArchiveDirectory = normalized;
        OnPropertyChanged(nameof(ArchiveDirectory));
        _ = LoadArchiveDirectoryAsync();
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoadNextPageCoreAsync(long generation, CancellationToken token)
    {
        if (IsLoading || !HasMore || string.IsNullOrWhiteSpace(CurrentPath)) return;
        IsLoading = true;
        try
        {
            var page = HasActiveFilter && _browser is IFilteredDirectoryBrowserService filtered
                ? await filtered.GetFilteredPageAsync(CurrentPath, _nextOffset, PageSize,
                    new DirectoryNameFilter(FilterText, FilterMode, FilterMatchCase), token)
                : await _browser.GetPageAsync(CurrentPath, _nextOffset, PageSize, token);
            if (generation != _loadGeneration || token.IsCancellationRequested) return;
            foreach (var item in page.Items)
                Entries.Add(new BrowserEntry(item.Name, item.FullPath, item.Type, item.Size, item.ModifiedAt,
                    Extension: item.Extension, Attributes: item.Attributes));
            NotifyPresentationState();
            _nextOffset += page.Items.Count;
            TotalItems = page.TotalCount;
            HasMore = page.HasMore;
            if (_selectionPathsToRestore.Count > 0)
            {
                var restored = Entries.Where(item => _selectionPathsToRestore.Contains(item.FullPath)).ToArray();
                _selectedEntries = restored;
                SelectedEntry = restored.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedEntries));
                SelectionRequested?.Invoke(this, restored);
                if (!HasMore) _selectionPathsToRestore.Clear();
            }
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

    private async Task LoadArchiveDirectoryAsync()
    {
        if (_archives is null || _archivePath is null) return;
        long generation;
        CancellationToken token;
        lock (_archiveLoadSync)
        {
            generation = Interlocked.Increment(ref _loadGeneration);
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = new CancellationTokenSource();
            token = _loadCancellation.Token;
            IsLoading = true;
            LastError = null;
            Entries.Clear();
            _nextOffset = 0;
            TotalItems = 0;
            HasMore = false;
            CurrentPath = $"{_archivePath}!/{_archiveDirectory}";
            if (_archiveDirectory.Length > 0)
            {
                var separator = _archiveDirectory.LastIndexOf('/');
                var parent = separator < 0 ? string.Empty : _archiveDirectory[..separator];
                Entries.Add(new BrowserEntry("..", $"{_archivePath}!/{parent}", FileEntryType.Directory,
                    null, null, IsParent: true, Endpoint: FileTransferEndpointKind.Archive,
                    ContainerPath: _archivePath, EntryPath: parent));
            }
        }
        try
        {
            var entries = await _archives.ListAsync(_archivePath, _archiveDirectory, token);
            if (HasActiveFilter)
            {
                if (!FileNamePatternMatcher.TryCreate(
                        new DirectoryNameFilter(FilterText, FilterMode, FilterMatchCase),
                        out var matcher, out var error))
                {
                    lock (_archiveLoadSync)
                    {
                        if (generation == _loadGeneration) FilterError = error;
                    }
                    return;
                }
                entries = entries.Where(entry => matcher(entry.Name)).ToArray();
            }
            lock (_archiveLoadSync)
            {
                if (generation != _loadGeneration || token.IsCancellationRequested) return;
                FilterError = null;
                _archiveEntries = entries;
                TotalItems = entries.Count;
                HasMore = entries.Count > PageSize;
                LoadNextArchivePageCore();
                NotifyPresentationState();
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            lock (_archiveLoadSync)
            {
                if (generation != _loadGeneration) return;
                LastError = ex.Message;
                _archiveEntries = [];
                HasMore = false;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally { if (generation == _loadGeneration) IsLoading = false; }
    }

    private Task LoadNextArchivePageAsync()
    {
        lock (_archiveLoadSync) LoadNextArchivePageCore();
        return Task.CompletedTask;
    }

    private void LoadNextArchivePageCore()
    {
        if (!IsArchive || _nextOffset >= _archiveEntries.Count) return;
        foreach (var entry in _archiveEntries.Skip(_nextOffset).Take(PageSize))
        {
            Entries.Add(new BrowserEntry(
                entry.Name,
                $"{_archivePath}!/{entry.Path}",
                entry.Type,
                entry.Size,
                entry.ModifiedAt,
                Extension: entry.Type == FileEntryType.File ? Path.GetExtension(entry.Name).TrimStart('.') : string.Empty,
                Attributes: entry.IsSymbolicLink ? "LINK" : "R---",
                Endpoint: FileTransferEndpointKind.Archive,
                ContainerPath: _archivePath,
                EntryPath: entry.Path,
                IsSymbolicLink: entry.IsSymbolicLink));
        }
        _nextOffset = Math.Min(_archiveEntries.Count, _nextOffset + PageSize);
        HasMore = _nextOffset < _archiveEntries.Count;
        OnPropertyChanged(nameof(SelectionSummary));
    }

    private void NotifyPresentationState()
    {
        OnPropertyChanged(nameof(ShowInitialLoading));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(ShowFilterEmptyState));
    }

    private void ReloadForFilter()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath)) return;
        if (!FileNamePatternMatcher.TryCreate(
                new DirectoryNameFilter(FilterText, FilterMode, FilterMatchCase), out _, out var error))
        {
            Interlocked.Increment(ref _loadGeneration);
            _loadCancellation?.Cancel();
            IsLoading = false;
            FilterError = error;
            return;
        }
        FilterError = null;
        if (IsArchive)
        {
            _ = LoadArchiveDirectoryAsync();
            return;
        }
        BrowseTo(CurrentPath, recordHistory: false, preserveSelection: true);
    }

    private string GetLocationToken() => IsArchive
        ? $"archive:{_archivePath}!/{_archiveDirectory}"
        : CurrentPath;

    private void NavigateToToken(string token)
    {
        if (!token.StartsWith("archive:", StringComparison.Ordinal))
        {
            BrowseTo(token, recordHistory: false);
            return;
        }
        var separator = token.IndexOf("!/", "archive:".Length, StringComparison.Ordinal);
        if (separator < 0) throw new InvalidDataException("Invalid archive history location.");
        var archivePath = token["archive:".Length..separator];
        var directory = token[(separator + 2)..];
        if (_archives is null || !_archives.CanOpen(archivePath) || SelectedTarget is null
            || !IsInside(archivePath, SelectedTarget.RootPath))
            throw new UnauthorizedAccessException("The archive history location is unavailable.");
        _archivePath = Path.GetFullPath(archivePath);
        _archiveDirectory = directory;
        if (_selectedTab is not null)
        {
            _selectedTab.ArchivePath = _archivePath;
            _selectedTab.ArchiveDirectory = directory;
        }
        OnPropertyChanged(nameof(IsArchive));
        OnPropertyChanged(nameof(ArchivePath));
        OnPropertyChanged(nameof(ArchiveDirectory));
        _ = LoadArchiveDirectoryAsync();
    }

    private void RequestSelection(IEnumerable<BrowserEntry> entries)
    {
        var next = entries.Distinct().ToArray();
        SetSelection(next);
        SelectionRequested?.Invoke(this, next);
    }

    private void SaveActiveTab()
    {
        if (_selectedTab is null || _switchingTab) return;
        if (!IsArchive) _selectedTab.Path = CurrentPath;
        _selectedTab.TargetRoot = SelectedTarget?.RootPath;
        _selectedTab.FilterText = FilterText;
        _selectedTab.FilterMode = FilterMode;
        _selectedTab.FilterMatchCase = FilterMatchCase;
        _selectedTab.BackHistory = _backHistory.ToArray();
        _selectedTab.ForwardHistory = _forwardHistory.ToArray();
        _selectedTab.ArchivePath = _archivePath;
        _selectedTab.ArchiveDirectory = _archiveDirectory;
    }

    private void ActivateTab(FilePaneTabViewModel tab, IReadOnlyList<ScanTarget>? targets = null)
    {
        if (targets is not null) _knownTargets = targets;
        _switchingTab = true;
        try
        {
            _selectedTarget = tab.TargetRoot is null
                ? null
                : _knownTargets.FirstOrDefault(target => PathComparer.Equals(
                    Normalize(target.RootPath), Normalize(tab.TargetRoot)));
            OnPropertyChanged(nameof(SelectedTarget));
            _filterText = tab.FilterText;
            _filterMode = tab.FilterMode;
            _filterMatchCase = tab.FilterMatchCase;
            _archivePath = tab.ArchivePath;
            _archiveDirectory = tab.ArchiveDirectory;
            OnPropertyChanged(nameof(FilterText));
            OnPropertyChanged(nameof(FilterMode));
            OnPropertyChanged(nameof(FilterMatchCase));
            OnPropertyChanged(nameof(HasActiveFilter));
            OnPropertyChanged(nameof(IsArchive));
            OnPropertyChanged(nameof(ArchivePath));
            OnPropertyChanged(nameof(ArchiveDirectory));
            _backHistory.Clear();
            for (var index = tab.BackHistory.Count - 1; index >= 0; index--) _backHistory.Push(tab.BackHistory[index]);
            _forwardHistory.Clear();
            for (var index = tab.ForwardHistory.Count - 1; index >= 0; index--) _forwardHistory.Push(tab.ForwardHistory[index]);
        }
        catch
        {
            _selectedTarget = null;
            OnPropertyChanged(nameof(SelectedTarget));
        }
        finally { _switchingTab = false; }

        Activate();
        if (!string.IsNullOrWhiteSpace(tab.ArchivePath))
        {
            var archivePath = tab.ArchivePath;
            if (_archives is null || _selectedTarget is null || !_archives.CanOpen(archivePath)
                || !File.Exists(archivePath) || !IsInside(archivePath, _selectedTarget.RootPath))
            {
                _archivePath = archivePath;
                _archiveDirectory = tab.ArchiveDirectory;
                ShowUnavailableTab($"{archivePath}!/{tab.ArchiveDirectory}");
                return;
            }
            _archivePath = Path.GetFullPath(archivePath);
            _archiveDirectory = tab.ArchiveDirectory;
            OnPropertyChanged(nameof(IsArchive));
            OnPropertyChanged(nameof(ArchivePath));
            OnPropertyChanged(nameof(ArchiveDirectory));
            _ = LoadArchiveDirectoryAsync();
            return;
        }
        var path = string.IsNullOrWhiteSpace(tab.Path) ? _selectedTarget?.RootPath : tab.Path;
        if (_selectedTarget is null || string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            ShowUnavailableTab(path ?? tab.Path);
            return;
        }
        try { BrowseTo(path, recordHistory: false); }
        catch { ShowUnavailableTab(path); }
    }

    private void ShowUnavailableTab(string path)
    {
        Interlocked.Increment(ref _loadGeneration);
        _loadCancellation?.Cancel();
        Entries.Clear();
        _currentPath = path;
        if (_selectedTab is not null) _selectedTab.Path = path;
        OnPropertyChanged(nameof(CurrentPath));
        _selectedEntries = [];
        SelectedEntry = null;
        TotalItems = 0;
        HasMore = false;
        IsLoading = false;
        LastError = path;
        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        StateChanged?.Invoke(this, EventArgs.Empty);
        WorkspaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private static FilePaneTabViewModel CreateTab(FilePaneTabSnapshot snapshot) => new(snapshot.Id, snapshot.Path)
    {
        TargetRoot = snapshot.TargetRoot,
        FilterText = snapshot.FilterText,
        FilterMode = snapshot.FilterMode,
        FilterMatchCase = snapshot.FilterMatchCase,
        BackHistory = snapshot.BackHistory,
        ForwardHistory = snapshot.ForwardHistory,
        ArchivePath = snapshot.ArchivePath,
        ArchiveDirectory = snapshot.ArchiveDirectory
    };

    private static FilePaneTabSnapshot CaptureTab(FilePaneTabViewModel tab) => new(
        tab.Id,
        tab.Path,
        tab.TargetRoot,
        tab.FilterText,
        tab.FilterMode,
        tab.FilterMatchCase,
        tab.BackHistory,
        tab.ForwardHistory,
        tab.ArchivePath,
        tab.ArchiveDirectory);

    private static bool SelectionsEqual(IReadOnlyList<BrowserEntry> left, IReadOnlyList<BrowserEntry> right)
    {
        if (left.Count != right.Count) return false;
        var paths = left.Select(item => item.FullPath).ToHashSet(PathComparer);
        return right.All(item => paths.Contains(item.FullPath));
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
    private readonly IDirectoryComparisonService? _directoryComparison;
    private readonly IDirectorySynchronizationPlanner? _synchronizationPlanner;
    private readonly ISftpConnectionService? _sftp;
    private readonly IArchiveService? _archives;
    private readonly IArchiveMutationService? _archiveMutations;
    private readonly IDiskManagementService _diskManagement;
    private readonly IFileOperationService _fileOperations;
    private readonly IFileTransferQueueService? _transferQueue;
    private readonly UserPreferencesService _preferences;
    private readonly IBackgroundAutomationService _automation;
    private readonly IOcrCapability _ocr;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _volumeDiscoveryGate = new(1, 1);
    private readonly SemaphoreSlim _searchPageGate = new(1, 1);
    private readonly SemaphoreSlim _queueIndexGate = new(1, 1);
    private readonly HashSet<Guid> _indexedTransferJobs = [];
    private readonly HashSet<Guid> _refreshedArchiveJobs = [];
    private readonly HashSet<string> _connectedVolumePaths = new(PathComparer);
    private readonly Dictionary<string, StorageVolume> _volumesByRoot = new(PathComparer);
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _suggestionCancellation;
    private CancellationTokenSource? _volumeMonitorCancellation;
    private CancellationTokenSource? _fileOperationCancellation;
    private CancellationTokenSource? _comparisonCancellation;
    private CancellationTokenSource? _preferenceSaveCancellation;
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
    private FileTransferQueueRow? _selectedTransfer;
    private FileConflictPolicy _selectedConflictPolicy = FileConflictPolicy.Fail;
    private bool _verifyTransfers = true;
    private DirectoryComparisonResult? _comparisonResult;
    private IReadOnlyList<DirectoryComparisonRow> _selectedComparisonRows = [];
    private DirectoryComparisonMode _selectedComparisonMode = DirectoryComparisonMode.SizeAndModifiedTime;
    private bool _hideIdenticalComparison = true;
    private bool _isComparing;
    private string _comparisonSummary = string.Empty;
    private SftpConnectionInfo? _sftpConnection;
    private IReadOnlyList<RemoteBrowserRow> _selectedRemoteRows = [];
    private string _sftpHost = string.Empty;
    private int _sftpPort = 22;
    private string _sftpUsername = string.Empty;
    private string _sftpPassword = string.Empty;
    private string _sftpHostKeySha256 = string.Empty;
    private string _remotePath = "/";
    private string _remoteStatus = string.Empty;
    private bool _isRemoteBusy;
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
        UpdateViewModel? updater = null,
        AgentApprovalViewModel? approvals = null,
        IFileTransferQueueService? transferQueue = null,
        IDirectoryComparisonService? directoryComparison = null,
        IDirectorySynchronizationPlanner? synchronizationPlanner = null,
        ISftpConnectionService? sftp = null,
        IArchiveService? archives = null,
        IArchiveMutationService? archiveMutations = null,
        IMultiRenameService? multiRename = null,
        IQuickViewService? quickView = null,
        ITextEditorService? textEditor = null)
    {
        _store = store;
        _scanner = scanner;
        _search = search;
        _systemSearchHistory = systemSearchHistory;
        _desktop = desktop;
        _localization = localization;
        _volumeDiscovery = volumeDiscovery;
        _directoryBrowser = directoryBrowser;
        _directoryComparison = directoryComparison;
        _synchronizationPlanner = synchronizationPlanner;
        _sftp = sftp;
        _archives = archives;
        _archiveMutations = archiveMutations ?? archives as IArchiveMutationService;
        _diskManagement = diskManagement;
        _fileOperations = fileOperations;
        _transferQueue = transferQueue;
        _preferences = preferences;
        _automation = automation;
        _ocr = ocr;
        Updater = updater;
        Approvals = approvals;
        _automation.StatusChanged += OnBackgroundStatusChanged;
        if (_transferQueue is not null) _transferQueue.Changed += OnTransferQueueChanged;
        _readOnlyMode = preferences.ReadOnlyMode;
        _fileOperations.AccessMode = _readOnlyMode ? FileAccessMode.ReadOnly : FileAccessMode.ManageFiles;
        if (_archives is not null) _archives.AccessMode = _fileOperations.AccessMode;
        if (_archiveMutations is not null) _archiveMutations.AccessMode = _fileOperations.AccessMode;
        _automaticDrives = preferences.AutomaticDrives && !string.Equals(
            Environment.GetEnvironmentVariable("ODYSSEY_DISABLE_AUTOMATIC_DRIVES"), "1", StringComparison.Ordinal);
        foreach (var path in preferences.Hotlist.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(PathComparer))
            Hotlist.Add(path);
        LeftPane = new FilePaneViewModel(directoryBrowser, archives);
        RightPane = new FilePaneViewModel(directoryBrowser, archives);
        _activePane = LeftPane;
        LeftPane.IsActive = true;
        LeftPane.Activated += (_, _) => SetActivePane(LeftPane);
        RightPane.Activated += (_, _) => SetActivePane(RightPane);
        LeftPane.StateChanged += (_, _) => OnPaneStateChanged(LeftPane);
        RightPane.StateChanged += (_, _) => OnPaneStateChanged(RightPane);
        LeftPane.WorkspaceChanged += (_, _) => OnPaneWorkspaceChanged();
        RightPane.WorkspaceChanged += (_, _) => OnPaneWorkspaceChanged();
        if (multiRename is not null)
        {
            multiRename.AccessMode = _fileOperations.AccessMode;
            MultiRename = new MultiRenameViewModel(multiRename, desktop, localization);
            MultiRename.Completed += (_, _) =>
            {
                TryRefreshPane(LeftPane);
                TryRefreshPane(RightPane);
            };
        }
        if (quickView is not null) QuickView = new QuickViewViewModel(quickView, localization);
        if (textEditor is not null)
        {
            TextEditor = new TextEditorViewModel(textEditor, desktop, localization)
            {
                AccessMode = _fileOperations.AccessMode
            };
            TextEditor.Saved += (_, _) =>
            {
                TryRefreshPane(LeftPane);
                TryRefreshPane(RightPane);
            };
        }
        _statusMessage = localization["Starting"];
        _scanState = localization["Ready"];
        Targets.CollectionChanged += (_, _) =>
        {
            RaiseRescueState();
            var knownTargets = Targets.ToArray();
            LeftPane.SetKnownTargets(knownTargets);
            RightPane.SetKnownTargets(knownTargets);
        };
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
        OpenQuickViewCommand = new AsyncRelayCommand(OpenQuickViewAsync, CanOpenQuickView);
        EditEntryCommand = new AsyncRelayCommand(EditEntryAsync, CanEditSelectedEntry);
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
        NewTabCommand = new RelayCommand<string?>(side => PaneForSide(side).NewTab());
        DuplicateTabCommand = new RelayCommand<string?>(side => PaneForSide(side).DuplicateTab());
        CloseTabCommand = new RelayCommand<string?>(side => PaneForSide(side).CloseTab(),
            side => PaneForSide(side).CanCloseTab);
        ReopenClosedTabCommand = new RelayCommand<string?>(side => PaneForSide(side).ReopenClosedTab(),
            side => PaneForSide(side).CanReopenClosedTab);
        AddHotlistCommand = new AsyncRelayCommand(AddHotlistAsync, () => IsFilesPage);
        RemoveHotlistCommand = new RelayCommand(RemoveHotlist, CanRemoveHotlist);
        OpenHotlistCommand = new RelayCommand(OpenHotlist, CanOpenHotlist);
        SelectAllCommand = new RelayCommand(() => ActivePane.SelectAll(), () => IsFilesPage);
        InvertSelectionCommand = new RelayCommand(() => ActivePane.InvertSelection(), () => IsFilesPage);
        SelectByExtensionCommand = new RelayCommand(() => ActivePane.SelectByExtension(),
            () => IsFilesPage && ActivePane.SelectedEntry is { IsParent: false, Extension.Length: > 0 });
        SelectByMaskCommand = new AsyncRelayCommand(SelectByMaskAsync, () => IsFilesPage);
        RestorePreviousSelectionCommand = new RelayCommand(() => ActivePane.RestorePreviousSelection(),
            () => IsFilesPage && ActivePane.CanRestorePreviousSelection);
        ToggleAccessModeCommand = new AsyncRelayCommand(ToggleAccessModeAsync, () => !IsFileOperationRunning && !HasActiveTransfers);
        CreateFolderCommand = new AsyncRelayCommand(CreateFolderAsync, CanCreateFolder);
        CreateArchiveCommand = new AsyncRelayCommand(CreateArchiveAsync, CanCreateArchive);
        RenameEntryCommand = new AsyncRelayCommand(RenameEntryAsync, CanRenameSelectedEntry);
        OpenMultiRenameCommand = new RelayCommand(OpenMultiRename, CanOpenMultiRename);
        CopyEntryCommand = new AsyncRelayCommand(CopyEntryAsync, CanCopySelectedEntry);
        MoveEntryCommand = new AsyncRelayCommand(MoveEntryAsync, CanManageSelectedEntry);
        TrashEntryCommand = new AsyncRelayCommand(TrashEntryAsync, CanTrashSelectedEntry);
        UndoFileOperationCommand = new AsyncRelayCommand(UndoFileOperationAsync, CanUndoFileOperation);
        CancelFileOperationCommand = new RelayCommand(CancelFileOperation, () => IsFileOperationRunning);
        PauseTransferCommand = new AsyncRelayCommand(PauseSelectedTransferAsync, CanPauseTransfer);
        ResumeTransferCommand = new AsyncRelayCommand(ResumeSelectedTransferAsync, CanResumeTransfer);
        RetryTransferCommand = new AsyncRelayCommand(RetrySelectedTransferAsync, CanRetryTransfer);
        CancelTransferCommand = new AsyncRelayCommand(CancelSelectedTransferAsync, CanCancelTransfer);
        CompareDirectoriesCommand = new AsyncRelayCommand(CompareDirectoriesAsync, CanCompareDirectories);
        CancelComparisonCommand = new RelayCommand(CancelComparison, () => IsComparing);
        SyncLeftToRightCommand = new AsyncRelayCommand(() => QueueComparisonAsync(leftToRight: true), () => CanQueueComparison(leftToRight: true));
        SyncRightToLeftCommand = new AsyncRelayCommand(() => QueueComparisonAsync(leftToRight: false), () => CanQueueComparison(leftToRight: false));
        ConnectSftpCommand = new AsyncRelayCommand(ConnectSftpAsync, CanConnectSftp);
        DisconnectSftpCommand = new AsyncRelayCommand(DisconnectSftpAsync, () => IsSftpConnected && !IsRemoteBusy);
        RefreshRemoteCommand = new AsyncRelayCommand(RefreshRemoteAsync, () => IsSftpConnected && !IsRemoteBusy);
        RemoteUpCommand = new AsyncRelayCommand(RemoteUpAsync, () => IsSftpConnected && RemotePath != "/" && !IsRemoteBusy);
        UploadRemoteCommand = new AsyncRelayCommand(UploadRemoteAsync, CanUploadRemote);
        DownloadRemoteCommand = new AsyncRelayCommand(DownloadRemoteAsync, CanDownloadRemote);
        UnmountDriveCommand = new AsyncRelayCommand(() => DisconnectDriveAsync(eject: false), CanDisconnectDrive);
        EjectDriveCommand = new AsyncRelayCommand(() => DisconnectDriveAsync(eject: true), CanDisconnectDrive);
    }

    public ObservableCollection<ScanTarget> Targets { get; } = [];
    public ObservableCollection<SearchResult> SearchResults { get; } = [];
    public ObservableCollection<SearchSuggestion> SearchSuggestions { get; } = [];
    public ObservableCollection<CategoryFilter> CategoryFilters { get; } = [];
    public ObservableCollection<FileOperationHistoryRow> FileOperationHistory { get; } = [];
    public ObservableCollection<FileTransferQueueRow> TransferQueue { get; } = [];
    public ObservableCollection<DirectoryComparisonRow> ComparisonRows { get; } = [];
    public ObservableCollection<RemoteBrowserRow> RemoteEntries { get; } = [];
    public ObservableCollection<string> Hotlist { get; } = [];
    public IReadOnlyList<FileConflictPolicy> ConflictPolicies { get; } = Enum.GetValues<FileConflictPolicy>();
    public IReadOnlyList<DirectoryComparisonMode> ComparisonModes { get; } = Enum.GetValues<DirectoryComparisonMode>();
    public FilePaneViewModel LeftPane { get; }
    public FilePaneViewModel RightPane { get; }
    public FilePaneViewModel ActivePane => _activePane;
    public FilePaneViewModel PassivePane => ReferenceEquals(_activePane, LeftPane) ? RightPane : LeftPane;
    public LocalizationService Localization => _localization;
    public UpdateViewModel? Updater { get; }
    public AgentApprovalViewModel? Approvals { get; }
    public MultiRenameViewModel? MultiRename { get; }
    public QuickViewViewModel? QuickView { get; }
    public TextEditorViewModel? TextEditor { get; }

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
    public IAsyncRelayCommand OpenQuickViewCommand { get; }
    public IAsyncRelayCommand EditEntryCommand { get; }
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
    public IRelayCommand<string?> NewTabCommand { get; }
    public IRelayCommand<string?> DuplicateTabCommand { get; }
    public IRelayCommand<string?> CloseTabCommand { get; }
    public IRelayCommand<string?> ReopenClosedTabCommand { get; }
    public IAsyncRelayCommand AddHotlistCommand { get; }
    public IRelayCommand RemoveHotlistCommand { get; }
    public IRelayCommand OpenHotlistCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand InvertSelectionCommand { get; }
    public IRelayCommand SelectByExtensionCommand { get; }
    public IAsyncRelayCommand SelectByMaskCommand { get; }
    public IRelayCommand RestorePreviousSelectionCommand { get; }
    public IAsyncRelayCommand ToggleAccessModeCommand { get; }
    public IAsyncRelayCommand CreateFolderCommand { get; }
    public IAsyncRelayCommand CreateArchiveCommand { get; }
    public IAsyncRelayCommand RenameEntryCommand { get; }
    public IRelayCommand OpenMultiRenameCommand { get; }
    public IAsyncRelayCommand CopyEntryCommand { get; }
    public IAsyncRelayCommand MoveEntryCommand { get; }
    public IAsyncRelayCommand TrashEntryCommand { get; }
    public IAsyncRelayCommand UndoFileOperationCommand { get; }
    public IRelayCommand CancelFileOperationCommand { get; }
    public IAsyncRelayCommand PauseTransferCommand { get; }
    public IAsyncRelayCommand ResumeTransferCommand { get; }
    public IAsyncRelayCommand RetryTransferCommand { get; }
    public IAsyncRelayCommand CancelTransferCommand { get; }
    public IAsyncRelayCommand CompareDirectoriesCommand { get; }
    public IRelayCommand CancelComparisonCommand { get; }
    public IAsyncRelayCommand SyncLeftToRightCommand { get; }
    public IAsyncRelayCommand SyncRightToLeftCommand { get; }
    public IAsyncRelayCommand ConnectSftpCommand { get; }
    public IAsyncRelayCommand DisconnectSftpCommand { get; }
    public IAsyncRelayCommand RefreshRemoteCommand { get; }
    public IAsyncRelayCommand RemoteUpCommand { get; }
    public IAsyncRelayCommand UploadRemoteCommand { get; }
    public IAsyncRelayCommand DownloadRemoteCommand { get; }
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
            OnPropertyChanged(nameof(SelectedResultConfidenceDisplay));
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
            if (_archives is not null) _archives.AccessMode = _fileOperations.AccessMode;
            if (_archiveMutations is not null) _archiveMutations.AccessMode = _fileOperations.AccessMode;
            if (MultiRename is not null) MultiRename.AccessMode = _fileOperations.AccessMode;
            if (TextEditor is not null) TextEditor.AccessMode = _fileOperations.AccessMode;
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
    public FileTransferQueueRow? SelectedTransfer
    {
        get => _selectedTransfer;
        set
        {
            if (!SetProperty(ref _selectedTransfer, value)) return;
            NotifyTransferCommandStates();
        }
    }
    public FileConflictPolicy SelectedConflictPolicy
    {
        get => _selectedConflictPolicy;
        set => SetProperty(ref _selectedConflictPolicy, value);
    }
    public bool VerifyTransfers { get => _verifyTransfers; set => SetProperty(ref _verifyTransfers, value); }
    public DirectoryComparisonMode SelectedComparisonMode
    {
        get => _selectedComparisonMode;
        set => SetProperty(ref _selectedComparisonMode, value);
    }
    public bool HideIdenticalComparison
    {
        get => _hideIdenticalComparison;
        set
        {
            if (!SetProperty(ref _hideIdenticalComparison, value)) return;
            RebuildComparisonRows();
        }
    }
    public bool IsComparing
    {
        get => _isComparing;
        private set
        {
            if (!SetProperty(ref _isComparing, value)) return;
            CompareDirectoriesCommand.NotifyCanExecuteChanged();
            CancelComparisonCommand.NotifyCanExecuteChanged();
            NotifyComparisonCommandStates();
        }
    }
    public string ComparisonSummary { get => _comparisonSummary; private set => SetProperty(ref _comparisonSummary, value); }
    public string ComparisonLeftPath => _comparisonResult?.LeftPath ?? LeftPane.CurrentPath;
    public string ComparisonRightPath => _comparisonResult?.RightPath ?? RightPane.CurrentPath;
    public string SftpHost { get => _sftpHost; set { if (SetProperty(ref _sftpHost, value)) NotifyRemoteCommandStates(); } }
    public int SftpPort { get => _sftpPort; set { if (SetProperty(ref _sftpPort, value)) NotifyRemoteCommandStates(); } }
    public string SftpUsername { get => _sftpUsername; set { if (SetProperty(ref _sftpUsername, value)) NotifyRemoteCommandStates(); } }
    public string SftpPassword { get => _sftpPassword; set { if (SetProperty(ref _sftpPassword, value)) NotifyRemoteCommandStates(); } }
    public string SftpHostKeySha256 { get => _sftpHostKeySha256; set => SetProperty(ref _sftpHostKeySha256, value); }
    public string RemotePath { get => _remotePath; private set { if (SetProperty(ref _remotePath, value)) NotifyRemoteCommandStates(); } }
    public string RemoteStatus { get => _remoteStatus; private set => SetProperty(ref _remoteStatus, value); }
    public bool IsRemoteBusy
    {
        get => _isRemoteBusy;
        private set { if (SetProperty(ref _isRemoteBusy, value)) NotifyRemoteCommandStates(); }
    }
    public bool IsSftpConnected => _sftpConnection is not null;
    public bool HasActiveTransfers => TransferQueue.Any(item => item.RawState is
        FileTransferState.Queued or FileTransferState.Running or FileTransferState.Paused);
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
    public string SelectedResultConfidenceDisplay => SelectedResult is null
        ? string.Empty
        : _localization.TranslateEnum(SelectedResult.Confidence);
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
    public string RescueQuoteText => _localization[$"RescueQuote{_rescueTipIndex + 1}"];
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
    public bool IsSyncPage => _activePage == "Sync";
    public bool IsRemotePage => _activePage == "Remote";
    public bool IsSearchPage => _activePage == "Search";
    public bool IsTargetsPage => _activePage == "Targets";
    public bool IsSettingsPage => _activePage == "Settings";

    public void AdvanceRescueTip()
    {
        _rescueTipIndex = (_rescueTipIndex + 1) % RescueTipCount;
        OnPropertyChanged(nameof(RescueTipText));
        OnPropertyChanged(nameof(RescueQuoteText));
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
            if (_transferQueue is not null)
            {
                await _transferQueue.InitializeAsync();
                RebuildTransferQueue();
            }
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
            await SynchronizeTransferQueueAsync();
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
        _comparisonCancellation?.Cancel();
        _preferenceSaveCancellation?.Cancel();
        QuickView?.Close();
        SaveCommanderWorkspace();
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

    private void OnPaneWorkspaceChanged()
    {
        CloseTabCommand.NotifyCanExecuteChanged();
        ReopenClosedTabCommand.NotifyCanExecuteChanged();
        OpenHotlistCommand.NotifyCanExecuteChanged();
        RemoveHotlistCommand.NotifyCanExecuteChanged();
        _preferenceSaveCancellation?.Cancel();
        _preferenceSaveCancellation?.Dispose();
        _preferenceSaveCancellation = new CancellationTokenSource();
        _ = SaveCommanderWorkspaceAfterDelayAsync(_preferenceSaveCancellation.Token);
    }

    private async Task SaveCommanderWorkspaceAfterDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            SaveCommanderWorkspace();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void SaveCommanderWorkspace() => _preferences.SaveCommanderWorkspace(
        LeftPane.CaptureWorkspace(), RightPane.CaptureWorkspace(), Hotlist);

    private FilePaneViewModel PaneForSide(string? side) => side == "Right" ? RightPane : LeftPane;

    private async Task AddHotlistAsync()
    {
        var input = await _desktop.PromptTextAsync(
            _localization["HotlistAddTitle"], _localization["HotlistAddPrompt"], ActivePane.CurrentPath);
        if (input is null) return;
        try
        {
            var path = NormalizePath(input);
            if (!Directory.Exists(path))
            {
                StatusMessage = _localization["HotlistUnavailable"];
                return;
            }
            if (!Targets.Any(target => IsPathInsideTarget(path, target.RootPath)))
            {
                StatusMessage = _localization["HotlistOutsideTargets"];
                return;
            }
            if (!Hotlist.Contains(path, PathComparer)) Hotlist.Add(path);
            ActivePane.SelectedHotlistPath = path;
            SaveCommanderWorkspace();
            StatusMessage = _localization["HotlistAdded"];
            NotifyCommandStates();
        }
        catch (Exception ex) { StatusMessage = _localization.Format("BrowseFailed", ex.Message); }
    }

    private void RemoveHotlist()
    {
        var path = ActivePane.SelectedHotlistPath;
        if (path is null) return;
        var existing = Hotlist.FirstOrDefault(item => PathComparer.Equals(item, path));
        if (existing is null) return;
        Hotlist.Remove(existing);
        LeftPane.SelectedHotlistPath = null;
        RightPane.SelectedHotlistPath = null;
        SaveCommanderWorkspace();
        StatusMessage = _localization["HotlistRemoved"];
        NotifyCommandStates();
    }

    private void OpenHotlist()
    {
        var path = ActivePane.SelectedHotlistPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusMessage = _localization["HotlistUnavailable"];
            return;
        }
        var target = Targets.Where(item => IsPathInsideTarget(path, item.RootPath))
            .OrderByDescending(item => item.RootPath.Length).FirstOrDefault();
        if (target is null)
        {
            StatusMessage = _localization["HotlistOutsideTargets"];
            return;
        }
        try
        {
            if (ActivePane.SelectedTarget?.Id != target.Id) ActivePane.SelectedTarget = target;
            ActivePane.BrowseTo(path);
            StatusMessage = _localization["HotlistOpened"];
        }
        catch (Exception ex) { StatusMessage = _localization.Format("BrowseFailed", ex.Message); }
    }

    private bool CanOpenHotlist() => IsFilesPage && !string.IsNullOrWhiteSpace(ActivePane.SelectedHotlistPath);
    private bool CanRemoveHotlist() => IsFilesPage && ActivePane.SelectedHotlistPath is not null
        && Hotlist.Contains(ActivePane.SelectedHotlistPath, PathComparer);

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
        _activePage = page is "Files" or "Sync" or "Remote" or "Search" or "Targets" or "Settings" or "Rescue" ? page : "Rescue";
        OnPropertyChanged(nameof(IsRescuePage));
        OnPropertyChanged(nameof(IsExpertPage));
        OnPropertyChanged(nameof(IsFilesPage));
        OnPropertyChanged(nameof(IsSyncPage));
        OnPropertyChanged(nameof(IsRemotePage));
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
            var knownTargets = Targets.ToArray();
            LeftPane.SetKnownTargets(knownTargets);
            RightPane.SetKnownTargets(knownTargets);
            if (_preferences.LeftPaneWorkspace is { Tabs.Count: > 0 } leftWorkspace)
                LeftPane.RestoreWorkspace(leftWorkspace, knownTargets);
            else
                LeftPane.SelectedTarget = Targets.FirstOrDefault();
            if (_preferences.RightPaneWorkspace is { Tabs.Count: > 0 } rightWorkspace)
                RightPane.RestoreWorkspace(rightWorkspace, knownTargets);
            else
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
        if (IsFilesPage && ActivePane.SelectedEntry is { } browserEntry)
        {
            if (browserEntry.Endpoint == FileTransferEndpointKind.Archive)
            {
                if (browserEntry.Type == FileEntryType.Directory) ActivePane.OpenSelectedDirectory();
                else return OpenQuickViewAsync();
                return Task.CompletedTask;
            }
            if (browserEntry.Type == FileEntryType.File && ActivePane.CanOpenArchive(browserEntry.FullPath))
            {
                try
                {
                    ActivePane.OpenArchive(browserEntry.FullPath);
                    StatusMessage = _localization["ArchiveOpened"];
                }
                catch (Exception ex) { StatusMessage = _localization.Format("ArchiveOpenFailed", ex.Message); }
                return Task.CompletedTask;
            }
        }
        var entry = GetSelectedEntry() ?? throw new InvalidOperationException(_localization["NoItemSelected"]);
        if (IsFilesPage && entry.Type == FileEntryType.Directory)
        {
            try { ActivePane.OpenSelectedDirectory(); }
            catch (Exception ex) { StatusMessage = _localization.Format("BrowseFailed", ex.Message); }
            return Task.CompletedTask;
        }
        return RunDesktopAction(() => _desktop.OpenAsync(entry.Path), _localization["Opened"]);
    }

    private async Task OpenQuickViewAsync()
    {
        if (IsRemotePage)
        {
            var remote = GetRemoteQuickViewEntry();
            if (QuickView is null || _sftpConnection is null || remote is null) return;
            await QuickView.OpenSftpAsync(remote.Entry.FullPath, _sftpConnection.ConnectionKey);
            return;
        }
        if (QuickView is null || ActivePane.SelectedEntry is not { } entry) return;
        if (entry.Endpoint == FileTransferEndpointKind.Archive)
        {
            if (entry.ContainerPath is null || entry.EntryPath is null) return;
            await QuickView.OpenArchiveAsync(entry.FullPath, entry.ContainerPath, entry.EntryPath);
            return;
        }
        await QuickView.OpenAsync(entry.FullPath);
    }

    private async Task EditEntryAsync()
    {
        if (IsFilesPage && ActivePane.SelectedEntry?.Endpoint == FileTransferEndpointKind.Archive)
            return;
        var entry = GetSelectedEntry() ?? throw new InvalidOperationException(_localization["NoItemSelected"]);
        if (entry.Type != FileEntryType.File) return;
        if (TextEditor is null)
        {
            await RunDesktopAction(() => _desktop.EditAsync(entry.Path), _localization["EditorOpened"]);
            return;
        }
        try
        {
            await TextEditor.OpenAsync(entry.Path);
            StatusMessage = _localization["TextEditorOpened"];
        }
        catch (TextEditorUnsupportedException)
        {
            await RunDesktopAction(
                () => _desktop.EditAsync(entry.Path),
                _localization["TextEditorExternalFallback"]);
        }
        catch (Exception exception) { StatusMessage = exception.Message; }
    }

    private Task OpenFolderAsync()
    {
        if (IsFilesPage && ActivePane.IsArchive && ActivePane.ArchivePath is { } archivePath)
            return RunDesktopAction(() => _desktop.OpenContainingFolderAsync(archivePath), _localization["FolderOpened"]);
        var entry = GetSelectedEntry() ?? throw new InvalidOperationException(_localization["NoItemSelected"]);
        return RunDesktopAction(() => _desktop.OpenContainingFolderAsync(entry.Path), _localization["FolderOpened"]);
    }

    private void NavigateUp()
    {
        NavigatePaneUp(ActivePane);
    }

    private async Task SelectByMaskAsync()
    {
        var mask = await _desktop.PromptTextAsync(
            _localization["SelectionMaskTitle"],
            _localization.Format("SelectionMaskPrompt", _localization.TranslateEnum(ActivePane.FilterMode)));
        if (mask is null) return;
        StatusMessage = ActivePane.SelectByMask(mask, ActivePane.FilterMode, ActivePane.FilterMatchCase)
            ? _localization["SelectionApplied"]
            : _localization["InvalidFilter"];
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

    private async Task CreateArchiveAsync()
    {
        if (_archiveMutations is null || ActivePane.IsArchive || !Directory.Exists(ActivePane.CurrentPath)) return;
        var selected = ActivePane.SelectedEntries.Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0 || selected.Any(item => item.Endpoint != FileTransferEndpointKind.Local
                                                         || item.IsSymbolicLink)) return;
        var name = await _desktop.PromptTextAsync(
            _localization["CreateArchiveTitle"],
            _localization.Format("CreateArchivePrompt", selected.Length),
            "archive.zip");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        var extension = Path.GetExtension(name);
        if (extension.Length == 0) name += ".zip";
        else if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                 && !extension.Equals(".tar", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = _localization["ArchiveNameInvalid"];
            return;
        }
        if (!string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal)
            || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusMessage = _localization["ArchiveNameInvalid"];
            return;
        }
        var archivePath = Path.Combine(ActivePane.CurrentPath, name);
        var confirmed = await _desktop.ConfirmAsync(
            _localization["CreateArchiveTitle"],
            _localization.Format("CreateArchiveConfirmation", selected.Length, archivePath),
            _localization["CreateArchive"]);
        if (!confirmed) return;
        await RunArchiveMutationAsync(
            token => _archiveMutations.CreateAsync(archivePath,
                selected.Select(item => item.FullPath).ToArray(), CreateFileProgress(), token),
            _localization["ArchiveCreated"]);
        TryRefreshPane(ActivePane);
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

    private void OpenMultiRename()
    {
        if (MultiRename is null) return;
        var sources = ActivePane.SelectedEntries
            .Where(item => !item.IsParent && item.Endpoint == FileTransferEndpointKind.Local && !item.IsSymbolicLink)
            .Select(item => item.FullPath).ToArray();
        if (sources.Length > 0) MultiRename.Open(sources);
    }

    private async Task CopyEntryAsync()
    {
        if (IsFilesPage && ActivePane.SelectedEntries.Any(item => item.Endpoint == FileTransferEndpointKind.Archive))
        {
            await CopyArchiveEntriesAsync();
            return;
        }
        if (IsFilesPage && PassivePane.IsArchive)
        {
            await CopyLocalEntriesIntoArchiveAsync();
            return;
        }
        var selected = GetSelectedEntries().Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0) return;
        var destination = Directory.Exists(PassivePane.CurrentPath)
            ? PassivePane.CurrentPath
            : await _desktop.PickDestinationFolderAsync();
        if (destination is null) return;
        if (_transferQueue is not null)
        {
            await QueueTransfersAsync(FileOperationKind.Copy, selected.Select(item => item.Path), destination);
            return;
        }
        if (!ValidateBatchDestination(selected.Select(item => item.Path), destination)) return;
        await RunFileOperationAsync(
            token => RunBatchAsync(selected.Select(item => item.Path),
                (source, cancellation) => _fileOperations.CopyAsync(source, destination, CreateFileProgress(), cancellation), token),
            _localization.Format("CopyBatchCompleted", selected.Length));
    }

    private async Task CopyArchiveEntriesAsync()
    {
        if (_transferQueue is null) return;
        var selected = ActivePane.SelectedEntries.Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0 || selected.Any(item => item.Endpoint != FileTransferEndpointKind.Archive
                                                         || item.IsSymbolicLink
                                                         || item.ContainerPath is null
                                                         || item.EntryPath is null)) return;
        var archivePath = selected[0].ContainerPath!;
        if (selected.Any(item => !PathComparer.Equals(item.ContainerPath, archivePath))) return;
        if (PassivePane.IsArchive && PassivePane.ArchivePath is { } destinationArchive)
        {
            if (_archives?.GetCapabilities(destinationArchive).HasFlag(ArchiveCapabilities.Update) != true)
            {
                StatusMessage = _localization["ArchiveWriteUnsupported"];
                return;
            }
            var archiveRequests = selected.Select(item => new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = item.EntryPath!,
                DestinationDirectory = PassivePane.ArchiveDirectory,
                SourceEndpoint = FileTransferEndpointKind.Archive,
                DestinationEndpoint = FileTransferEndpointKind.Archive,
                SourceConnectionKey = archivePath,
                DestinationConnectionKey = destinationArchive,
                ConflictPolicy = SelectedConflictPolicy,
                VerifyAfterCopy = false
            }).ToArray();
            await _transferQueue.EnqueueAsync(archiveRequests);
            StatusMessage = _localization.Format("ArchiveCopiesQueued", archiveRequests.Length);
            RebuildTransferQueue();
            return;
        }
        var destination = !PassivePane.IsArchive && Directory.Exists(PassivePane.CurrentPath)
            ? PassivePane.CurrentPath
            : await _desktop.PickDestinationFolderAsync();
        if (destination is null) return;
        var requests = selected.Select(item => new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = item.EntryPath!,
            DestinationDirectory = destination,
            SourceEndpoint = FileTransferEndpointKind.Archive,
            DestinationEndpoint = FileTransferEndpointKind.Local,
            SourceConnectionKey = archivePath,
            ConflictPolicy = SelectedConflictPolicy,
            VerifyAfterCopy = false
        }).ToArray();
        await _transferQueue.EnqueueAsync(requests);
        StatusMessage = _localization.Format("ArchiveExtractionsQueued", requests.Length);
        RebuildTransferQueue();
    }

    private async Task CopyLocalEntriesIntoArchiveAsync()
    {
        if (_transferQueue is null || _archives is null || PassivePane.ArchivePath is not { } archivePath)
            return;
        if (!_archives.GetCapabilities(archivePath).HasFlag(ArchiveCapabilities.Update))
        {
            StatusMessage = _localization["ArchiveWriteUnsupported"];
            return;
        }
        var selected = ActivePane.SelectedEntries.Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0 || selected.Any(item => item.Endpoint != FileTransferEndpointKind.Local
                                                         || item.IsSymbolicLink)) return;
        var requests = selected.Select(item => new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = item.FullPath,
            DestinationDirectory = PassivePane.ArchiveDirectory,
            SourceEndpoint = FileTransferEndpointKind.Local,
            DestinationEndpoint = FileTransferEndpointKind.Archive,
            DestinationConnectionKey = archivePath,
            ConflictPolicy = SelectedConflictPolicy,
            VerifyAfterCopy = false
        }).ToArray();
        await _transferQueue.EnqueueAsync(requests);
        StatusMessage = _localization.Format("ArchiveAdditionsQueued", requests.Length);
        RebuildTransferQueue();
    }

    private async Task MoveEntryAsync()
    {
        var selected = GetSelectedEntries().Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0) return;
        var destination = Directory.Exists(PassivePane.CurrentPath)
            ? PassivePane.CurrentPath
            : await _desktop.PickDestinationFolderAsync();
        if (destination is null) return;
        if (_transferQueue is null && !ValidateBatchDestination(selected.Select(item => item.Path), destination)) return;
        var confirmed = await _desktop.ConfirmAsync(
            _localization["MoveTitle"],
            _localization.Format("MoveBatchConfirmation", selected.Length, destination),
            _localization["Move"]);
        if (!confirmed) return;
        if (_transferQueue is not null)
        {
            await QueueTransfersAsync(FileOperationKind.Move, selected.Select(item => item.Path), destination);
            return;
        }
        await RunFileOperationAsync(
            token => RunBatchAsync(selected.Select(item => item.Path),
                (source, cancellation) => _fileOperations.MoveAsync(source, destination, CreateFileProgress(), cancellation), token),
            _localization.Format("MoveBatchCompleted", selected.Length));
    }

    private async Task QueueTransfersAsync(
        FileOperationKind kind, IEnumerable<string> sources, string destination)
    {
        if (_transferQueue is null) return;
        var requests = sources.Select(source => new FileTransferRequest
        {
            Kind = kind,
            SourcePath = source,
            DestinationDirectory = destination,
            ConflictPolicy = SelectedConflictPolicy,
            VerifyAfterCopy = VerifyTransfers
        }).ToArray();
        await _transferQueue.EnqueueAsync(requests);
        StatusMessage = _localization.Format("TransfersQueued", requests.Length);
        RebuildTransferQueue();
    }

    public void SetComparisonSelection(IEnumerable<DirectoryComparisonRow> rows)
    {
        _selectedComparisonRows = rows.Distinct().ToArray();
        NotifyComparisonCommandStates();
    }

    public void SetRemoteSelection(IEnumerable<RemoteBrowserRow> rows)
    {
        _selectedRemoteRows = rows.Distinct().ToArray();
        NotifyRemoteCommandStates();
    }

    public async Task OpenRemoteEntryAsync(RemoteBrowserRow row)
    {
        if (row.Entry.Type == FileEntryType.Directory)
        {
            RemotePath = NormalizeRemoteUiPath(row.Entry.FullPath);
            await RefreshRemoteAsync();
            return;
        }
        if (row.Entry.IsSymbolicLink || QuickView is null || _sftpConnection is null) return;
        await QuickView.OpenSftpAsync(row.Entry.FullPath, _sftpConnection.ConnectionKey);
    }

    private async Task ConnectSftpAsync()
    {
        if (_sftp is null) return;
        IsRemoteBusy = true;
        RemoteStatus = _localization["SftpConnecting"];
        try
        {
            _sftpConnection = await _sftp.ConnectAsync(new SftpConnectionRequest
            {
                Host = SftpHost,
                Port = SftpPort,
                Username = SftpUsername,
                Password = SftpPassword,
                ExpectedHostKeySha256 = SftpHostKeySha256
            });
            SftpHostKeySha256 = _sftpConnection.HostKeySha256;
            SftpPassword = string.Empty;
            RemotePath = "/";
            OnPropertyChanged(nameof(IsSftpConnected));
            RemoteStatus = _localization.Format("SftpConnected", _sftpConnection.Host, _sftpConnection.HostKeySha256);
        }
        catch (Exception ex) { RemoteStatus = _localization.Format("SftpConnectionFailed", ex.Message); }
        finally { IsRemoteBusy = false; NotifyRemoteCommandStates(); }
        if (IsSftpConnected) await RefreshRemoteAsync();
    }

    private async Task DisconnectSftpAsync()
    {
        if (_sftp is null || _sftpConnection is null) return;
        IsRemoteBusy = true;
        try
        {
            await _sftp.DisconnectAsync(_sftpConnection.ConnectionKey);
            _sftpConnection = null;
            if (QuickView is { IsOpen: true, SourceEndpoint: FileTransferEndpointKind.Sftp })
                QuickView.Close();
            RemoteEntries.Clear();
            _selectedRemoteRows = [];
            RemoteStatus = _localization["SftpDisconnected"];
            OnPropertyChanged(nameof(IsSftpConnected));
        }
        catch (Exception ex) { RemoteStatus = _localization.Format("SftpConnectionFailed", ex.Message); }
        finally { IsRemoteBusy = false; NotifyRemoteCommandStates(); }
    }

    private async Task RefreshRemoteAsync()
    {
        if (_sftp is null || _sftpConnection is null) return;
        IsRemoteBusy = true;
        try
        {
            var entries = await _sftp.ListAsync(_sftpConnection.ConnectionKey, RemotePath);
            RemoteEntries.Clear();
            foreach (var entry in entries)
            {
                var size = entry.Size is null ? string.Empty : FormatBytes(entry.Size.Value);
                var modified = entry.ModifiedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? string.Empty;
                RemoteEntries.Add(new RemoteBrowserRow(
                    entry, entry.Name, _localization.TranslateEnum(entry.Type), size, modified));
            }
            _selectedRemoteRows = [];
            RemoteStatus = _localization.Format("RemoteItemsLoaded", entries.Count);
        }
        catch (Exception ex) { RemoteStatus = _localization.Format("RemoteBrowseFailed", ex.Message); }
        finally { IsRemoteBusy = false; NotifyRemoteCommandStates(); }
    }

    private async Task RemoteUpAsync()
    {
        if (RemotePath == "/") return;
        var trimmed = RemotePath.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        RemotePath = slash <= 0 ? "/" : trimmed[..slash];
        await RefreshRemoteAsync();
    }

    private async Task UploadRemoteAsync()
    {
        if (_transferQueue is null || _sftpConnection is null) return;
        var selected = ActivePane.SelectedEntries.Where(item => !item.IsParent).ToArray();
        var requests = selected.Select(item => new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = item.FullPath,
            DestinationDirectory = RemotePath,
            SourceEndpoint = FileTransferEndpointKind.Local,
            DestinationEndpoint = FileTransferEndpointKind.Sftp,
            DestinationConnectionKey = _sftpConnection.ConnectionKey,
            ConflictPolicy = SelectedConflictPolicy,
            VerifyAfterCopy = VerifyTransfers
        }).ToArray();
        if (requests.Length == 0) return;
        await _transferQueue.EnqueueAsync(requests);
        RemoteStatus = _localization.Format("RemoteTransfersQueued", requests.Length);
        RebuildTransferQueue();
    }

    private async Task DownloadRemoteAsync()
    {
        if (_transferQueue is null || _sftpConnection is null || !Directory.Exists(ActivePane.CurrentPath)) return;
        var requests = _selectedRemoteRows.Select(row => new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = row.Entry.FullPath,
            DestinationDirectory = ActivePane.CurrentPath,
            SourceEndpoint = FileTransferEndpointKind.Sftp,
            DestinationEndpoint = FileTransferEndpointKind.Local,
            SourceConnectionKey = _sftpConnection.ConnectionKey,
            ConflictPolicy = SelectedConflictPolicy,
            VerifyAfterCopy = VerifyTransfers
        }).ToArray();
        if (requests.Length == 0) return;
        await _transferQueue.EnqueueAsync(requests);
        RemoteStatus = _localization.Format("RemoteTransfersQueued", requests.Length);
        RebuildTransferQueue();
    }

    private bool CanConnectSftp() => _sftp is not null && !IsRemoteBusy && !IsSftpConnected
        && !string.IsNullOrWhiteSpace(SftpHost) && !string.IsNullOrWhiteSpace(SftpUsername)
        && !string.IsNullOrEmpty(SftpPassword) && SftpPort is > 0 and <= 65535;
    private bool CanUploadRemote() => _transferQueue is not null && IsSftpConnected && !IsRemoteBusy && !ReadOnlyMode
        && ActivePane.SelectedEntries.Any(item => !item.IsParent);
    private bool CanDownloadRemote() => _transferQueue is not null && IsSftpConnected && !IsRemoteBusy && !ReadOnlyMode
        && _selectedRemoteRows.Count > 0 && Directory.Exists(ActivePane.CurrentPath);

    private void NotifyRemoteCommandStates()
    {
        ConnectSftpCommand.NotifyCanExecuteChanged();
        DisconnectSftpCommand.NotifyCanExecuteChanged();
        RefreshRemoteCommand.NotifyCanExecuteChanged();
        RemoteUpCommand.NotifyCanExecuteChanged();
        UploadRemoteCommand.NotifyCanExecuteChanged();
        DownloadRemoteCommand.NotifyCanExecuteChanged();
        OpenQuickViewCommand.NotifyCanExecuteChanged();
    }

    private RemoteBrowserRow? GetRemoteQuickViewEntry() =>
        _selectedRemoteRows.Count == 1
        && _selectedRemoteRows[0].Entry is { Type: FileEntryType.File, IsSymbolicLink: false }
            ? _selectedRemoteRows[0]
            : null;

    private static string NormalizeRemoteUiPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private async Task CompareDirectoriesAsync()
    {
        if (_directoryComparison is null) return;
        var leftPath = LeftPane.CurrentPath;
        var rightPath = RightPane.CurrentPath;
        if (!Directory.Exists(leftPath) || !Directory.Exists(rightPath)) return;
        ShowPage("Sync");
        _comparisonCancellation?.Cancel();
        _comparisonCancellation?.Dispose();
        _comparisonCancellation = new CancellationTokenSource();
        IsComparing = true;
        ComparisonRows.Clear();
        _selectedComparisonRows = [];
        ComparisonSummary = _localization["ComparisonStarting"];
        var progress = new Progress<DirectoryComparisonProgress>(value =>
            ComparisonSummary = _localization.Format("ComparisonProgress", value.EntriesScanned, value.CurrentPath ?? string.Empty));
        try
        {
            var request = new DirectoryComparisonRequest
            {
                LeftPath = leftPath,
                RightPath = rightPath,
                Recursive = true,
                Mode = SelectedComparisonMode
            };
            _comparisonResult = await Task.Run(
                () => _directoryComparison.CompareAsync(request, progress, _comparisonCancellation.Token),
                _comparisonCancellation.Token);
            RebuildComparisonRows();
            OnPropertyChanged(nameof(ComparisonLeftPath));
            OnPropertyChanged(nameof(ComparisonRightPath));
            var differences = _comparisonResult.Entries.Count(item => item.Difference != DirectoryDifferenceKind.Identical);
            ComparisonSummary = _localization.Format(
                "ComparisonCompleted", differences, _comparisonResult.Entries.Count, _comparisonResult.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) { ComparisonSummary = _localization["ComparisonCancelled"]; }
        catch (Exception ex) { ComparisonSummary = _localization.Format("ComparisonFailed", ex.Message); }
        finally { IsComparing = false; }
    }

    private void CancelComparison() => _comparisonCancellation?.Cancel();

    private void RebuildComparisonRows()
    {
        if (_comparisonResult is null) return;
        ComparisonRows.Clear();
        foreach (var item in _comparisonResult.Entries.Where(item =>
                     !HideIdenticalComparison || item.Difference != DirectoryDifferenceKind.Identical))
        {
            ComparisonRows.Add(new DirectoryComparisonRow(
                item,
                item.RelativePath,
                _localization.TranslateEnum(item.Difference),
                FormatComparisonSide(item.Left),
                FormatComparisonSide(item.Right),
                item.Error));
        }
        _selectedComparisonRows = [];
        NotifyComparisonCommandStates();
    }

    private string FormatComparisonSide(DirectoryComparisonSide? side)
    {
        if (side is null) return "—";
        if (side.Type == FileEntryType.Directory) return _localization["Directory"];
        var modified = side.ModifiedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
        return $"{FormatBytes(side.Size ?? 0)}  •  {modified}";
    }

    private async Task QueueComparisonAsync(bool leftToRight)
    {
        if (_transferQueue is null || _comparisonResult is null || _synchronizationPlanner is null) return;
        var requests = _synchronizationPlanner.CreatePlan(
            _comparisonResult,
            _selectedComparisonRows.Select(row => row.Entry),
            leftToRight ? DirectorySyncDirection.LeftToRight : DirectorySyncDirection.RightToLeft,
            VerifyTransfers);
        if (requests.Count == 0)
        {
            StatusMessage = _localization["NoSynchronizationSources"];
            return;
        }
        await _transferQueue.EnqueueAsync(requests);
        StatusMessage = _localization.Format("SynchronizationQueued", requests.Count);
        RebuildTransferQueue();
    }

    private bool CanCompareDirectories() => _directoryComparison is not null && !IsComparing
        && Directory.Exists(LeftPane.CurrentPath) && Directory.Exists(RightPane.CurrentPath)
        && !PathComparer.Equals(Path.TrimEndingDirectorySeparator(LeftPane.CurrentPath),
            Path.TrimEndingDirectorySeparator(RightPane.CurrentPath));

    private bool CanQueueComparison(bool leftToRight) => _transferQueue is not null && !ReadOnlyMode && !IsComparing
        && _synchronizationPlanner is not null && _selectedComparisonRows.Any(row =>
            row.Entry.Difference is not (DirectoryDifferenceKind.Identical or DirectoryDifferenceKind.Error)
            && (leftToRight ? row.Entry.Left : row.Entry.Right) is not null);

    private void NotifyComparisonCommandStates()
    {
        CompareDirectoriesCommand.NotifyCanExecuteChanged();
        CancelComparisonCommand.NotifyCanExecuteChanged();
        SyncLeftToRightCommand.NotifyCanExecuteChanged();
        SyncRightToLeftCommand.NotifyCanExecuteChanged();
    }

    private async Task TrashEntryAsync()
    {
        if (IsFilesPage && ActivePane.IsArchive)
        {
            await DeleteArchiveEntriesAsync();
            return;
        }
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

    private async Task DeleteArchiveEntriesAsync()
    {
        if (_archiveMutations is null || ActivePane.ArchivePath is not { } archivePath) return;
        var selected = ActivePane.SelectedEntries.Where(item => !item.IsParent).ToArray();
        if (selected.Length == 0 || selected.Any(item => item.Endpoint != FileTransferEndpointKind.Archive
                                                         || item.IsSymbolicLink
                                                         || item.EntryPath is null)) return;
        var confirmed = await _desktop.ConfirmAsync(
            _localization["ArchiveDeleteTitle"],
            _localization.Format("ArchiveDeleteConfirmation", selected.Length, archivePath),
            _localization["ArchiveDelete"]);
        if (!confirmed) return;
        await RunArchiveMutationAsync(async token =>
        {
            await _archiveMutations.DeleteManyAsync(archivePath,
                selected.Select(item => item.EntryPath!).ToArray(), CreateFileProgress(), token);
        }, _localization.Format("ArchiveDeleted", selected.Length));
        ActivePane.Refresh();
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

    private async Task RunArchiveMutationAsync(
        Func<CancellationToken, Task> operation,
        string successMessage)
    {
        _automation.NotifyUserActivity();
        _fileOperationCancellation?.Dispose();
        _fileOperationCancellation = new CancellationTokenSource();
        IsFileOperationRunning = true;
        FileOperationProgress = _localization["OperationStarting"];
        try
        {
            await operation(_fileOperationCancellation.Token);
            StatusMessage = successMessage;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = _localization["OperationCancelled"];
        }
        catch (Exception ex)
        {
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
        if (_transferQueue is not null)
        {
            var completedOperationIds = completed.Select(item => item.Id).ToHashSet();
            foreach (var job in _transferQueue.Items.Where(item =>
                         item.OperationId is not null && completedOperationIds.Contains(item.OperationId.Value)))
                _indexedTransferJobs.Add(job.Id);
        }
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
    private bool CanEditSelectedEntry() => GetSelectedEntry() is
        { Available: true, Type: FileEntryType.File, IsParent: false }
        && (!IsFilesPage || ActivePane.SelectedEntry?.Endpoint == FileTransferEndpointKind.Local);
    private bool CanCopySelectedEntry() => !ReadOnlyMode && !IsFileOperationRunning
        && (IsFilesPage
            ? ActivePane.SelectedEntries.Any(item => !item.IsParent && !item.IsSymbolicLink)
            : GetSelectedEntries().Any(item => item is { Available: true, IsParent: false }));
    private bool CanManageSelectedEntry() => !ReadOnlyMode && !IsFileOperationRunning
                                             && (!IsFilesPage || ActivePane.SelectedEntries.All(item => item.Endpoint == FileTransferEndpointKind.Local))
                                             && GetSelectedEntries().Any(item => item is { Available: true, IsParent: false });
    private bool CanTrashSelectedEntry() => !ReadOnlyMode && !IsFileOperationRunning
        && (IsFilesPage && ActivePane.IsArchive
            ? _archiveMutations is not null && ActivePane.ArchivePath is { } archivePath
              && _archives?.GetCapabilities(archivePath).HasFlag(ArchiveCapabilities.DeleteEntries) == true
              && ActivePane.SelectedEntries.Any(item => !item.IsParent)
              && ActivePane.SelectedEntries.All(item => item.Endpoint == FileTransferEndpointKind.Archive
                                                        && !item.IsSymbolicLink && item.EntryPath is not null)
            : CanManageSelectedEntry());
    private bool CanRenameSelectedEntry() => !ReadOnlyMode && !IsFileOperationRunning
                                             && (!IsFilesPage || ActivePane.SelectedEntries.All(item => item.Endpoint == FileTransferEndpointKind.Local))
                                             && GetSelectedEntries() is { Count: 1 }
                                             && GetSelectedEntries()[0] is { Available: true, IsParent: false };
    private bool CanOpenMultiRename() => MultiRename is not null && !ReadOnlyMode && !IsFileOperationRunning
                                         && IsFilesPage && !ActivePane.IsArchive
                                         && ActivePane.SelectedEntries.Any(item => !item.IsParent
                                             && item.Endpoint == FileTransferEndpointKind.Local && !item.IsSymbolicLink)
                                         && ActivePane.SelectedEntries.All(item => item.IsParent
                                             || item.Endpoint == FileTransferEndpointKind.Local && !item.IsSymbolicLink);
    private bool CanOpenQuickView() => QuickView is not null
                                       && (IsRemotePage
                                           ? _sftpConnection is not null && GetRemoteQuickViewEntry() is not null
                                           : IsFilesPage && ActivePane.SelectedEntry is
                                       {
                                           IsParent: false,
                                           Type: FileEntryType.File,
                                           IsSymbolicLink: false
                                       } entry
                                       && (entry.Endpoint == FileTransferEndpointKind.Local
                                           || entry.Endpoint == FileTransferEndpointKind.Archive
                                           && entry.ContainerPath is not null && entry.EntryPath is not null));
    private bool CanCreateFolder() => !ReadOnlyMode && !IsFileOperationRunning
                                      && (!IsFilesPage || !ActivePane.IsArchive)
                                      && (!string.IsNullOrWhiteSpace(CurrentDirectoryPath) || SelectedTarget is not null);
    private bool CanCreateArchive() => _archiveMutations is not null && !ReadOnlyMode && !IsFileOperationRunning
                                       && IsFilesPage && !ActivePane.IsArchive
                                       && Directory.Exists(ActivePane.CurrentPath)
                                       && ActivePane.SelectedEntries.Any(item => !item.IsParent
                                           && item.Endpoint == FileTransferEndpointKind.Local && !item.IsSymbolicLink);
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

    private void OnTransferQueueChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            RebuildTransferQueue();
            RefreshCompletedArchiveDestinations();
            _ = SynchronizeTransferQueueAsync();
        });

    private void RefreshCompletedArchiveDestinations()
    {
        if (_transferQueue is null) return;
        foreach (var item in _transferQueue.Items.Where(item => item.State == FileTransferState.Completed
                                                                && item.Request.DestinationEndpoint == FileTransferEndpointKind.Archive
                                                                && item.Request.DestinationConnectionKey is not null
                                                                && _refreshedArchiveJobs.Add(item.Id)))
        {
            if (LeftPane.IsArchive && PathComparer.Equals(LeftPane.ArchivePath, item.Request.DestinationConnectionKey))
                LeftPane.Refresh();
            if (RightPane.IsArchive && PathComparer.Equals(RightPane.ArchivePath, item.Request.DestinationConnectionKey))
                RightPane.Refresh();
        }
    }

    private void RebuildTransferQueue()
    {
        if (_transferQueue is null) return;
        var selectedId = SelectedTransfer?.Id;
        TransferQueue.Clear();
        foreach (var item in _transferQueue.Items.OrderByDescending(item => item.CreatedAt).Take(100))
        {
            var progress = item.TotalBytes is > 0
                ? _localization.Format("OperationProgress", FormatBytes(item.BytesCompleted), FormatBytes(item.TotalBytes.Value))
                : item.CurrentPath is null ? string.Empty : _localization.Format("OperationProgressUnknown", item.CurrentPath);
            TransferQueue.Add(new FileTransferQueueRow(
                item.Id,
                _localization.TranslateEnum(item.Request.Kind),
                item.Request.SourcePath,
                item.DestinationPath ?? item.Request.DestinationDirectory,
                _localization.TranslateEnum(item.State),
                progress,
                item.Error,
                item.State));
        }
        SelectedTransfer = TransferQueue.FirstOrDefault(item => item.Id == selectedId) ?? TransferQueue.FirstOrDefault();
        var active = TransferQueue.FirstOrDefault(item => item.RawState == FileTransferState.Running);
        FileOperationProgress = active?.Progress ?? string.Empty;
        OnPropertyChanged(nameof(HasActiveTransfers));
        NotifyCommandStates();
        NotifyTransferCommandStates();
    }

    private async Task SynchronizeTransferQueueAsync()
    {
        if (_transferQueue is null || _workspace is null || Targets.Count == 0) return;
        if (!await _queueIndexGate.WaitAsync(0)) return;
        try
        {
            var completed = _transferQueue.Items
                .Where(item => item.State == FileTransferState.Completed && !_indexedTransferJobs.Contains(item.Id)
                               && item.DestinationPath is not null
                               && item.Request.SourceEndpoint == FileTransferEndpointKind.Local
                               && item.Request.DestinationEndpoint == FileTransferEndpointKind.Local)
                .OrderBy(item => item.CompletedAt)
                .ToArray();
            if (completed.Length == 0) return;
            var records = completed.Select(item => new FileOperationRecord
            {
                Id = item.OperationId ?? item.Id,
                Kind = item.Request.Kind,
                SourcePath = item.Request.SourcePath,
                DestinationPath = item.DestinationPath,
                CompletedAt = item.CompletedAt ?? DateTimeOffset.UtcNow,
                CanUndo = item.OperationId is not null
            }).ToArray();
            await _store.ApplyFileOperationsAsync(records, Targets.ToArray(), CancellationToken.None);
            foreach (var item in completed) _indexedTransferJobs.Add(item.Id);
            RebuildFileOperationHistory();
            TryRefreshPane(LeftPane);
            TryRefreshPane(RightPane);
            await SearchNowAsync();
        }
        catch (Exception ex) { StatusMessage = _localization.Format("OperationFailed", ex.Message); }
        finally { _queueIndexGate.Release(); }
    }

    private Task PauseSelectedTransferAsync() => SelectedTransfer is null || _transferQueue is null
        ? Task.CompletedTask
        : _transferQueue.PauseAsync(SelectedTransfer.Id);

    private Task ResumeSelectedTransferAsync() => SelectedTransfer is null || _transferQueue is null
        ? Task.CompletedTask
        : _transferQueue.ResumeAsync(SelectedTransfer.Id);

    private Task RetrySelectedTransferAsync() => SelectedTransfer is null || _transferQueue is null
        ? Task.CompletedTask
        : _transferQueue.RetryAsync(SelectedTransfer.Id);

    private Task CancelSelectedTransferAsync() => SelectedTransfer is null || _transferQueue is null
        ? Task.CompletedTask
        : _transferQueue.CancelAsync(SelectedTransfer.Id);

    private bool CanPauseTransfer() => SelectedTransfer?.RawState is FileTransferState.Queued or FileTransferState.Running;
    private bool CanResumeTransfer() => SelectedTransfer?.RawState == FileTransferState.Paused;
    private bool CanRetryTransfer() => SelectedTransfer?.RawState is FileTransferState.Failed or FileTransferState.Cancelled;
    private bool CanCancelTransfer() => SelectedTransfer?.RawState is
        FileTransferState.Queued or FileTransferState.Running or FileTransferState.Paused or FileTransferState.Failed;

    private void NotifyTransferCommandStates()
    {
        PauseTransferCommand.NotifyCanExecuteChanged();
        ResumeTransferCommand.NotifyCanExecuteChanged();
        RetryTransferCommand.NotifyCanExecuteChanged();
        CancelTransferCommand.NotifyCanExecuteChanged();
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
        OnPropertyChanged(nameof(SelectedResultConfidenceDisplay));
        OnPropertyChanged(nameof(SelectedResultConfidenceDetail));
        OnPropertyChanged(nameof(RescueTipText));
        OnPropertyChanged(nameof(RescueQuoteText));
        OnPropertyChanged(nameof(RescueSearchButtonText));
        RaiseRescueState();
        RebuildFileOperationHistory();
        RebuildComparisonRows();

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
        BackgroundActivity.ResumingScan => _localization.Format("BackgroundResuming", status.Path ?? string.Empty),
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
        CopyPathCommand.NotifyCanExecuteChanged(); OpenResultCommand.NotifyCanExecuteChanged(); OpenQuickViewCommand.NotifyCanExecuteChanged();
        EditEntryCommand.NotifyCanExecuteChanged(); OpenFolderCommand.NotifyCanExecuteChanged();
        NavigateUpCommand.NotifyCanExecuteChanged();
        NavigateLeftUpCommand.NotifyCanExecuteChanged(); NavigateRightUpCommand.NotifyCanExecuteChanged();
        NavigateLeftBackCommand.NotifyCanExecuteChanged(); NavigateLeftForwardCommand.NotifyCanExecuteChanged();
        NavigateRightBackCommand.NotifyCanExecuteChanged(); NavigateRightForwardCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged(); InvertSelectionCommand.NotifyCanExecuteChanged();
        SelectByExtensionCommand.NotifyCanExecuteChanged(); SelectByMaskCommand.NotifyCanExecuteChanged();
        RestorePreviousSelectionCommand.NotifyCanExecuteChanged();
        CloseTabCommand.NotifyCanExecuteChanged(); ReopenClosedTabCommand.NotifyCanExecuteChanged();
        AddHotlistCommand.NotifyCanExecuteChanged(); RemoveHotlistCommand.NotifyCanExecuteChanged();
        OpenHotlistCommand.NotifyCanExecuteChanged();
        ToggleAccessModeCommand.NotifyCanExecuteChanged(); CreateFolderCommand.NotifyCanExecuteChanged(); CreateArchiveCommand.NotifyCanExecuteChanged(); RenameEntryCommand.NotifyCanExecuteChanged(); OpenMultiRenameCommand.NotifyCanExecuteChanged();
        CopyEntryCommand.NotifyCanExecuteChanged(); MoveEntryCommand.NotifyCanExecuteChanged(); TrashEntryCommand.NotifyCanExecuteChanged();
        UndoFileOperationCommand.NotifyCanExecuteChanged(); CancelFileOperationCommand.NotifyCanExecuteChanged();
        UnmountDriveCommand.NotifyCanExecuteChanged(); EjectDriveCommand.NotifyCanExecuteChanged();
        NotifyComparisonCommandStates();
        NotifyRemoteCommandStates();
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
