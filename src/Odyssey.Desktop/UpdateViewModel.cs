using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Odyssey.Desktop;

public sealed class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private readonly LocalizationService _localization;
    private UpdateUiState _state;
    private bool _isBusy;
    private Version? _availableVersion;
    private string? _lastError;
    private bool _automaticCheckStarted;

    public UpdateViewModel(UpdateService updates, LocalizationService localization)
    {
        _updates = updates;
        _localization = localization;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !IsBusy);
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => IsReady && !IsBusy);
        _localization.LanguageChanged += (_, _) => RaiseLocalizedProperties();
    }

    public event EventHandler? RestartRequested;
    public IAsyncRelayCommand CheckCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) NotifyCommands(); } }
    public bool IsReady => _state == UpdateUiState.Ready;
    public bool CanInstallAutomatically => _updates.CanInstall;
    public string CurrentVersion => _localization.Format("UpdateCurrentVersion", _updates.CurrentVersionText);
    public string StatusTitle => _localization[StateTitleKey];
    public string StatusDetail => _state switch
    {
        UpdateUiState.Ready => _localization.Format("UpdateReadyDetail", FormatVersion(_availableVersion)),
        UpdateUiState.Failed => _localization.Format("UpdateFailedDetail", _lastError ?? _localization["UnknownError"]),
        UpdateUiState.Unsupported => _localization["UpdateUnsupportedDetail"],
        _ => _localization[StateDetailKey]
    };
    public string InstallButtonText => _localization["UpdateRestartButton"];

    public void BeginAutomaticCheck()
    {
        if (_automaticCheckStarted || !_updates.CanInstall || Environment.GetEnvironmentVariable("ODYSSEY_DISABLE_UPDATE_CHECK") == "1") return;
        _automaticCheckStarted = true;
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        IsBusy = true;
        SetState(UpdateUiState.Checking);
        try
        {
            var result = await _updates.CheckAndPrepareAsync();
            _availableVersion = result.Update?.Version;
            SetState(result.Status switch
            {
                UpdateCheckStatus.Ready => UpdateUiState.Ready,
                UpdateCheckStatus.Unsupported => UpdateUiState.Unsupported,
                _ => UpdateUiState.UpToDate
            });
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            SetState(UpdateUiState.Failed);
        }
        finally { IsBusy = false; }
    }

    private async Task InstallAsync()
    {
        IsBusy = true;
        SetState(UpdateUiState.Installing);
        try
        {
            await _updates.LaunchPreparedInstallerAsync();
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            SetState(UpdateUiState.Failed);
            IsBusy = false;
        }
    }

    private string StateTitleKey => _state switch
    {
        UpdateUiState.Checking => "UpdateCheckingTitle",
        UpdateUiState.UpToDate => "UpdateCurrentTitle",
        UpdateUiState.Ready => "UpdateReadyTitle",
        UpdateUiState.Installing => "UpdateInstallingTitle",
        UpdateUiState.Failed => "UpdateFailedTitle",
        UpdateUiState.Unsupported => "UpdateUnsupportedTitle",
        _ => "UpdateIdleTitle"
    };

    private string StateDetailKey => _state switch
    {
        UpdateUiState.Checking => "UpdateCheckingDetail",
        UpdateUiState.UpToDate => "UpdateCurrentDetail",
        UpdateUiState.Installing => "UpdateInstallingDetail",
        _ => _updates.CanInstall ? "UpdateIdleDetail" : "UpdateDevelopmentDetail"
    };

    private void SetState(UpdateUiState state)
    {
        _state = state;
        OnPropertyChanged(nameof(IsReady));
        RaiseLocalizedProperties();
        NotifyCommands();
    }

    private void RaiseLocalizedProperties()
    {
        OnPropertyChanged(nameof(CurrentVersion));
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(InstallButtonText));
    }

    private void NotifyCommands()
    {
        CheckCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    private static string FormatVersion(Version? version) => version is null ? "—" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    private enum UpdateUiState { Idle, Checking, UpToDate, Ready, Installing, Failed, Unsupported }
}
