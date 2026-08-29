using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;
using System.IO.Compression;

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
        var initialQuote = viewModel.RescueQuoteText;
        viewModel.AdvanceRescueTip();
        Assert.NotEqual(initialTip, viewModel.RescueTipText);
        Assert.NotEqual(initialQuote, viewModel.RescueQuoteText);
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

    [Fact]
    public async Task CommanderSelectionCommand_UsesOnlyTheActivePane()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var leftRoot = Path.Combine(environment.Root, "left");
        var rightRoot = Path.Combine(environment.Root, "right");
        Directory.CreateDirectory(leftRoot);
        Directory.CreateDirectory(rightRoot);
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "left-one.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "left-two.txt"), "2");
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "right-one.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(rightRoot, "right-two.txt"), "2");
        var coordinator = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store,
            new ScanPipelineOptions(2, 16, 4), NullLogger<ScanCoordinator>.Instance);
        var viewModel = new MainViewModel(
            environment.Store, coordinator, environment.Search,
            new EmptySystemSearchHistory(), new UnusedDesktopInteraction(),
            new LocalizationService(environment.Storage),
            new FixedVolumeDiscovery(new StorageVolume(leftRoot, "Test", false)),
            new CachedDirectoryBrowserService(), new UnusedDiskManagement(),
            new SafeFileOperationService(environment.Storage),
            new UserPreferencesService(environment.Storage),
            new NullBackgroundAutomationService(), new DisabledOcrCapability());
        viewModel.ShowPageCommand.Execute("Files");
        viewModel.LeftPane.SelectedTarget = Target(leftRoot);
        viewModel.RightPane.SelectedTarget = Target(rightRoot);
        Assert.True(await TestWait.UntilAsync(
            () => !viewModel.LeftPane.IsLoading && !viewModel.RightPane.IsLoading,
            TimeSpan.FromSeconds(3)));
        viewModel.LeftPane.SetSelection([viewModel.LeftPane.Entries[0]]);
        viewModel.RightPane.SetSelection([viewModel.RightPane.Entries[0]]);

        viewModel.RightPane.Activate();
        viewModel.SelectAllCommand.Execute(null);

        Assert.Same(viewModel.RightPane, viewModel.ActivePane);
        Assert.Equal(2, viewModel.RightPane.SelectedEntries.Count);
        Assert.Single(viewModel.LeftPane.SelectedEntries);
    }

    [Fact]
    public async Task CommanderCopy_RoutesOnlyActiveLocalSelectionIntoPassiveZipTab()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var leftRoot = Path.Combine(environment.Root, "left");
        var rightRoot = Path.Combine(environment.Root, "right");
        Directory.CreateDirectory(leftRoot);
        Directory.CreateDirectory(rightRoot);
        var source = Path.Combine(leftRoot, "selected.txt");
        await File.WriteAllTextAsync(source, "selected active pane data");
        await File.WriteAllTextAsync(Path.Combine(leftRoot, "not-selected.txt"), "must stay outside");
        var archivePath = Path.Combine(rightRoot, "target.zip");
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("existing.txt").Open());
            writer.Write("existing");
        }
        var preferences = new UserPreferencesService(environment.Storage) { ReadOnlyMode = false };
        var operations = new SafeFileOperationService(environment.Storage);
        var archives = new SafeArchiveService();
        await using var queue = new FileTransferQueueService(
            operations, environment.Storage, archives: archives, archiveMutations: archives);
        await queue.InitializeAsync();
        var coordinator = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store,
            new ScanPipelineOptions(2, 16, 4), NullLogger<ScanCoordinator>.Instance);
        var viewModel = new MainViewModel(
            environment.Store, coordinator, environment.Search,
            new EmptySystemSearchHistory(), new UnusedDesktopInteraction(),
            new LocalizationService(environment.Storage),
            new FixedVolumeDiscovery(new StorageVolume(leftRoot, "Test", false)),
            new CachedDirectoryBrowserService(), new UnusedDiskManagement(),
            operations, preferences, new NullBackgroundAutomationService(), new DisabledOcrCapability(),
            transferQueue: queue, archives: archives);
        viewModel.ShowPageCommand.Execute("Files");
        viewModel.LeftPane.SelectedTarget = Target(leftRoot);
        viewModel.RightPane.SelectedTarget = Target(rightRoot);
        Assert.True(await TestWait.UntilAsync(
            () => !viewModel.LeftPane.IsLoading && !viewModel.RightPane.IsLoading,
            TimeSpan.FromSeconds(3)));
        viewModel.RightPane.OpenArchive(archivePath);
        Assert.True(await TestWait.UntilAsync(
            () => !viewModel.RightPane.IsLoading, TimeSpan.FromSeconds(3)));
        viewModel.LeftPane.SetSelection([
            viewModel.LeftPane.Entries.Single(item => item.Name == "selected.txt")
        ]);
        viewModel.LeftPane.Activate();

        await viewModel.CopyEntryCommand.ExecuteAsync(null);
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout && queue.Items.Single().State != FileTransferState.Completed)
            await Task.Delay(20);

        Assert.Equal(FileTransferState.Completed, queue.Items.Single().State);
        Assert.Contains(await archives.ListAsync(archivePath), item => item.Name == "selected.txt");
        Assert.DoesNotContain(await archives.ListAsync(archivePath), item => item.Name == "not-selected.txt");
        viewModel.CancelActiveWork();
    }

    [Fact]
    public async Task CommanderCreateArchive_PreviewsNameAndCreatesOneValidatedZipForSelection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var root = Path.Combine(environment.Root, "files");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(root, "two.txt"), "two");
        var preferences = new UserPreferencesService(environment.Storage) { ReadOnlyMode = false };
        var operations = new SafeFileOperationService(environment.Storage);
        var archives = new SafeArchiveService();
        var coordinator = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store,
            new ScanPipelineOptions(2, 16, 4), NullLogger<ScanCoordinator>.Instance);
        var desktop = new ArchiveCreationDesktopInteraction("bundle", confirm: true);
        var viewModel = new MainViewModel(
            environment.Store, coordinator, environment.Search,
            new EmptySystemSearchHistory(), desktop,
            new LocalizationService(environment.Storage),
            new FixedVolumeDiscovery(new StorageVolume(root, "Test", false)),
            new CachedDirectoryBrowserService(), new UnusedDiskManagement(), operations, preferences,
            new NullBackgroundAutomationService(), new DisabledOcrCapability(), archives: archives);
        viewModel.ShowPageCommand.Execute("Files");
        viewModel.LeftPane.SelectedTarget = Target(root);
        Assert.True(await TestWait.UntilAsync(
            () => !viewModel.LeftPane.IsLoading, TimeSpan.FromSeconds(3)));
        viewModel.LeftPane.SetSelection(viewModel.LeftPane.Entries.Where(item => item.Type == FileEntryType.File));
        viewModel.LeftPane.Activate();

        await viewModel.CreateArchiveCommand.ExecuteAsync(null);

        var archivePath = Path.Combine(root, "bundle.zip");
        Assert.True(File.Exists(archivePath));
        Assert.Equal(["one.txt", "two.txt"],
            (await archives.ListAsync(archivePath)).Select(item => item.Name).Order().ToArray());
        Assert.Contains("bundle.zip", desktop.ConfirmationMessage, StringComparison.Ordinal);

        viewModel.LeftPane.OpenArchive(archivePath);
        Assert.True(await TestWait.UntilAsync(
            () => !viewModel.LeftPane.IsLoading, TimeSpan.FromSeconds(3)));
        viewModel.LeftPane.SetSelection([
            viewModel.LeftPane.Entries.Single(item => item.Name == "one.txt")
        ]);
        await viewModel.TrashEntryCommand.ExecuteAsync(null);
        Assert.True(await TestWait.UntilAsync(
            () => !viewModel.LeftPane.IsLoading, TimeSpan.FromSeconds(3)));

        Assert.Equal("two.txt", Assert.Single(await archives.ListAsync(archivePath)).Name);
        Assert.Contains("bundle.zip", desktop.ConfirmationMessage, StringComparison.Ordinal);
        viewModel.CancelActiveWork();
    }

    [Fact]
    public async Task Startup_RestoresIndependentCommanderWorkspacesAndHotlist()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var root = Path.Combine(environment.Root, "indexed");
        var nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        var session = await environment.Store.SaveSessionAsync(new RescueSession
        {
            Id = Guid.NewGuid(), Name = "Workspace", Mode = SessionMode.ForensicReadOnly,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var target = await environment.Store.SaveTargetAsync(new ScanTarget
        {
            Id = Guid.NewGuid(), SessionId = session.Id, RootPath = root
        });
        var preferences = new UserPreferencesService(environment.Storage) { AutomaticDrives = false };
        var leftId = Guid.NewGuid();
        preferences.SaveCommanderWorkspace(
            new FilePaneWorkspaceSnapshot(
                [new FilePaneTabSnapshot(leftId, nested, root, "*.txt", FileNameFilterMode.Glob,
                    false, [root], [])], leftId),
            new FilePaneWorkspaceSnapshot(
                [new FilePaneTabSnapshot(Guid.NewGuid(), root, root, string.Empty,
                    FileNameFilterMode.Contains, false, [], [])], null),
            [nested]);
        var coordinator = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store,
            new ScanPipelineOptions(2, 16, 4), NullLogger<ScanCoordinator>.Instance);
        var viewModel = new MainViewModel(
            environment.Store, coordinator, environment.Search,
            new EmptySystemSearchHistory(), new UnusedDesktopInteraction(),
            new LocalizationService(environment.Storage),
            new FixedVolumeDiscovery(new StorageVolume(root, "Test", false)),
            new CachedDirectoryBrowserService(), new UnusedDiskManagement(),
            new SafeFileOperationService(environment.Storage), preferences,
            new NullBackgroundAutomationService(), new DisabledOcrCapability());

        await viewModel.InitializeAsync();

        Assert.Equal(target.Id, viewModel.LeftPane.SelectedTarget?.Id);
        Assert.Equal(Path.GetFullPath(nested), viewModel.LeftPane.CurrentPath);
        Assert.Equal("*.txt", viewModel.LeftPane.FilterText);
        Assert.Equal(leftId, viewModel.LeftPane.SelectedTab?.Id);
        Assert.Equal(Path.GetFullPath(root), viewModel.RightPane.CurrentPath);
        Assert.Equal(Path.GetFullPath(nested), Assert.Single(viewModel.Hotlist));
        Assert.True(viewModel.LeftPane.CanGoBack);
        viewModel.LeftPane.GoBack();
        Assert.True(await TestWait.UntilAsync(
            () => !viewModel.LeftPane.IsLoading, TimeSpan.FromSeconds(3)));
        Assert.Equal(Path.GetFullPath(root), viewModel.LeftPane.CurrentPath);
        viewModel.CancelActiveWork();
    }

    private static ScanTarget Target(string path) => new()
    {
        Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), RootPath = path
    };

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
        public Task EditAsync(string path) => Task.CompletedTask;
        public Task OpenContainingFolderAsync(string path) => Task.CompletedTask;
    }

    private sealed class ArchiveCreationDesktopInteraction(string name, bool confirm) : IDesktopInteractionService
    {
        public string ConfirmationMessage { get; private set; } = string.Empty;
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickDestinationFolderAsync() => Task.FromResult<string?>(null);
        public Task<string?> PromptTextAsync(string title, string message, string initialValue = "") =>
            Task.FromResult<string?>(name);
        public Task<bool> ConfirmAsync(string title, string message, string confirmText)
        {
            ConfirmationMessage = message;
            return Task.FromResult(confirm);
        }
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task OpenAsync(string path) => Task.CompletedTask;
        public Task EditAsync(string path) => Task.CompletedTask;
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
