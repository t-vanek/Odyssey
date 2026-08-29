using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Odyssey.Agent;

namespace Odyssey.Desktop;

public sealed class AgentApprovalViewModel : ObservableObject
{
    private readonly AgentOperationService _operations;
    private readonly AgentAccessPolicyService _policy;
    private readonly IDesktopInteractionService _desktop;
    private readonly LocalizationService _localization;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private AgentApprovalRow? _selected;
    private bool _isBusy;
    private string _status = string.Empty;

    public AgentApprovalViewModel(
        AgentOperationService operations,
        AgentAccessPolicyService policy,
        IDesktopInteractionService desktop,
        LocalizationService localization)
    {
        _operations = operations;
        _policy = policy;
        _desktop = desktop;
        _localization = localization;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += async (_, _) => await RefreshAsync(silent: true);
        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(silent: false), () => !IsBusy);
        ApproveCommand = new AsyncRelayCommand(ApproveAsync, () => Selected is not null && !IsBusy);
        DenyCommand = new AsyncRelayCommand(DenyAsync, () => Selected is not null && !IsBusy);
        UseReadOnlyCommand = new AsyncRelayCommand(() => SetAccessAsync(AgentAccessLevel.ReadOnly), () => !IsBusy);
        UseFileManagementCommand = new AsyncRelayCommand(() => SetAccessAsync(AgentAccessLevel.FileManagement), () => !IsBusy);
        UseFullAccessCommand = new AsyncRelayCommand(() => SetAccessAsync(AgentAccessLevel.FullAccess), () => !IsBusy);
        _localization.LanguageChanged += (_, _) => RaiseLocalized();
        _policy.Changed += (_, _) => RaiseLocalized();
    }

    public ObservableCollection<AgentApprovalRow> Pending { get; } = [];
    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand ApproveCommand { get; }
    public IAsyncRelayCommand DenyCommand { get; }
    public IAsyncRelayCommand UseReadOnlyCommand { get; }
    public IAsyncRelayCommand UseFileManagementCommand { get; }
    public IAsyncRelayCommand UseFullAccessCommand { get; }
    public bool HasPending => Pending.Count > 0;
    public bool HasNoPending => !HasPending;
    public string AccessDisplay => _localization[$"AgentAccess{_policy.AccessLevel}"];
    public string PendingSummary => _localization.Format("AgentPendingSummary", Pending.Count);
    public string Status => string.IsNullOrWhiteSpace(_status) ? _localization["AgentApprovalIdle"] : _status;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) NotifyCommands(); } }
    public AgentApprovalRow? Selected
    {
        get => _selected;
        set { if (SetProperty(ref _selected, value)) NotifyCommands(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _operations.InitializeAsync(cancellationToken);
        await RefreshAsync(silent: true);
    }

    public void BeginPolling()
    {
        if (!_timer.IsEnabled) _timer.Start();
    }

    private async Task RefreshAsync(bool silent)
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        if (!silent) IsBusy = true;
        try
        {
            var selectedId = Selected?.PlanId;
            var items = await _operations.GetPendingAsync();
            Pending.Clear();
            foreach (var item in items) Pending.Add(new AgentApprovalRow(item, _localization));
            Selected = Pending.FirstOrDefault(item => item.PlanId == selectedId) ?? Pending.FirstOrDefault();
            _status = string.Empty;
            RaiseCollectionState();
        }
        catch (Exception ex)
        {
            _status = _localization.Format("AgentApprovalFailed", ex.Message);
            OnPropertyChanged(nameof(Status));
        }
        finally
        {
            if (!silent) IsBusy = false;
            _refreshGate.Release();
        }
    }

    private async Task ApproveAsync()
    {
        if (Selected is null) return;
        var selected = Selected;
        var detail = _localization.Format(
            selected.RequiresStrongApproval ? "AgentStrongApprovalMessage" : "AgentApprovalMessage",
            selected.ClientId, selected.Summary, selected.Risk, selected.EstimatedItems,
            FormatBytes(selected.EstimatedBytes), selected.ReviewDetail);
        if (!await _desktop.ConfirmAsync(_localization["AgentApprovalDialogTitle"], detail, _localization["AgentApproveOnce"])) return;
        IsBusy = true;
        try
        {
            var approved = await _operations.ApproveAsync(selected.PlanId, strongApproval: true);
            _status = approved ? _localization["AgentApprovedStatus"] : _localization["AgentApprovalChangedStatus"];
            await RefreshAsync(silent: true);
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(Status)); }
    }

    private async Task DenyAsync()
    {
        if (Selected is null) return;
        IsBusy = true;
        try
        {
            await _operations.DenyAsync(Selected.PlanId, _localization["AgentDeniedByUser"]);
            _status = _localization["AgentDeniedStatus"];
            await RefreshAsync(silent: true);
        }
        finally { IsBusy = false; OnPropertyChanged(nameof(Status)); }
    }

    private async Task SetAccessAsync(AgentAccessLevel level)
    {
        if (_policy.AccessLevel == level) return;
        if (level != AgentAccessLevel.ReadOnly)
        {
            var confirmed = await _desktop.ConfirmAsync(
                _localization["AgentAccessDialogTitle"],
                _localization[level == AgentAccessLevel.FullAccess ? "AgentFullAccessWarning" : "AgentFileManagementWarning"],
                _localization["Confirm"]);
            if (!confirmed) return;
        }
        _policy.AccessLevel = level;
        _status = _localization["AgentAccessChanged"];
        RaiseLocalized();
    }

    private void RaiseCollectionState()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(HasNoPending));
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(Status));
    }

    private void RaiseLocalized()
    {
        foreach (var item in Pending) item.RaiseLocalized();
        OnPropertyChanged(nameof(AccessDisplay));
        OnPropertyChanged(nameof(PendingSummary));
        OnPropertyChanged(nameof(Status));
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        ApproveCommand.NotifyCanExecuteChanged();
        DenyCommand.NotifyCanExecuteChanged();
        UseReadOnlyCommand.NotifyCanExecuteChanged();
        UseFileManagementCommand.NotifyCanExecuteChanged();
        UseFullAccessCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes); var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}

