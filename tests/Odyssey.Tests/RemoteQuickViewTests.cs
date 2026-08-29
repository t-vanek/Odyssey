using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class RemoteQuickViewTests
{
    [Fact]
    public async Task SftpSource_IsReadInBoundedBlocksAndRejectsAChangedVersion()
    {
        var remote = new FakeRemotePreviewReader("/large.txt",
            new string('a', 1023) + "€" + new string('b', 4096));
        var service = new QuickViewService(remote);

        var first = await service.ReadAsync(Request("session", "/large.txt") with
        {
            MaximumBytes = 1024
        });
        var second = await service.ReadAsync(Request("session", "/large.txt") with
        {
            Offset = first.NextOffset,
            MaximumBytes = 1024,
            ExpectedVersion = first.Version
        });

        Assert.Equal(1023, first.BytesRead);
        Assert.Equal(new string('a', 1023) + "€" + new string('b', 1021),
            first.Content + second.Content);
        Assert.All(remote.RequestedMaximumBytes, value => Assert.InRange(value, 1, 1024));

        remote.ChangeVersion();
        await Assert.ThrowsAsync<IOException>(() => service.ReadAsync(Request("session", "/large.txt") with
        {
            ExpectedVersion = first.Version
        }));
    }

    [Fact]
    public async Task SftpSource_CancellationPropagatesWithoutTemporaryArtifacts()
    {
        var remote = new FakeRemotePreviewReader("/slow.txt", "content") { BlockUntilCancelled = true };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new QuickViewService(remote).ReadAsync(Request("session", "/slow.txt"), cancellation.Token));
        Assert.Empty(remote.CreatedArtifacts);
    }

    [Fact]
    public async Task SftpBinarySource_UsesOffsetAddressedHexDisplay()
    {
        var remote = new FakeRemotePreviewReader("/binary.dat", "A\0B");

        var chunk = await new QuickViewService(remote).ReadAsync(Request("session", "/binary.dat"));

        Assert.Equal(QuickViewDisplayMode.Hex, chunk.EffectiveMode);
        Assert.Contains("0000000000000000", chunk.Content, StringComparison.Ordinal);
        Assert.Contains("41 00 42", chunk.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative.txt")]
    [InlineData("/folder/../escape.txt")]
    [InlineData("/folder\\file.txt")]
    public async Task SftpReader_RejectsNonCanonicalPathsBeforeResolvingASession(string path)
    {
        await using var service = new SftpConnectionService();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ReadBlockAsync("missing-session", path, 0, 1024));
    }

    [Fact]
    public async Task RemoteF3_UsesTheSelectedSftpFileInsteadOfTheLocalPaneSelection()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var localRoot = Path.Combine(environment.Root, "local");
        Directory.CreateDirectory(localRoot);
        await File.WriteAllTextAsync(Path.Combine(localRoot, "wrong.txt"), "wrong local panel");
        var remote = new FakeSftpService("/remote.txt", "selected remote data");
        var coordinator = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store,
            new ScanPipelineOptions(2, 16, 4), NullLogger<ScanCoordinator>.Instance);
        var viewModel = new MainViewModel(
            environment.Store, coordinator, environment.Search,
            new EmptySystemSearchHistory(), new UnusedDesktopInteraction(),
            new LocalizationService(environment.Storage),
            new FixedVolumeDiscovery(new StorageVolume(localRoot, "Test", false)),
            new CachedDirectoryBrowserService(), new UnusedDiskManagement(),
            new SafeFileOperationService(environment.Storage),
            new UserPreferencesService(environment.Storage),
            new NullBackgroundAutomationService(), new DisabledOcrCapability(),
            sftp: remote, quickView: new QuickViewService((IRemoteFilePreviewReader)remote));
        viewModel.ShowPageCommand.Execute("Files");
        viewModel.LeftPane.SelectedTarget = Target(localRoot);
        Assert.True(await TestWait.UntilAsync(
            () => !viewModel.LeftPane.IsLoading && viewModel.LeftPane.Entries.Count == 1,
            TimeSpan.FromSeconds(3)));
        viewModel.LeftPane.SetSelection([viewModel.LeftPane.Entries.Single()]);
        viewModel.SftpHost = "example.test";
        viewModel.SftpUsername = "tester";
        viewModel.SftpPassword = "session-secret";

        await viewModel.ConnectSftpCommand.ExecuteAsync(null);
        viewModel.ShowPageCommand.Execute("Remote");
        var row = Assert.Single(viewModel.RemoteEntries);
        viewModel.SetRemoteSelection([row]);
        await viewModel.OpenQuickViewCommand.ExecuteAsync(null);

        var preview = Assert.IsType<QuickViewViewModel>(viewModel.QuickView);
        Assert.True(preview.IsOpen);
        Assert.Equal("/remote.txt", preview.SourcePath);
        Assert.Equal("selected remote data", preview.Content);
        Assert.DoesNotContain("wrong local panel", preview.Content, StringComparison.Ordinal);
        Assert.Equal(string.Empty, viewModel.SftpPassword);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(environment.Storage.DirectoryPath, "*.json", SearchOption.AllDirectories),
            file => File.ReadAllText(file).Contains("session-secret", StringComparison.Ordinal));
        await viewModel.DisconnectSftpCommand.ExecuteAsync(null);
        Assert.False(preview.IsOpen);
        viewModel.CancelActiveWork();
    }

    private static QuickViewReadRequest Request(string connectionKey, string path) => new()
    {
        Path = path,
        Endpoint = FileTransferEndpointKind.Sftp,
        ConnectionKey = connectionKey,
        MaximumBytes = 1024
    };

    private static ScanTarget Target(string path) => new()
    {
        Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), RootPath = path
    };

    private sealed class FakeRemotePreviewReader(string path, string content) : IRemoteFilePreviewReader
    {
        private readonly byte[] _data = System.Text.Encoding.UTF8.GetBytes(content);
        private DateTimeOffset _modifiedAt = DateTimeOffset.UnixEpoch;
        public int MaximumBlockBytes => 256 * 1024;
        public bool BlockUntilCancelled { get; init; }
        public List<int> RequestedMaximumBytes { get; } = [];
        public List<string> CreatedArtifacts { get; } = [];

        public void ChangeVersion() => _modifiedAt = _modifiedAt.AddSeconds(1);

        public async Task<RemoteFilePreviewBlock> ReadBlockAsync(
            string connectionKey,
            string requestedPath,
            long offset,
            int maximumBytes,
            RemoteFilePreviewVersion? expectedVersion = null,
            CancellationToken cancellationToken = default)
        {
            RequestedMaximumBytes.Add(maximumBytes);
            if (BlockUntilCancelled)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(path, requestedPath);
            var version = new RemoteFilePreviewVersion(_data.Length, _modifiedAt);
            if (expectedVersion is not null && expectedVersion != version)
                throw new IOException("changed");
            var actualOffset = Math.Min(offset, _data.Length);
            var header = _data[..Math.Min(4096, _data.Length)];
            var length = Math.Min(maximumBytes, _data.Length - (int)actualOffset);
            var block = _data.AsSpan((int)actualOffset, length).ToArray();
            return new RemoteFilePreviewBlock(
                connectionKey, path, actualOffset, header, block, version);
        }
    }

    private sealed class FakeSftpService(string path, string content) :
        ISftpConnectionService, IRemoteFilePreviewReader
    {
        private readonly FakeRemotePreviewReader _preview = new(path, content);
        private readonly SftpConnectionInfo _connection =
            new("test-session", "example.test", 22, "tester", "SHA256:test");
        public IReadOnlyList<SftpConnectionInfo> Connections => [_connection];
        public int MaximumBlockBytes => _preview.MaximumBlockBytes;

        public Task<SftpConnectionInfo> ConnectAsync(
            SftpConnectionRequest request,
            CancellationToken cancellationToken = default) => Task.FromResult(_connection);

        public Task DisconnectAsync(string connectionKey, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<FileLocationEntry>> ListAsync(
            string connectionKey,
            string requestedPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileLocationEntry>>([
                new FileLocationEntry(Path.GetFileName(path), path, FileEntryType.File,
                    System.Text.Encoding.UTF8.GetByteCount(content), DateTimeOffset.UnixEpoch)
            ]);

        public Task<FileTransferOutcome> TransferAsync(
            FileTransferRequest request,
            IProgress<FileOperationProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RemoteFilePreviewBlock> ReadBlockAsync(
            string connectionKey,
            string requestedPath,
            long offset,
            int maximumBytes,
            RemoteFilePreviewVersion? expectedVersion = null,
            CancellationToken cancellationToken = default) =>
            _preview.ReadBlockAsync(connectionKey, requestedPath, offset, maximumBytes,
                expectedVersion, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
        public Task<string?> PromptTextAsync(string title, string message, string initialValue = "") =>
            Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, string confirmText) => Task.FromResult(false);
        public Task CopyTextAsync(string text) => Task.CompletedTask;
        public Task OpenAsync(string path) => Task.CompletedTask;
        public Task EditAsync(string path) => Task.CompletedTask;
        public Task OpenContainingFolderAsync(string path) => Task.CompletedTask;
    }

    private sealed class UnusedDiskManagement : IDiskManagementService
    {
        public Task UnmountAsync(StorageVolume volume, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task EjectAsync(StorageVolume volume, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptySystemSearchHistory : ISystemSearchHistoryService
    {
        public Task WarmupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IReadOnlyList<string> Suggest(string query, int limit) => [];
    }
}
