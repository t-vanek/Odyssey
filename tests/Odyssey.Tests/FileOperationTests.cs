using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class FileOperationTests
{
    [Fact]
    public async Task ReadOnlyMode_BlocksEveryMutationBeforeTouchingFilesystem()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "source.txt");
        var destination = Path.Combine(environment.Root, "destination");
        await File.WriteAllTextAsync(source, "Odyssey");
        Directory.CreateDirectory(destination);
        var service = new SafeFileOperationService(environment.Storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CopyAsync(source, destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateDirectoryAsync(destination, "new-folder"));

        Assert.False(File.Exists(Path.Combine(destination, "source.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "new-folder")));
        Assert.Empty(service.History);
    }

    [Fact]
    public async Task ManageMode_CopiesFoldersWithoutOverwritingConflicts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "source");
        var destination = Path.Combine(environment.Root, "destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "report.txt"), "first");
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };

        var record = await service.CopyAsync(source, destination);

        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(destination, "source", "nested", "report.txt")));
        Assert.Equal(FileOperationKind.Copy, record.Kind);
        await Assert.ThrowsAsync<IOException>(() => service.CopyAsync(source, destination));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(destination, "source", "nested", "report.txt")));
    }

    [Fact]
    public async Task RenameAndMove_CanBeUndoneSafely()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var firstDirectory = Path.Combine(environment.Root, "first");
        var secondDirectory = Path.Combine(environment.Root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var original = Path.Combine(firstDirectory, "old.txt");
        await File.WriteAllTextAsync(original, "content");
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };

        var rename = await service.RenameAsync(original, "new.txt");
        Assert.True(File.Exists(rename.DestinationPath));
        await service.UndoLastAsync();
        Assert.True(File.Exists(original));

        var move = await service.MoveAsync(original, secondDirectory);
        Assert.True(File.Exists(move.DestinationPath));
        await service.UndoLastAsync();
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(Path.Combine(secondDirectory, "old.txt")));
    }

    [Fact]
    public async Task CreateFolder_CanOnlyBeUndoneWhileEmpty()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var parent = Path.Combine(environment.Root, "parent");
        Directory.CreateDirectory(parent);
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };

        var created = await service.CreateDirectoryAsync(parent, "new-folder");
        await File.WriteAllTextAsync(Path.Combine(created.SourcePath, "keep.txt"), "data");

        await Assert.ThrowsAsync<IOException>(() => service.UndoLastAsync());
        Assert.True(Directory.Exists(created.SourcePath));
    }

    [Fact]
    public async Task AccessModePreference_IsReadOnlyByDefaultAndPersistsChoice()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var preferences = new UserPreferencesService(environment.Storage);
        Assert.True(preferences.ReadOnlyMode);

        preferences.ReadOnlyMode = false;

        Assert.False(new UserPreferencesService(environment.Storage).ReadOnlyMode);
    }

    [Fact]
    public async Task ReplaceAndVerify_CanBeUndoneWithoutLosingOriginalDestination()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var sourceDirectory = Path.Combine(environment.Root, "source");
        var destinationDirectory = Path.Combine(environment.Root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "report.txt");
        var destination = Path.Combine(destinationDirectory, "report.txt");
        await File.WriteAllTextAsync(source, "new content");
        await File.WriteAllTextAsync(destination, "original content");
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };

        var outcome = await service.TransferAsync(new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = source,
            DestinationDirectory = destinationDirectory,
            ConflictPolicy = FileConflictPolicy.Replace,
            VerifyAfterCopy = true
        });

        Assert.True(outcome.Verified);
        Assert.Equal("new content", await File.ReadAllTextAsync(destination));
        Assert.NotNull(outcome.Operation?.ReplacedItemBackupPath);
        Assert.True(File.Exists(outcome.Operation.ReplacedItemBackupPath));

        await service.UndoLastAsync();

        Assert.Equal("original content", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(outcome.Operation.ReplacedItemBackupPath));
    }

    [Fact]
    public async Task ConflictPolicies_CanSkipOrGenerateAnAvailableName()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var sourceDirectory = Path.Combine(environment.Root, "source");
        var destinationDirectory = Path.Combine(environment.Root, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "photo.jpg");
        await File.WriteAllTextAsync(source, "new");
        await File.WriteAllTextAsync(Path.Combine(destinationDirectory, "photo.jpg"), "existing");
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };

        var skipped = await service.TransferAsync(new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = source,
            DestinationDirectory = destinationDirectory,
            ConflictPolicy = FileConflictPolicy.Skip
        });
        var kept = await service.TransferAsync(new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = source,
            DestinationDirectory = destinationDirectory,
            ConflictPolicy = FileConflictPolicy.KeepBoth
        });

        Assert.True(skipped.Skipped);
        Assert.Null(skipped.Operation);
        Assert.Equal("existing", await File.ReadAllTextAsync(Path.Combine(destinationDirectory, "photo.jpg")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(destinationDirectory, "photo (2).jpg")));
        Assert.Equal(Path.Combine(destinationDirectory, "photo (2).jpg"), kept.DestinationPath);
    }

    [Fact]
    public async Task TransferQueue_PersistsFailureAndCompletesAfterRetry()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var destinationDirectory = Path.Combine(environment.Root, "destination");
        var source = Path.Combine(environment.Root, "later.txt");
        Directory.CreateDirectory(destinationDirectory);
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };
        Guid id;

        await using (var firstQueue = new FileTransferQueueService(service, environment.Storage))
        {
            await firstQueue.InitializeAsync();
            id = (await firstQueue.EnqueueAsync([new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = source,
                DestinationDirectory = destinationDirectory
            }])).Single().Id;
            await WaitForStateAsync(firstQueue, id, FileTransferState.Failed);
        }

        await File.WriteAllTextAsync(source, "ready now");
        await using var secondQueue = new FileTransferQueueService(service, environment.Storage);
        await secondQueue.InitializeAsync();
        Assert.Equal(FileTransferState.Failed, secondQueue.Items.Single(item => item.Id == id).State);

        await secondQueue.RetryAsync(id);
        await WaitForStateAsync(secondQueue, id, FileTransferState.Completed);

        Assert.Equal("ready now", await File.ReadAllTextAsync(Path.Combine(destinationDirectory, "later.txt")));
        Assert.Equal(2, secondQueue.Items.Single(item => item.Id == id).Attempt);
    }

    [Fact]
    public async Task TransferQueue_PausesRunningWorkAndResumesItAsANewAttempt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var operations = new PauseAwareOperationService();
        await using var queue = new FileTransferQueueService(operations, environment.Storage);
        await queue.InitializeAsync();
        var job = (await queue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = Path.Combine(environment.Root, "source.bin"),
            DestinationDirectory = environment.Root
        }])).Single();
        await operations.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await queue.PauseAsync(job.Id);
        await WaitForStateAsync(queue, job.Id, FileTransferState.Paused);
        await queue.ResumeAsync(job.Id);
        await WaitForStateAsync(queue, job.Id, FileTransferState.Completed);

        Assert.Equal(2, queue.Items.Single(item => item.Id == job.Id).Attempt);
    }

    [Fact]
    public async Task TransferQueue_ShutdownInterruptsRunningWorkWithoutHanging()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var operations = new PauseAwareOperationService();
        var queue = new FileTransferQueueService(operations, environment.Storage);
        await queue.InitializeAsync();
        await queue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = Path.Combine(environment.Root, "large.bin"),
            DestinationDirectory = environment.Root
        }]);
        await operations.FirstAttemptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TransferQueue_RoutesSftpJobsWithoutPersistingCredentials()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var localOperations = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };
        var remote = new RecordingSftpService();
        await using var queue = new FileTransferQueueService(localOperations, environment.Storage, remote);
        await queue.InitializeAsync();
        var job = (await queue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = Path.Combine(environment.Root, "upload.txt"),
            DestinationDirectory = "/incoming",
            DestinationEndpoint = FileTransferEndpointKind.Sftp,
            DestinationConnectionKey = "server-identity",
            VerifyAfterCopy = true
        }])).Single();

        await WaitForStateAsync(queue, job.Id, FileTransferState.Completed);

        Assert.Single(remote.Transfers);
        Assert.Equal("server-identity", remote.Transfers[0].DestinationConnectionKey);
        var persisted = await File.ReadAllTextAsync(Path.Combine(environment.Storage.DirectoryPath, "transfer-queue.json"));
        Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransferQueue_ReadOnlyModeBlocksRemoteWriteBeforeSftpCall()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var localOperations = new SafeFileOperationService(environment.Storage);
        var remote = new RecordingSftpService();
        await using var queue = new FileTransferQueueService(localOperations, environment.Storage, remote);
        await queue.InitializeAsync();
        var job = (await queue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = Path.Combine(environment.Root, "upload.txt"),
            DestinationDirectory = "/incoming",
            DestinationEndpoint = FileTransferEndpointKind.Sftp,
            DestinationConnectionKey = "server-identity"
        }])).Single();

        await WaitForStateAsync(queue, job.Id, FileTransferState.Failed);

        Assert.Empty(remote.Transfers);
    }

    [Fact]
    public async Task SftpConnection_ValidatesInputBeforeOpeningANetworkConnection()
    {
        await using var service = new SftpConnectionService();
        await Assert.ThrowsAsync<ArgumentException>(() => service.ConnectAsync(new SftpConnectionRequest
        {
            Host = string.Empty,
            Username = string.Empty,
            Password = string.Empty
        }));
    }

    private static async Task WaitForStateAsync(
        IFileTransferQueueService queue, Guid id, FileTransferState expected)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            if (queue.Items.Single(item => item.Id == id).State == expected) return;
            await Task.Delay(20);
        }
        Assert.Equal(expected, queue.Items.Single(item => item.Id == id).State);
    }

    private sealed class PauseAwareOperationService : IFileOperationService
    {
        private int _attempts;
        public TaskCompletionSource FirstAttemptStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileAccessMode AccessMode { get; set; } = FileAccessMode.ManageFiles;
        public IReadOnlyList<FileOperationRecord> History => [];

        public async Task<FileTransferOutcome> TransferAsync(
            FileTransferRequest request,
            IProgress<FileOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (attempt == 1)
            {
                FirstAttemptStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            var destination = Path.Combine(request.DestinationDirectory, Path.GetFileName(request.SourcePath));
            return new FileTransferOutcome(new FileOperationRecord
            {
                Id = Guid.NewGuid(),
                Kind = request.Kind,
                SourcePath = request.SourcePath,
                DestinationPath = destination,
                CompletedAt = DateTimeOffset.UtcNow,
                CanUndo = true
            }, destination, Skipped: false, Verified: true);
        }

        public Task<FileOperationRecord> CreateDirectoryAsync(string parentPath, string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FileOperationRecord> CopyAsync(string sourcePath, string destinationDirectory, IProgress<FileOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FileOperationRecord> MoveAsync(string sourcePath, string destinationDirectory, IProgress<FileOperationProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FileOperationRecord> RenameAsync(string sourcePath, string newName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FileOperationRecord> TrashAsync(string path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FileOperationRecord?> UndoLastAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingSftpService : ISftpConnectionService
    {
        public List<FileTransferRequest> Transfers { get; } = [];
        public IReadOnlyList<SftpConnectionInfo> Connections => [];
        public Task<SftpConnectionInfo> ConnectAsync(SftpConnectionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task DisconnectAsync(string connectionKey, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<FileLocationEntry>> ListAsync(string connectionKey, string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileLocationEntry>>([]);
        public Task<FileTransferOutcome> TransferAsync(FileTransferRequest request, IProgress<FileOperationProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Transfers.Add(request);
            return Task.FromResult(new FileTransferOutcome(null, request.DestinationDirectory + "/upload.txt", false, true));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
