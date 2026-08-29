using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Odyssey.Core;

namespace Odyssey.Desktop;

public sealed class MultiRenamePreviewRow : ObservableObject
{
    private string _proposedName;
    private string _errors;
    private readonly Action<MultiRenamePreviewRow> _changed;

    public MultiRenamePreviewRow(MultiRenamePlanItem item, Action<MultiRenamePreviewRow> changed)
    {
        SourcePath = item.SourcePath;
        OriginalName = item.OriginalName;
        _proposedName = item.ProposedName;
        _errors = string.Join(" · ", item.Errors);
        _changed = changed;
    }

    public string SourcePath { get; }
    public string OriginalName { get; }
    public string ProposedName
    {
        get => _proposedName;
        set
        {
            if (!SetProperty(ref _proposedName, value ?? string.Empty)) return;
            _changed(this);
        }
    }
    public string Errors { get => _errors; private set => SetProperty(ref _errors, value); }
    public bool HasErrors => Errors.Length > 0;

    internal void ApplyValidation(MultiRenamePlanItem item)
    {
        Errors = string.Join(" · ", item.Errors);
        OnPropertyChanged(nameof(HasErrors));
    }
}

public sealed class MultiRenameViewModel : ObservableObject
{
    private readonly IMultiRenameService _service;
    private readonly IDesktopInteractionService _desktop;
    private readonly LocalizationService _localization;
    private readonly Dictionary<string, string> _manualNames = new();
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<string> _sources = [];
    private MultiRenamePlan? _plan;
    private bool _isOpen;
    private bool _isBusy;
    private string _prefix = string.Empty;
    private string _suffix = string.Empty;
    private string _searchText = string.Empty;
    private string _replacementText = string.Empty;
    private bool _useRegex;
    private bool _matchCase;
    private MultiRenameCaseMode _caseMode;
    private bool _includeCounter;
    private int _counterStart = 1;
    private int _counterStep = 1;
    private int _counterPadding = 3;
    private string _counterSeparator = "_";
    private string _dateFormat = "yyyyMMdd";
    private bool _preserveExtension = true;
    private string _extensionReplacement = string.Empty;
    private MultiRenameSortMode _sortMode;
    private bool _sortDescending;
    private FileSystemCaseMode _caseSensitivity;
    private string _status = string.Empty;
    private string _errorSummary = string.Empty;

