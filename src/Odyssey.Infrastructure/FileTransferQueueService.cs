using System.Text.Json;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class FileTransferQueueService : IFileTransferQueueService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IFileOperationService _operations;
    private readonly ISftpConnectionService? _sftp;
    private readonly IArchiveService? _archives;
    private readonly IArchiveMutationService? _archiveMutations;
    private readonly string _queuePath;
    private readonly string _archiveRelayRoot;
    private readonly List<FileTransferJob> _items = [];
    private readonly object _sync = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeCancellation;
    private Task? _worker;
    private bool _initialized;

    public FileTransferQueueService(
        IFileOperationService operations,
        ApplicationStorage storage,
        ISftpConnectionService? sftp = null,
        IArchiveService? archives = null,
        IArchiveMutationService? archiveMutations = null)
    {
        _operations = operations;
        _sftp = sftp;
        _archives = archives;
        _archiveMutations = archiveMutations;
        _queuePath = Path.Combine(storage.DirectoryPath, "transfer-queue.json");
        _archiveRelayRoot = Path.Combine(storage.DirectoryPath, "archive-relay");
    }

    public event EventHandler? Changed;

    public IReadOnlyList<FileTransferJob> Items
    {
        get { lock (_sync) return _items.ToArray(); }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_initialized) return;
            _initialized = true;
        }

        if (_archiveMutations is IArchiveRecoveryService recovery)
            _ = await recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
        CleanupStaleArchiveRelays();
        await RecoverTemporaryQueueAsync(cancellationToken).ConfigureAwait(false);

        var loaded = await LoadAsync(cancellationToken);
        lock (_sync)
        {
            _items.Clear();
            _items.AddRange(loaded.Select(item => item.State == FileTransferState.Running
                ? item with { State = FileTransferState.Queued, Error = "Odyssey restarted before this transfer finished." }
                : item));
        }
        await SaveAsync(cancellationToken);
        _worker = Task.Run(() => WorkerAsync(_lifetime.Token), CancellationToken.None);
        Changed?.Invoke(this, EventArgs.Empty);
        SignalWorker();
    }

    public async Task<IReadOnlyList<FileTransferJob>> EnqueueAsync(
        IEnumerable<FileTransferRequest> requests,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var now = DateTimeOffset.UtcNow;
        var added = requests.Select(request =>
        {
            ValidateRequest(request);
            return new FileTransferJob
            {
                Id = Guid.NewGuid(),
                Request = request with
                {
                    SourcePath = request.SourceEndpoint == FileTransferEndpointKind.Local
                        ? Path.GetFullPath(request.SourcePath)
                        : request.SourcePath,
                    DestinationDirectory = request.DestinationEndpoint == FileTransferEndpointKind.Local
                        ? Path.GetFullPath(request.DestinationDirectory)
                        : request.DestinationDirectory,
                    SourceConnectionKey = request.SourceEndpoint == FileTransferEndpointKind.Archive
                        ? Path.GetFullPath(request.SourceConnectionKey!)
                        : request.SourceConnectionKey,
                    DestinationConnectionKey = request.DestinationEndpoint == FileTransferEndpointKind.Archive
                        ? Path.GetFullPath(request.DestinationConnectionKey!)
                        : request.DestinationConnectionKey
                },
                State = FileTransferState.Queued,
                CreatedAt = now
            };
        }).ToArray();
        if (added.Length == 0) return added;

        lock (_sync) _items.AddRange(added);
        await SaveAndNotifyAsync(cancellationToken);
        SignalWorker();
        return added;
    }

    public async Task PauseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var cancelActive = false;
        lock (_sync)
        {
            var item = Find(id);
            if (item.State is not (FileTransferState.Queued or FileTransferState.Running)) return;
            cancelActive = item.State == FileTransferState.Running;
            Replace(item with { State = FileTransferState.Paused, Error = null });
        }
        if (cancelActive) _activeCancellation?.Cancel();
        await SaveAndNotifyAsync(cancellationToken);
    }

    public async Task ResumeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        lock (_sync)
        {
            var item = Find(id);
            if (item.State != FileTransferState.Paused) return;
            Replace(item with { State = FileTransferState.Queued, Error = null, CompletedAt = null });
        }
        await SaveAndNotifyAsync(cancellationToken);
        SignalWorker();
    }

    public async Task RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        lock (_sync)
        {
            var item = Find(id);
            if (item.State is not (FileTransferState.Failed or FileTransferState.Cancelled)) return;
            Replace(item with
            {
                State = FileTransferState.Queued,
                Error = null,
                CompletedAt = null,
                BytesCompleted = 0,
                TotalBytes = null,
                CurrentPath = null,
                DestinationPath = null
            });
        }
        await SaveAndNotifyAsync(cancellationToken);
        SignalWorker();
    }

    public async Task CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        var cancelActive = false;
        lock (_sync)
        {
            var item = Find(id);
            if (item.State is FileTransferState.Completed or FileTransferState.Skipped or FileTransferState.Cancelled) return;
            cancelActive = item.State == FileTransferState.Running;
            Replace(item with { State = FileTransferState.Cancelled, CompletedAt = DateTimeOffset.UtcNow });
        }
        if (cancelActive) _activeCancellation?.Cancel();
        await SaveAndNotifyAsync(cancellationToken);
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { break; }

            while (!cancellationToken.IsCancellationRequested && TryClaimNext(out var job))
            {
                await SaveAndNotifyAsync(CancellationToken.None);
                using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _activeCancellation = operationCancellation;
                var progress = new Progress<FileOperationProgress>(value => UpdateProgress(job.Id, value));
                try
                {
                    var isLocal = job.Request.SourceEndpoint == FileTransferEndpointKind.Local
                                  && job.Request.DestinationEndpoint == FileTransferEndpointKind.Local;
                    if (_operations.AccessMode == FileAccessMode.ReadOnly)
                        throw new InvalidOperationException("Odyssey is in read-only mode. Enable file management before running transfers.");
                    FileTransferOutcome outcome;
                    if (isLocal)
                    {
                        outcome = await _operations.TransferAsync(job.Request, progress, operationCancellation.Token);
                    }
                    else if (job.Request.SourceEndpoint == FileTransferEndpointKind.Archive
                             && job.Request.DestinationEndpoint == FileTransferEndpointKind.Local)
                    {
                        var archives = _archives ?? throw new InvalidOperationException("Archive support is unavailable.");
                        archives.AccessMode = _operations.AccessMode;
                        outcome = await archives.ExtractAsync(
                            job.Request.SourceConnectionKey!, job.Request.SourcePath,
                            job.Request.DestinationDirectory, job.Request.ConflictPolicy,
                            progress, operationCancellation.Token);
                    }
                    else if (job.Request.SourceEndpoint == FileTransferEndpointKind.Local
                             && job.Request.DestinationEndpoint == FileTransferEndpointKind.Archive)
                    {
                        var mutations = _archiveMutations
                                        ?? throw new InvalidOperationException("Archive writing is unavailable.");
                        mutations.AccessMode = _operations.AccessMode;
                        outcome = await mutations.AddAsync(
                            job.Request.DestinationConnectionKey!, job.Request.SourcePath,
                            job.Request.DestinationDirectory, job.Request.ConflictPolicy,
                            progress, operationCancellation.Token);
                    }
                    else if (job.Request.SourceEndpoint == FileTransferEndpointKind.Archive
                             && job.Request.DestinationEndpoint == FileTransferEndpointKind.Archive)
                    {
                        outcome = await RelayArchiveEntryAsync(job.Request, progress, operationCancellation.Token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        outcome = await (_sftp ?? throw new InvalidOperationException("SFTP support is unavailable."))
                            .TransferAsync(job.Request, progress, operationCancellation.Token);
                    }
                    lock (_sync)
                    {
                        var current = Find(job.Id);
                        if (current.State == FileTransferState.Cancelled) continue;
                        Replace(current with
                        {
                            State = outcome.Skipped ? FileTransferState.Skipped : FileTransferState.Completed,
                            DestinationPath = outcome.DestinationPath,
                            OperationId = outcome.Operation?.Id,
                            CompletedAt = DateTimeOffset.UtcNow,
                            Error = null
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_sync)
                    {
                        var current = Find(job.Id);
                        if (current.State == FileTransferState.Running)
                            Replace(current with { State = FileTransferState.Queued, Error = "Transfer interrupted." });
                    }
                }
                catch (Exception ex)
                {
                    lock (_sync)
                    {
                        var current = Find(job.Id);
                        if (current.State is not (FileTransferState.Paused or FileTransferState.Cancelled))
                            Replace(current with
                            {
                                State = FileTransferState.Failed,
                                Error = ex.Message,
                                CompletedAt = DateTimeOffset.UtcNow
                            });
                    }
                }
                finally
                {
                    _activeCancellation = null;
                    await SaveAndNotifyAsync(CancellationToken.None);
                }
            }
        }
    }

    private bool TryClaimNext(out FileTransferJob job)
    {
        lock (_sync)
        {
            var next = _items.FirstOrDefault(item => item.State == FileTransferState.Queued);
            if (next is null)
            {
                job = null!;
                return false;
            }
            job = next with
            {
                State = FileTransferState.Running,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedAt = null,
                Error = null,
                Attempt = next.Attempt + 1
            };
            Replace(job);
            return true;
        }
    }

    private void UpdateProgress(Guid id, FileOperationProgress progress)
    {
        lock (_sync)
        {
            var item = _items.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null || item.State != FileTransferState.Running) return;
            Replace(item with
            {
                BytesCompleted = progress.BytesCompleted,
                TotalBytes = progress.TotalBytes,
                CurrentPath = progress.CurrentPath
            });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<IReadOnlyList<FileTransferJob>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadQueueFileAsync(_queuePath, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            QuarantineInvalidQueue(_queuePath, "transfer-queue.corrupt");
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task RecoverTemporaryQueueAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = _queuePath + ".tmp";
        if (!File.Exists(temporaryPath)) return;
        try
        {
            _ = await ReadQueueFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
            await PublishQueueFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            QuarantineInvalidQueue(temporaryPath, "transfer-queue.tmp.corrupt");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked temporary snapshot can be reconsidered on the next startup.
        }
    }

    private static async Task<IReadOnlyList<FileTransferJob>> ReadQueueFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var loaded = await JsonSerializer.DeserializeAsync<List<FileTransferJob?>>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? [];
        var identities = new HashSet<Guid>();
        foreach (var item in loaded)
        {
            if (item is null)
                throw new JsonException("The persisted transfer queue contains an empty job.");
            if (item.Id == Guid.Empty || !identities.Add(item.Id) || !Enum.IsDefined(item.State)
                || item.Request is null
                || !Enum.IsDefined(item.Request.Kind)
                || !Enum.IsDefined(item.Request.ConflictPolicy)
                || !Enum.IsDefined(item.Request.SourceEndpoint)
                || !Enum.IsDefined(item.Request.DestinationEndpoint))
                throw new JsonException("The persisted transfer queue contains an invalid job identity, state, or request.");
            try { ValidateRequest(item.Request); }
            catch (ArgumentException ex) { throw new JsonException("The persisted transfer queue contains an invalid request.", ex); }
        }
        return loaded.Select(item => item!).ToArray();
    }

    private static void QuarantineInvalidQueue(string path, string prefix)
    {
        try
        {
            if (!File.Exists(path)) return;
            var directory = Path.GetDirectoryName(path)!;
            var quarantinePath = Path.Combine(directory,
                $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
            File.Move(path, quarantinePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting Odyssey is more important than preserving a diagnostic copy
            // when the application-data directory itself cannot be modified.
        }
    }

    private async Task SaveAndNotifyAsync(CancellationToken cancellationToken)
    {
        await SaveAsync(cancellationToken);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            FileTransferJob[] snapshot;
            lock (_sync) snapshot = _items.ToArray();
            var temporaryPath = _queuePath + ".tmp";
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            await PublishQueueFileAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
        }
        finally { _saveGate.Release(); }
    }

    private async Task PublishQueueFileAsync(string temporaryPath, CancellationToken cancellationToken)
    {
        const int maximumAttempts = 8;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporaryPath, _queuePath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < maximumAttempts)
            {
                // Windows denies atomic replacement while a short-lived reader or scanner has the
                // destination open without delete sharing. Keep the complete temporary snapshot and retry.
                await Task.Delay(Math.Min(25 * attempt, 200), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateRequest(FileTransferRequest request)
    {
        if (request.Kind is not (FileOperationKind.Copy or FileOperationKind.Move))
            throw new ArgumentException("Only copy and move requests can be queued.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.SourcePath)
            || (request.DestinationEndpoint != FileTransferEndpointKind.Archive
                && string.IsNullOrWhiteSpace(request.DestinationDirectory)))
            throw new ArgumentException("Source and destination are required.", nameof(request));
        if (request.SourceEndpoint == FileTransferEndpointKind.Sftp && string.IsNullOrWhiteSpace(request.SourceConnectionKey))
            throw new ArgumentException("An SFTP source connection is required.", nameof(request));
        if (request.SourceEndpoint == FileTransferEndpointKind.Archive
            && string.IsNullOrWhiteSpace(request.SourceConnectionKey))
            throw new ArgumentException("An archive source path is required.", nameof(request));
        if (request.SourceEndpoint == FileTransferEndpointKind.Archive
            && (request.DestinationEndpoint is not (FileTransferEndpointKind.Local or FileTransferEndpointKind.Archive)
                || request.Kind != FileOperationKind.Copy))
            throw new ArgumentException("Archive entries can only be copied to local or archive destinations.", nameof(request));
        if (request.DestinationEndpoint == FileTransferEndpointKind.Archive)
        {
            if (request.SourceEndpoint is not (FileTransferEndpointKind.Local or FileTransferEndpointKind.Archive)
                || request.Kind != FileOperationKind.Copy)
                throw new ArgumentException("Only local or archive items can be copied into an archive.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.DestinationConnectionKey))
                throw new ArgumentException("An archive destination path is required.", nameof(request));
        }
        if (request.DestinationEndpoint == FileTransferEndpointKind.Sftp && string.IsNullOrWhiteSpace(request.DestinationConnectionKey))
            throw new ArgumentException("An SFTP destination connection is required.", nameof(request));
    }

    private async Task<FileTransferOutcome> RelayArchiveEntryAsync(
        FileTransferRequest request,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archives = _archives ?? throw new InvalidOperationException("Archive reading is unavailable.");
        var mutations = _archiveMutations ?? throw new InvalidOperationException("Archive writing is unavailable.");
        archives.AccessMode = _operations.AccessMode;
        mutations.AccessMode = _operations.AccessMode;
        EnsureRelayRoot();
        var relay = Path.Combine(_archiveRelayRoot, $"relay-{Guid.NewGuid():N}");
        Directory.CreateDirectory(relay);
        try
        {
            var extracted = await archives.ExtractAsync(
                request.SourceConnectionKey!, request.SourcePath, relay, FileConflictPolicy.Fail,
                progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await mutations.AddAsync(
                request.DestinationConnectionKey!, extracted.DestinationPath,
                request.DestinationDirectory, request.ConflictPolicy,
                progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DeleteRelay(relay);
        }
    }

    private static void DeleteRelay(string path)
    {
        try
        {
            DeleteRelayTree(new DirectoryInfo(path));
        }
        catch { /* A failed cleanup remains isolated under Odyssey application data. */ }
    }

    private void CleanupStaleArchiveRelays()
    {
        EnsureRelayRoot();
        foreach (var path in Directory.EnumerateFileSystemEntries(_archiveRelayRoot, "relay-*",
                     SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith("relay-", StringComparison.Ordinal)
                || !Guid.TryParseExact(name["relay-".Length..], "N", out _)
                || !Directory.Exists(path))
                continue;
            DeleteRelay(path);
        }
    }

    private static void DeleteRelayTree(DirectoryInfo directory)
    {
        directory.Refresh();
        if (!directory.Exists) return;
        if (directory.LinkTarget is not null || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            directory.Delete(recursive: false);
            return;
        }
        foreach (var child in directory.EnumerateFileSystemInfos())
        {
            child.Refresh();
            if (child.LinkTarget is not null || child.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                child.Delete();
            }
            else if (child is DirectoryInfo childDirectory)
            {
                DeleteRelayTree(childDirectory);
            }
            else
            {
                child.Delete();
            }
        }
        directory.Delete(recursive: false);
    }

    private void EnsureRelayRoot()
    {
        var current = new DirectoryInfo(Path.GetDirectoryName(_archiveRelayRoot)!);
        while (current is not null)
        {
            if (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Archive relay storage cannot cross a symbolic link or reparse point.");
            current = current.Parent;
        }
        Directory.CreateDirectory(_archiveRelayRoot);
        var relayRoot = new DirectoryInfo(_archiveRelayRoot);
        if (relayRoot.LinkTarget is not null || relayRoot.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Archive relay storage cannot be a symbolic link or reparse point.");
    }

    private FileTransferJob Find(Guid id) =>
        _items.FirstOrDefault(item => item.Id == id)
        ?? throw new KeyNotFoundException($"Unknown transfer job: {id}");

    private void Replace(FileTransferJob item)
    {
        var index = _items.FindIndex(candidate => candidate.Id == item.Id);
        if (index < 0) throw new KeyNotFoundException($"Unknown transfer job: {item.Id}");
        _items[index] = item;
    }

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException("Initialize the transfer queue before using it.");
    }

    private void SignalWorker()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _activeCancellation?.Cancel();
        if (_worker is not null)
        {
            try { await _worker; }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        _signal.Dispose();
        _saveGate.Dispose();
    }
}
