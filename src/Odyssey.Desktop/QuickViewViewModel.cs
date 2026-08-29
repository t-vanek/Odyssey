using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Odyssey.Core;

namespace Odyssey.Desktop;

public sealed record QuickViewChunkSize(int Bytes, string Display);

public sealed class QuickViewViewModel : ObservableObject
{
    private readonly IQuickViewService _service;
    private readonly LocalizationService _localization;
    private CancellationTokenSource? _loadCancellation;
    private QuickViewVersion? _version;
    private QuickViewChunk? _chunk;
    private long _generation;
    private bool _isOpen;
    private bool _isBusy;
    private string _sourcePath = string.Empty;
    private FileTransferEndpointKind _endpoint;
    private string? _connectionKey;
    private string? _containerPath;
    private string? _entryPath;
    private string _content = string.Empty;
    private string _status = string.Empty;
    private string _error = string.Empty;
    private QuickViewDisplayMode _mode;
    private string _selectedEncoding = "auto";
    private QuickViewChunkSize _selectedChunkSize;

    public QuickViewViewModel(IQuickViewService service, LocalizationService localization)
    {
        _service = service;
        _localization = localization;
        ChunkSizes = new[]
        {
            new QuickViewChunkSize(16 * 1024, "16 KiB"),
            new QuickViewChunkSize(32 * 1024, "32 KiB"),
            new QuickViewChunkSize(64 * 1024, "64 KiB"),
            new QuickViewChunkSize(Math.Min(256 * 1024, service.MaximumChunkBytes),
                $"{Math.Min(256 * 1024, service.MaximumChunkBytes) / 1024} KiB")
        }.Where(item => item.Bytes <= service.MaximumChunkBytes).DistinctBy(item => item.Bytes).ToArray();
        _selectedChunkSize = ChunkSizes.FirstOrDefault(item => item.Bytes == 32 * 1024) ?? ChunkSizes[0];
        Modes = Enum.GetValues<QuickViewDisplayMode>();
        Encodings = service.SupportedEncodings;
        PreviousCommand = new AsyncRelayCommand(PreviousAsync, () => !IsBusy && _chunk?.HasPrevious == true);
        NextCommand = new AsyncRelayCommand(NextAsync, () => !IsBusy && _chunk?.HasNext == true);
        FirstCommand = new AsyncRelayCommand(() => LoadAsync(0), () => !IsBusy && _chunk?.HasPrevious == true);
        LastCommand = new AsyncRelayCommand(LastAsync, () => !IsBusy && _chunk?.HasNext == true);
        ReloadCommand = new AsyncRelayCommand(() => LoadAsync(_chunk?.Offset ?? 0), () => !IsBusy && IsOpen);
        CancelCommand = new RelayCommand(() => _loadCancellation?.Cancel(), () => IsBusy);
        CloseCommand = new RelayCommand(Close, () => !IsBusy);
    }