    public MultiRenameViewModel(
        IMultiRenameService service,
        IDesktopInteractionService desktop,
        LocalizationService localization)
    {
        _service = service;
        _desktop = desktop;
        _localization = localization;
        RefreshCommand = new RelayCommand(RefreshPreview, () => !IsBusy && _plan is not null);
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync, () => CanExecute);
        UndoCommand = new AsyncRelayCommand(UndoAsync,
            () => !IsBusy && AccessMode == FileAccessMode.ManageFiles && _service.CanUndoLastBatch);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => _plan is not null && !IsBusy);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _operationCancellation?.Cancel(),
            () => IsBusy && _operationCancellation is not null);
        CloseCommand = new RelayCommand(() => IsOpen = false, () => !IsBusy);
    }

    public event EventHandler? Completed;
    public ObservableCollection<MultiRenamePreviewRow> Rows { get; } = [];
    public IReadOnlyList<MultiRenameCaseMode> CaseModes { get; } = Enum.GetValues<MultiRenameCaseMode>();
    public IReadOnlyList<MultiRenameSortMode> SortModes { get; } = Enum.GetValues<MultiRenameSortMode>();
    public IReadOnlyList<FileSystemCaseMode> CaseSensitivityModes { get; } = Enum.GetValues<FileSystemCaseMode>();
    public IRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ExecuteCommand { get; }
    public IAsyncRelayCommand UndoCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public bool IsOpen { get => _isOpen; private set => SetProperty(ref _isOpen, value); }
    public FileAccessMode AccessMode
    {
        get => _service.AccessMode;
        set
        {
            if (_service.AccessMode == value) return;
            _service.AccessMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanExecute));
            NotifyCommands();
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            NotifyCommands();
        }
    }
    public bool CanEdit => !IsBusy && _plan is not null;
    public bool CanExecute => AccessMode == FileAccessMode.ManageFiles && !IsBusy && _plan?.IsValid == true;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ErrorSummary { get => _errorSummary; private set => SetProperty(ref _errorSummary, value); }
    public bool HasErrors => ErrorSummary.Length > 0;
    public string Prefix { get => _prefix; set => SetOption(ref _prefix, value ?? string.Empty); }
    public string Suffix { get => _suffix; set => SetOption(ref _suffix, value ?? string.Empty); }
    public string SearchText { get => _searchText; set => SetOption(ref _searchText, value ?? string.Empty); }
    public string ReplacementText { get => _replacementText; set => SetOption(ref _replacementText, value ?? string.Empty); }
    public bool UseRegex { get => _useRegex; set => SetOption(ref _useRegex, value); }
    public bool MatchCase { get => _matchCase; set => SetOption(ref _matchCase, value); }
    public MultiRenameCaseMode CaseMode { get => _caseMode; set => SetOption(ref _caseMode, value); }
    public bool IncludeCounter { get => _includeCounter; set => SetOption(ref _includeCounter, value); }
    public int CounterStart { get => _counterStart; set => SetOption(ref _counterStart, value); }
    public int CounterStep { get => _counterStep; set => SetOption(ref _counterStep, value); }
    public int CounterPadding { get => _counterPadding; set => SetOption(ref _counterPadding, value); }
    public string CounterSeparator { get => _counterSeparator; set => SetOption(ref _counterSeparator, value ?? string.Empty); }
    public string DateFormat { get => _dateFormat; set => SetOption(ref _dateFormat, value ?? string.Empty); }
    public bool PreserveExtension { get => _preserveExtension; set => SetOption(ref _preserveExtension, value); }
    public string ExtensionReplacement { get => _extensionReplacement; set => SetOption(ref _extensionReplacement, value ?? string.Empty); }
    public MultiRenameSortMode SortMode { get => _sortMode; set => SetOption(ref _sortMode, value); }
    public bool SortDescending { get => _sortDescending; set => SetOption(ref _sortDescending, value); }
    public FileSystemCaseMode CaseSensitivity { get => _caseSensitivity; set => SetOption(ref _caseSensitivity, value); }

    public void Open(IReadOnlyList<string> sources)
    {
        _sources = sources;
        _manualNames.Clear();
        Status = string.Empty;
        RefreshPreview();
        IsOpen = true;
    }

    private void SetOption<T>(ref T field, T value)
    {
        if (!SetProperty(ref field, value)) return;
        if (IsOpen) RefreshPreview();
    }

    private MultiRenameRequest BuildRequest() => new()
    {
        SourcePaths = _sources,
        Prefix = Prefix,
        Suffix = Suffix,
        SearchText = SearchText,
        ReplacementText = ReplacementText,
        UseRegex = UseRegex,
        MatchCase = MatchCase,
        CaseMode = CaseMode,
        IncludeCounter = IncludeCounter,
        CounterStart = CounterStart,
        CounterStep = CounterStep,
        CounterPadding = CounterPadding,
        CounterSeparator = CounterSeparator,
        DateFormat = DateFormat,
        PreserveExtension = PreserveExtension,
        ExtensionReplacement = ExtensionReplacement,
        SortMode = SortMode,
        SortDescending = SortDescending,
        CaseSensitivity = CaseSensitivity,
        ManualNames = new Dictionary<string, string>(_manualNames)
    };

    private void RefreshPreview()
    {
        _plan = _service.CreatePlan(BuildRequest());
        Rows.Clear();
        foreach (var item in _plan.Items) Rows.Add(new MultiRenamePreviewRow(item, OnManualNameChanged));
        UpdateValidationPresentation();
    }

    private void OnManualNameChanged(MultiRenamePreviewRow row)
    {
        _manualNames[row.SourcePath] = row.ProposedName;
        _plan = _service.CreatePlan(BuildRequest());
        foreach (var current in Rows)
        {
            var item = _plan.Items.FirstOrDefault(item => string.Equals(
                item.SourcePath, current.SourcePath, StringComparison.Ordinal));
            if (item is not null) current.ApplyValidation(item);
        }
        UpdateValidationPresentation();
    }

    private void UpdateValidationPresentation()
    {
        var errors = (_plan?.Errors ?? []).Concat(_plan?.Items.SelectMany(item => item.Errors) ?? [])
            .Distinct(StringComparer.Ordinal).ToArray();
        ErrorSummary = string.Join(" · ", errors);
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(CanExecute));
        Status = _plan is null
            ? string.Empty
            : _localization.Format("MultiRenamePreviewSummary", _plan.Items.Count,
                _plan.Items.Count(item => item.HasChange));
        NotifyCommands();
    }

    private async Task ExecuteAsync()
    {
        if (_plan is null || !_plan.IsValid) return;
        if (!await _desktop.ConfirmAsync(
                _localization["MultiRenameConfirmTitle"],
                _localization.Format("MultiRenameConfirmMessage", _plan.Items.Count(item => item.HasChange)),
                _localization["MultiRenameExecute"])) return;
        var operation = new CancellationTokenSource();
        _operationCancellation = operation;
        IsBusy = true;
        try
        {
            var progress = new Progress<FileOperationProgress>(value => Status =
                _localization.Format("MultiRenameProgress", value.BytesCompleted, value.TotalBytes ?? 0));
            var result = await _service.ExecuteAsync(_plan, progress, operation.Token);
            Status = _localization.Format("MultiRenameCompleted", result.RenamedItems);
            _plan = null;
            OnPropertyChanged(nameof(CanExecute));
            OnPropertyChanged(nameof(CanEdit));
            Completed?.Invoke(this, EventArgs.Empty);
            NotifyCommands();
        }
        catch (OperationCanceledException) { Status = _localization["MultiRenameCancelled"]; }
        catch (Exception exception) { ErrorSummary = exception.Message; OnPropertyChanged(nameof(HasErrors)); }
        finally
        {
            _operationCancellation = null;
            operation.Dispose();
            IsBusy = false;
        }
    }

    private async Task UndoAsync()
    {
        var operation = new CancellationTokenSource();
        _operationCancellation = operation;
        IsBusy = true;
        try
        {
            var result = await _service.UndoLastBatchAsync(cancellationToken: operation.Token);
            Completed?.Invoke(this, EventArgs.Empty);
            RefreshPreview();
            Status = result is null
                ? _localization["MultiRenameNothingToUndo"]
                : _localization.Format("MultiRenameUndoCompleted", result.RenamedItems);
        }
        catch (OperationCanceledException) { Status = _localization["MultiRenameCancelled"]; }
        catch (Exception exception) { ErrorSummary = exception.Message; OnPropertyChanged(nameof(HasErrors)); }
        finally
        {
            _operationCancellation = null;
            operation.Dispose();
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        if (_plan is null) return;
        await _desktop.CopyTextAsync(_service.ExportPlan(_plan));
        Status = _localization["MultiRenameExported"];
    }

    private async Task ImportAsync()
    {
        var json = await _desktop.PromptMultilineAsync(
            _localization["MultiRenameImportTitle"], _localization["MultiRenameImportPrompt"]);
        if (json is null) return;
        try
        {
            var imported = _service.ImportPlan(json);
            ApplyRequest(imported.Request);
            Status = _localization["MultiRenameImported"];
        }
        catch (Exception exception) { ErrorSummary = exception.Message; OnPropertyChanged(nameof(HasErrors)); }
    }

    private void ApplyRequest(MultiRenameRequest request)
    {
        _sources = request.SourcePaths;
        _prefix = request.Prefix; _suffix = request.Suffix;
        _searchText = request.SearchText; _replacementText = request.ReplacementText;
        _useRegex = request.UseRegex; _matchCase = request.MatchCase; _caseMode = request.CaseMode;
        _includeCounter = request.IncludeCounter; _counterStart = request.CounterStart;
        _counterStep = request.CounterStep; _counterPadding = request.CounterPadding;
        _counterSeparator = request.CounterSeparator; _dateFormat = request.DateFormat;
        _preserveExtension = request.PreserveExtension; _extensionReplacement = request.ExtensionReplacement;
        _sortMode = request.SortMode; _sortDescending = request.SortDescending;
        _caseSensitivity = request.CaseSensitivity;
        _manualNames.Clear();
        foreach (var item in request.ManualNames) _manualNames[item.Key] = item.Value;
        OnPropertyChanged(string.Empty);
        RefreshPreview();
    }

    private void NotifyCommands()
    {
        ExecuteCommand.NotifyCanExecuteChanged(); UndoCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged(); ImportCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        CloseCommand.NotifyCanExecuteChanged();
    }
}
