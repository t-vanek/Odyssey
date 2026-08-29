using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class AutomaticDriveTests
{
    [Fact]
    public async Task DetectedDrive_IsAddedScannedAndSearchableAutomatically()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var drive = Path.Combine(environment.Root, "connected-drive");
        Directory.CreateDirectory(drive);
        await File.WriteAllTextAsync(Path.Combine(drive, "Girlanda_Vanoce_2024.txt"), "read-only source");

        var coordinator = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store,
            new ScanPipelineOptions(2, 16, 4), NullLogger<ScanCoordinator>.Instance);
        var localization = new LocalizationService(environment.Storage);
        var viewModel = new MainViewModel(
            environment.Store, coordinator, environment.Search,
            new EmptySystemSearchHistory(),
            new UnusedDesktopInteraction(), localization,
            new FixedVolumeDiscovery(new StorageVolume(drive, "Test drive", true)),
            new CachedDirectoryBrowserService(),
            new UnusedDiskManagement(),
            new SafeFileOperationService(environment.Storage),
            new UserPreferencesService(environment.Storage),
            new NullBackgroundAutomationService(),
            new DisabledOcrCapability());

        var startupStatuses = new List<string>();
        await viewModel.InitializeAsync(startupStatuses.Add);
        Assert.Equal(localization["SplashOpeningIndex"], startupStatuses[0]);
        Assert.Contains(localization["SplashLoadingWorkspace"], startupStatuses);
        Assert.Contains(localization["SplashWarmingSearch"], startupStatuses);
        Assert.Contains(localization["SplashPreparingFolders"], startupStatuses);
        Assert.Contains(localization["SplashStartingServices"], startupStatuses);
        Assert.Equal(localization["SplashReady"], startupStatuses[^1]);
        Assert.True(viewModel.IsRescuePage);
        var initialTip = viewModel.RescueTipText;
        viewModel.AdvanceRescueTip();
        Assert.NotEqual(initialTip, viewModel.RescueTipText);
        Assert.Equal(localization["StartSearching"], viewModel.RescueSearchButtonText);
        viewModel.SearchText = "Girlanda";
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !viewModel.SearchResults.Any(result => result.Name.Contains("Girlanda", StringComparison.Ordinal)))
            await Task.Delay(25);

        Assert.Single(viewModel.Targets);
        Assert.NotNull(viewModel.LeftPane.SelectedTarget);
        Assert.Equal(Path.GetFullPath(drive), viewModel.LeftPane.CurrentPath);
        Assert.True(viewModel.IsRescuePage);
        Assert.Contains(viewModel.SearchResults, result => result.Name == "Girlanda_Vanoce_2024.txt");
        Assert.True(viewModel.ShowRescueResults);
        viewModel.CancelActiveWork();
    }

    [Fact]
    public async Task PlatformDiscovery_NeverReturnsSystemRoot()
    {
        var volumes = await new PortableStorageVolumeDiscovery().DiscoverAsync();
        Assert.DoesNotContain(volumes, volume => volume.RootPath == Path.GetPathRoot(Environment.SystemDirectory));
        if (!OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain(volumes, volume => volume.RootPath is "/" or "/home" or "/boot" or "/boot/efi");
        }
    }

    private sealed class FixedVolumeDiscovery(StorageVolume volume) : IStorageVolumeDiscovery
    {
        public Task<IReadOnlyList<StorageVolume>> DiscoverAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StorageVolume>>([volume]);
    }

    private sealed class UnusedDesktopInteraction : IDesktopInteractionService
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickDestinationFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PromptTextAsync(string title, string message, string initialValue = "") => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task OpenAsync(string path) => Task.CompletedTask;
        public Task OpenContainingFolderAsync(string path) => Task.CompletedTask;
    }

    private sealed class UnusedDiskManagement : IDiskManagementService
    {
        public Task UnmountAsync(StorageVolume volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EjectAsync(StorageVolume volume, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptySystemSearchHistory : ISystemSearchHistoryService
    {
        public Task WarmupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReadOnlyList<string> Suggest(string query, int limit) => [];
    }
}