public sealed class AgentApprovalRow(AgentOperationSnapshot snapshot, LocalizationService localization) : ObservableObject
{
    public Guid PlanId => snapshot.Plan.Id;
    public string ClientId => snapshot.Plan.Request.ClientId;
    public string Summary => snapshot.Plan.Request.Kind switch
    {
        AgentOperationKind.CreateDirectory => localization.Format("AgentPlanCreateFolder", snapshot.Plan.Request.Name!, snapshot.Plan.Request.DestinationPath!),
        AgentOperationKind.Copy => localization.Format("AgentPlanCopy", snapshot.Plan.Request.SourcePath!, snapshot.Plan.Request.DestinationPath!),
        AgentOperationKind.Move => localization.Format("AgentPlanMove", snapshot.Plan.Request.SourcePath!, snapshot.Plan.Request.DestinationPath!),
        AgentOperationKind.Rename => localization.Format("AgentPlanRename", snapshot.Plan.Request.SourcePath!, snapshot.Plan.Request.Name!),
        AgentOperationKind.Trash => localization.Format("AgentPlanTrash", snapshot.Plan.Request.SourcePath!),
        AgentOperationKind.WriteText => localization.Format("AgentPlanWriteText", snapshot.Plan.Request.DestinationPath!),
        _ => snapshot.Plan.Summary
    };
    public string ExactTarget => snapshot.Plan.Request.DestinationPath ?? snapshot.Plan.Request.SourcePath ?? string.Empty;
    public string Risk => localization[$"AgentRisk{snapshot.Plan.Risk}"];
    public int EstimatedItems => snapshot.Plan.EstimatedItems;
    public long EstimatedBytes => snapshot.Plan.EstimatedBytes;
    public bool RequiresStrongApproval => snapshot.Plan.RequiresStrongApproval;
    public string ApprovalKind => localization[RequiresStrongApproval ? "AgentStrongApproval" : "AgentStandardApproval"];
    public string GuardSummary => string.Join(" · ", snapshot.Plan.Guards
        .Where(item => item.Decision != AgentGuardDecision.Allow)
        .Select(item => $"{item.Guard}: {localization[$"AgentGuard{item.Decision}"]}"));
    public string ReviewDetail
    {
        get
        {
            var request = snapshot.Plan.Request;
            var paths = localization.Format(
                "AgentReviewPaths",
                request.SourcePath ?? "—",
                request.DestinationPath ?? "—",
                request.Name ?? "—");
            if (request.Content is null) return paths;
            var bytes = Encoding.UTF8.GetBytes(request.Content);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var escaped = request.Content.Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);
            var preview = escaped.Length <= 240 ? escaped : escaped[..240] + "…";
            return paths + "\n" + localization.Format("AgentReviewContent", bytes.Length, hash, preview);
        }
    }
    public void RaiseLocalized()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Risk));
        OnPropertyChanged(nameof(ApprovalKind));
        OnPropertyChanged(nameof(GuardSummary));
        OnPropertyChanged(nameof(ReviewDetail));
    }
}