    public IReadOnlyList<QuickViewDisplayMode> Modes { get; }
    public IReadOnlyList<string> Encodings { get; }
    public IReadOnlyList<QuickViewChunkSize> ChunkSizes { get; }
    public IAsyncRelayCommand PreviousCommand { get; }
    public IAsyncRelayCommand NextCommand { get; }
    public IAsyncRelayCommand FirstCommand { get; }
    public IAsyncRelayCommand LastCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanChangeOptions));
            NotifyCommands();
        }
    }
    public bool CanChangeOptions => !IsBusy;
    public string SourcePath { get => _sourcePath; private set => SetProperty(ref _sourcePath, value); }
    public FileTransferEndpointKind SourceEndpoint => _endpoint;
    public string SourceName => Path.GetFileName(SourcePath);
    public string Content { get => _content; private set => SetProperty(ref _content, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Error { get => _error; private set { SetProperty(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public string EffectiveMode => _chunk is null
        ? string.Empty
        : _localization.TranslateEnum(_chunk.EffectiveMode);
    public string EffectiveEncoding => _chunk?.EncodingName ?? string.Empty;
    public IReadOnlyList<QuickViewSyntaxSpan> SyntaxSpans => _chunk?.SyntaxSpans ?? [];
    public bool HasSyntaxHighlighting => SyntaxSpans.Count > 0;
    public bool ShowPlainContent => !HasSyntaxHighlighting;
    public string SyntaxStatus => _chunk?.SyntaxLanguage is not { Length: > 0 } language
        ? string.Empty
        : _chunk.IsSyntaxHighlightingTruncated
            ? _localization.Format("QuickViewSyntaxLimited", language)
            : _localization.Format("QuickViewSyntax", language);
    public string OffsetSummary => _chunk is null
        ? string.Empty
        : _localization.Format("QuickViewOffset", _chunk.Offset, _chunk.NextOffset, _chunk.Version.Length);
    public QuickViewDisplayMode Mode
    {
        get => _mode;
        set
        {
            if (!SetProperty(ref _mode, value)) return;
            ReloadForOptionChange();
        }
    }
    public string SelectedEncoding
    {
        get => _selectedEncoding;
        set
        {
            if (!SetProperty(ref _selectedEncoding, value ?? "auto")) return;
            ReloadForOptionChange();
        }
    }
    public QuickViewChunkSize SelectedChunkSize
    {
        get => _selectedChunkSize;
        set
        {
            if (value is null || !SetProperty(ref _selectedChunkSize, value)) return;
            ReloadForOptionChange();
        }
    }

    public Task OpenAsync(string path) => OpenSourceAsync(
        Path.GetFullPath(path), FileTransferEndpointKind.Local, null, null, null);

    public Task OpenArchiveAsync(string displayPath, string archivePath, string entryPath) => OpenSourceAsync(
        displayPath, FileTransferEndpointKind.Archive, null, Path.GetFullPath(archivePath), entryPath);

    public Task OpenSftpAsync(string path, string connectionKey) => OpenSourceAsync(
        path, FileTransferEndpointKind.Sftp, connectionKey, null, null);

    private async Task OpenSourceAsync(
        string displayPath,
        FileTransferEndpointKind endpoint,
        string? connectionKey,
        string? containerPath,
        string? entryPath)
    {
        SourcePath = displayPath;
        _endpoint = endpoint;
        OnPropertyChanged(nameof(SourceEndpoint));
        _connectionKey = connectionKey;
        _containerPath = containerPath;
        _entryPath = entryPath;
        OnPropertyChanged(nameof(SourceName));
        _version = null;
        _chunk = null;
        Content = string.Empty;
        Error = string.Empty;
        IsOpen = true;
        await LoadAsync(0);
    }

    public void Close()
    {
        Interlocked.Increment(ref _generation);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        IsBusy = false;
        IsOpen = false;
        Content = string.Empty;
        _chunk = null;
        _version = null;
        NotifyPresentation();
    }

    private Task PreviousAsync() => LoadAsync(Math.Max(0, (_chunk?.Offset ?? 0) - SelectedChunkSize.Bytes));
    private Task NextAsync() => LoadAsync(_chunk?.NextOffset ?? 0);
    private Task LastAsync() => LoadAsync(Math.Max(0, (_version?.Length ?? 0) - SelectedChunkSize.Bytes));

    private void ReloadForOptionChange()
    {
        if (!IsOpen || IsBusy) return;
        _ = LoadAsync(_chunk?.Offset ?? 0);
    }

    private async Task LoadAsync(long offset)
    {
        var generation = Interlocked.Increment(ref _generation);
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        IsBusy = true;
        Error = string.Empty;
        Status = _localization["QuickViewLoading"];
        try
        {
            var chunk = await _service.ReadAsync(new QuickViewReadRequest
            {
                Path = SourcePath,
                Endpoint = _endpoint,
                ConnectionKey = _connectionKey,
                ContainerPath = _containerPath,
                EntryPath = _entryPath,
                Offset = offset,
                MaximumBytes = SelectedChunkSize.Bytes,
                Mode = Mode,
                EncodingName = SelectedEncoding,
                ExpectedVersion = _version
            }, cancellation.Token);
            if (generation != Volatile.Read(ref _generation) || cancellation.IsCancellationRequested) return;
            _version ??= chunk.Version;
            _chunk = chunk;
            Content = chunk.Content;
            Status = _localization.Format("QuickViewLoaded", chunk.BytesRead);
            NotifyPresentation();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (generation == Volatile.Read(ref _generation)) Status = _localization["QuickViewCancelled"];
        }
        catch (Exception exception)
        {
            if (generation == Volatile.Read(ref _generation))
            {
                Error = exception.Message;
                Status = _localization["QuickViewFailed"];
            }
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation)) IsBusy = false;
        }
    }

    private void NotifyPresentation()
    {
        OnPropertyChanged(nameof(EffectiveMode));
        OnPropertyChanged(nameof(EffectiveEncoding));
        OnPropertyChanged(nameof(OffsetSummary));
        OnPropertyChanged(nameof(SyntaxSpans));
        OnPropertyChanged(nameof(HasSyntaxHighlighting));
        OnPropertyChanged(nameof(ShowPlainContent));
        OnPropertyChanged(nameof(SyntaxStatus));
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        PreviousCommand.NotifyCanExecuteChanged(); NextCommand.NotifyCanExecuteChanged();
        FirstCommand.NotifyCanExecuteChanged(); LastCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }
}
