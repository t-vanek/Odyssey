using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Odyssey.Core;

namespace Odyssey.Desktop;

public interface ITextEditorBridge
{
    Task ShowDocumentAsync(TextEditorDocument document, bool readOnly, CancellationToken cancellationToken = default);
    Task<string> ReadContentAsync(CancellationToken cancellationToken = default);
    Task SetReadOnlyAsync(bool readOnly, CancellationToken cancellationToken = default);
    Task MarkSavedAsync(CancellationToken cancellationToken = default);
    Task FocusAsync(CancellationToken cancellationToken = default);
}

public sealed class TextEditorViewModel : ObservableObject
{
    private readonly ITextEditorService _service;
    private readonly IDesktopInteractionService _desktop;
    private readonly LocalizationService _localization;
    private ITextEditorBridge? _bridge;
    private TextEditorDocument? _document;
    private FileAccessMode _accessMode = FileAccessMode.ReadOnly;
    private bool _isOpen;
    private bool _isDirty;
    private bool _isBusy;
    private bool _isHostUnavailable;
    private bool _areHostDetailsVisible;
    private string _status = string.Empty;
    private string? _error;
    private string _hostFailureDetails = string.Empty;
    private CancellationTokenSource? _activeOperation;

    public TextEditorViewModel(
        ITextEditorService service,
        IDesktopInteractionService desktop,
        LocalizationService localization)
    {
        _service = service;
        _desktop = desktop;
        _localization = localization;
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        ReloadCommand = new AsyncRelayCommand(ReloadAsync, () => IsOpen && !IsBusy);
        CloseCommand = new AsyncRelayCommand(CloseAsync, () => IsOpen && !IsBusy);
        OpenExternalCommand = new AsyncRelayCommand(OpenExternalAsync, () => IsOpen && Document is not null);
        RetryHostCommand = new RelayCommand(RetryHost, () => IsOpen && IsHostUnavailable && !IsBusy);
        ToggleHostDetailsCommand = new RelayCommand(ToggleHostDetails, () => IsHostUnavailable);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        _localization.LanguageChanged += (_, _) => NotifyLocalizedState();
    }

    public event EventHandler? Saved;
    public event EventHandler? HostRetryRequested;
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }
    public IAsyncRelayCommand CloseCommand { get; }
    public IAsyncRelayCommand OpenExternalCommand { get; }
    public IRelayCommand RetryHostCommand { get; }
    public IRelayCommand ToggleHostDetailsCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public TextEditorDocument? Document => _document;
    public string SourceName => Document?.Name ?? string.Empty;
    public string SourcePath => Document?.Path ?? string.Empty;
    public string Language => Document?.LanguageId ?? string.Empty;
    public string Encoding => Document is null
        ? string.Empty
        : $"{Document.EncodingName}{(Document.HasByteOrderMark ? " BOM" : string.Empty)}";
    public bool IsReadOnly => AccessMode != FileAccessMode.ManageFiles;
    public bool HasError => Error is not null;
    public bool HasOperationalError => HasError && !IsHostUnavailable;
    public bool IsHostAvailable => !IsHostUnavailable;
    public bool CanSaveNow => CanSave();
    public string AccessStatus => _localization[IsReadOnly ? "TextEditorReadOnly" : "TextEditorWritable"];
    public string HostFailureDetails => _hostFailureDetails;
    public string HostDetailsButtonText =>
        _localization[AreHostDetailsVisible ? "TextEditorHideDetails" : "TextEditorShowDetails"];

    public FileAccessMode AccessMode
    {
        get => _accessMode;
        set
        {
            if (!SetProperty(ref _accessMode, value)) return;
            _service.AccessMode = value;
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(AccessStatus));
            NotifyCommands();
            if (_bridge is not null && IsOpen) _ = SetBridgeReadOnlyAsync(_bridge, IsReadOnly);
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set { if (SetProperty(ref _isOpen, value)) NotifyCommands(); }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!SetProperty(ref _isDirty, value)) return;
            OnPropertyChanged(nameof(CanSaveNow));
            NotifyCommands();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (SetProperty(ref _isBusy, value)) NotifyCommands(); }
    }

    public bool IsHostUnavailable
    {
        get => _isHostUnavailable;
        private set
        {
            if (!SetProperty(ref _isHostUnavailable, value)) return;
            OnPropertyChanged(nameof(IsHostAvailable));
            OnPropertyChanged(nameof(HasOperationalError));
            NotifyCommands();
        }
    }

    public bool AreHostDetailsVisible
    {
        get => _areHostDetailsVisible;
        private set
        {
            if (!SetProperty(ref _areHostDetailsVisible, value)) return;
            OnPropertyChanged(nameof(HostDetailsButtonText));
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string? Error
    {
        get => _error;
        private set
        {
            if (!SetProperty(ref _error, value)) return;
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(HasOperationalError));
        }
    }

    public async Task OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        Cancel();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeOperation = linked;
        IsBusy = true;
        Error = null;
        Status = _localization["TextEditorLoading"];
        try
        {
            var document = await _service.OpenAsync(path, linked.Token);
            _document = document;
            NotifyDocument();
            IsDirty = false;
            var wasOpen = IsOpen;
            var existingBridge = _bridge;
            Status = _localization.Format("TextEditorLoaded", document.Version.Length);
            IsOpen = true;
            if (existingBridge is not null)
            {
                try
                {
                    await existingBridge.ShowDocumentAsync(document, IsReadOnly, linked.Token);
                    ReportHostReady();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ReportHostFailure(exception.Message);
                }
            }
            else if (wasOpen && _bridge is null)
            {
                HostRetryRequested?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            if (ReferenceEquals(_activeOperation, linked)) _activeOperation = null;
            IsBusy = false;
        }
    }

    public void AttachBridge(ITextEditorBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        _bridge = bridge;
        NotifyCommands();
        if (IsOpen && Document is not null) _ = ShowOnAttachedBridgeAsync(bridge, Document);
    }

    public void DetachBridge(ITextEditorBridge bridge)
    {
        if (!ReferenceEquals(_bridge, bridge)) return;
        _bridge = null;
        NotifyCommands();
    }

    public void SetDirtyFromEditor(bool dirty)
    {
        if (IsOpen && !IsBusy) IsDirty = dirty;
    }

    public void ReportHostFailure(string message)
    {
        _hostFailureDetails = message;
        OnPropertyChanged(nameof(HostFailureDetails));
        AreHostDetailsVisible = false;
        IsHostUnavailable = true;
        Error = _localization.Format("TextEditorHostFailed", message);
        Status = _localization["TextEditorExternalAvailable"];
    }

    public void ReportHostReady()
    {
        if (!IsHostUnavailable) return;
        IsHostUnavailable = false;
        AreHostDetailsVisible = false;
        _hostFailureDetails = string.Empty;
        OnPropertyChanged(nameof(HostFailureDetails));
        Error = null;
        Status = _localization.Format("TextEditorLoaded", Document?.Version.Length ?? 0);
    }

    public void RequestSaveFromEditor()
    {
        if (SaveCommand.CanExecute(null)) SaveCommand.Execute(null);
    }

    public void RequestCloseFromEditor()
    {
        if (CloseCommand.CanExecute(null)) CloseCommand.Execute(null);
    }

    private async Task SaveAsync()
    {
        if (Document is null || _bridge is null || IsReadOnly) return;
        IsBusy = true;
        Error = null;
        Status = _localization["TextEditorSaving"];
        using var cancellation = new CancellationTokenSource();
        _activeOperation = cancellation;
        try
        {
            var content = await _bridge.ReadContentAsync(cancellation.Token);
            var version = await _service.SaveAsync(new TextEditorSaveRequest
            {
                Path = Document.Path,
                Content = content,
                EncodingName = Document.EncodingName,
                HasByteOrderMark = Document.HasByteOrderMark,
                ExpectedVersion = Document.Version
            }, cancellation.Token);
            _document = Document with { Content = content, Version = version };
            NotifyDocument();
            await _bridge.MarkSavedAsync(CancellationToken.None);
            IsDirty = false;
            Status = _localization["TextEditorSaved"];
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Status = _localization["TextEditorCancelled"];
        }
        catch (TextDocumentChangedException exception)
        {
            Error = _localization.Format("TextEditorConflict", exception.Message);
            Status = _localization["TextEditorNotSaved"];
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            Status = _localization["TextEditorNotSaved"];
        }
        finally
        {
            if (ReferenceEquals(_activeOperation, cancellation)) _activeOperation = null;
            IsBusy = false;
        }
    }

    private async Task ReloadAsync()
    {
        if (Document is null) return;
        if (IsDirty && !await ConfirmDiscardAsync()) return;
        try { await OpenAsync(Document.Path); }
        catch (Exception exception)
        {
            Error = exception.Message;
            Status = _localization["TextEditorReloadFailed"];
        }
    }

    private async Task CloseAsync()
    {
        if (IsDirty && !await ConfirmDiscardAsync()) return;
        Cancel();
        IsOpen = false;
        IsDirty = false;
        IsHostUnavailable = false;
        AreHostDetailsVisible = false;
        _hostFailureDetails = string.Empty;
        OnPropertyChanged(nameof(HostFailureDetails));
        Error = null;
        Status = string.Empty;
        _document = null;
        NotifyDocument();
    }

    private async Task OpenExternalAsync()
    {
        if (Document is null) return;
        try
        {
            await _desktop.EditAsync(Document.Path);
            Status = _localization["EditorOpened"];
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            Status = exception.Message;
        }
    }

    private void RetryHost()
    {
        AreHostDetailsVisible = false;
        Status = _localization["TextEditorRetrying"];
        HostRetryRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ToggleHostDetails() => AreHostDetailsVisible = !AreHostDetailsVisible;

    private Task<bool> ConfirmDiscardAsync() => _desktop.ConfirmAsync(
        _localization["TextEditorDiscardTitle"],
        _localization["TextEditorDiscardPrompt"],
        _localization["TextEditorDiscard"]);

    private void Cancel() => _activeOperation?.Cancel();

    private bool CanSave() => IsOpen && Document is not null && IsDirty && !IsReadOnly && !IsBusy && _bridge is not null;

    private async Task ShowOnAttachedBridgeAsync(ITextEditorBridge bridge, TextEditorDocument document)
    {
        try
        {
            await bridge.ShowDocumentAsync(document, IsReadOnly);
            ReportHostReady();
        }
        catch (Exception exception) { ReportHostFailure(exception.Message); }
    }

    private async Task SetBridgeReadOnlyAsync(ITextEditorBridge bridge, bool readOnly)
    {
        try { await bridge.SetReadOnlyAsync(readOnly); }
        catch (Exception exception) { ReportHostFailure(exception.Message); }
    }

    private void NotifyDocument()
    {
        OnPropertyChanged(nameof(Document));
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(SourcePath));
        OnPropertyChanged(nameof(Language));
        OnPropertyChanged(nameof(Encoding));
        OnPropertyChanged(nameof(CanSaveNow));
        NotifyCommands();
    }

    private void NotifyLocalizedState()
    {
        OnPropertyChanged(nameof(AccessStatus));
        OnPropertyChanged(nameof(HostDetailsButtonText));
        if (IsHostUnavailable)
        {
            Error = _localization.Format("TextEditorHostFailed", HostFailureDetails);
            Status = _localization["TextEditorExternalAvailable"];
            return;
        }
        if (IsOpen && !IsBusy && !HasError)
            Status = _localization.Format("TextEditorLoaded", Document?.Version.Length ?? 0);
    }

    private void NotifyCommands()
    {
        SaveCommand.NotifyCanExecuteChanged();
        ReloadCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
        OpenExternalCommand.NotifyCanExecuteChanged();
        RetryHostCommand.NotifyCanExecuteChanged();
        ToggleHostDetailsCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSaveNow));
    }
}
