using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed record ScanPipelineOptions(
    int WorkerCount = 0,
    int QueueCapacity = 0,
    int BatchSize = 0,
    SystemPerformanceProfile? PerformanceProfile = null)
{
    private SystemPerformanceProfile Profile => PerformanceProfile ?? SystemPerformanceProfile.Current;
    public int EffectiveWorkerCount => WorkerCount > 0 ? WorkerCount : Profile.ScanWorkerCount;
    public int EffectiveQueueCapacity => QueueCapacity > 0 ? QueueCapacity : Profile.ScanQueueCapacity;
    public int EffectiveBatchSize => BatchSize > 0 ? BatchSize : Profile.ScanBatchSize;
}

public sealed class ScanCoordinator(
    IFileSystemScanner scanner,
    IFileClassifier classifier,
    IOdysseyStore store,
    ScanPipelineOptions options,
    ILogger<ScanCoordinator> logger) : IScanCoordinator
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> TargetGates = new();
    private sealed record ProcessedItem(FileEntry? Entry, ScanError? Error);

    public async Task<ScanSession> ScanAsync(
        ScanTarget target,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        Guid? resumedFromScanId = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var cancelled = new ScanSession
            {
                Id = Guid.NewGuid(),
                TargetId = target.Id,
                StartedAt = now,
                CompletedAt = now,
                Status = ScanStatus.Cancelled,
                ResumedFromScanId = resumedFromScanId
            };
            await store.StartScanAsync(cancelled, CancellationToken.None).ConfigureAwait(false);
            await store.CompleteScanAsync(cancelled.Id, ScanStatus.Cancelled, now, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(new ScanProgress(0, 0, 0, 0, 0, TimeSpan.Zero));
            return cancelled;
        }
        var gate = TargetGates.GetOrAdd(target.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ScanCoreAsync(target, progress, cancellationToken, resumedFromScanId).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<ScanSession> ScanCoreAsync(
        ScanTarget target,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken,
        Guid? resumedFromScanId)
    {
        var started = DateTimeOffset.UtcNow;
        var scan = new ScanSession
        {
            Id = Guid.NewGuid(),
            TargetId = target.Id,
            StartedAt = started,
            Status = ScanStatus.Running,
            ResumedFromScanId = resumedFromScanId
        };
        // Persist the attempted scan even when cancellation was requested immediately,
        // so the investigation history remains complete.
        await store.StartScanAsync(scan, CancellationToken.None).ConfigureAwait(false);
        logger.LogInformation("Scan {ScanId} started for target {TargetId}", scan.Id, target.Id);
        using var pipelineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pipelineToken = pipelineCancellation.Token;

        long files = 0, directories = 0, indexed = 0, bytes = 0, errors = 0;
        long lastProgressTick = 0;
        string? currentPath = null;
        var stopwatch = Stopwatch.StartNew();
        var input = Channel.CreateBounded<FileSystemItem>(new BoundedChannelOptions(options.EffectiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false
        });
        var output = Channel.CreateBounded<ProcessedItem>(new BoundedChannelOptions(options.EffectiveQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true
        });

        ScanProgress SnapshotProgress() => new(
            Interlocked.Read(ref files), Interlocked.Read(ref directories), Interlocked.Read(ref indexed),
            Interlocked.Read(ref bytes), Interlocked.Read(ref errors), stopwatch.Elapsed, Volatile.Read(ref currentPath));

        void Report(bool force = false)
        {
            if (progress is null) return;
            var now = stopwatch.ElapsedMilliseconds;
            var previous = Interlocked.Read(ref lastProgressTick);
            if (!force && now - previous < 150) return;
            if (!force && Interlocked.CompareExchange(ref lastProgressTick, now, previous) != previous) return;
            progress.Report(SnapshotProgress());
        }

        await store.SaveScanCheckpointAsync(scan.Id, SnapshotProgress(), CancellationToken.None).ConfigureAwait(false);

        var producer = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await foreach (var item in scanner.EnumerateAsync(target, pipelineToken).ConfigureAwait(false))
                    await input.Writer.WriteAsync(item, pipelineToken).ConfigureAwait(false);
            }
            catch (Exception ex) { failure = ex; pipelineCancellation.Cancel(); throw; }
            finally { input.Writer.TryComplete(failure); }
        }, CancellationToken.None);

        var workers = Enumerable.Range(0, options.EffectiveWorkerCount).Select(_ => Task.Run(async () =>
        {
            await foreach (var item in input.Reader.ReadAllAsync(pipelineToken).ConfigureAwait(false))
            {
                Volatile.Write(ref currentPath, item.FullPath);
                if (item.Error is not null)
                {
                    Interlocked.Increment(ref errors);
                    await output.Writer.WriteAsync(new ProcessedItem(null,
                        new ScanError(scan.Id, item.FullPath, item.Error, DateTimeOffset.UtcNow)), pipelineToken).ConfigureAwait(false);
                    Report();
                    continue;
                }

                if (item.Type == FileEntryType.File) Interlocked.Increment(ref files);
                else Interlocked.Increment(ref directories);

                try
                {
                    var entry = ReadMetadata(target.Id, item);
                    if (entry.Size is long length) Interlocked.Add(ref bytes, length);
                    await output.Writer.WriteAsync(new ProcessedItem(entry, null), pipelineToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (PortableFileSystemScanner.IsRecoverable(ex))
                {
                    Interlocked.Increment(ref errors);
                    await output.Writer.WriteAsync(new ProcessedItem(null,
                        new ScanError(scan.Id, item.FullPath, ex.Message, DateTimeOffset.UtcNow)), pipelineToken).ConfigureAwait(false);
                }
                Report();
            }
        }, CancellationToken.None)).ToArray();

        var completeOutput = Task.Run(async () =>
        {
            Exception? failure = null;
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (Exception ex) { failure = ex; pipelineCancellation.Cancel(); throw; }
            finally { output.Writer.TryComplete(failure); }
        }, CancellationToken.None);

        var consumer = Task.Run(async () =>
        {
            var entries = new List<FileEntry>(options.EffectiveBatchSize);
            var scanErrors = new List<ScanError>(Math.Min(options.EffectiveBatchSize, 64));
            try
            {
                await foreach (var item in output.Reader.ReadAllAsync(pipelineToken).ConfigureAwait(false))
                {
                    if (item.Entry is not null) entries.Add(item.Entry);
                    if (item.Error is not null) scanErrors.Add(item.Error);
                    if (entries.Count + scanErrors.Count < options.EffectiveBatchSize) continue;
                    await FlushAsync(entries, scanErrors, scan.Id, pipelineToken).ConfigureAwait(false);
                    Interlocked.Add(ref indexed, entries.Count);
                    await store.SaveScanCheckpointAsync(scan.Id, SnapshotProgress(), pipelineToken).ConfigureAwait(false);
                    entries.Clear();
                    scanErrors.Clear();
                    Report();
                }
                await FlushAsync(entries, scanErrors, scan.Id, pipelineToken).ConfigureAwait(false);
                Interlocked.Add(ref indexed, entries.Count);
                await store.SaveScanCheckpointAsync(scan.Id, SnapshotProgress(), pipelineToken).ConfigureAwait(false);
            }
            catch
            {
                pipelineCancellation.Cancel();
                throw;
            }
        }, CancellationToken.None);

        ScanStatus status;
        try
        {
            await Task.WhenAll(producer, completeOutput, consumer).ConfigureAwait(false);
            status = ScanStatus.Completed;
            await store.MarkMissingAsync(target.Id, scan.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = ScanStatus.Cancelled;
            logger.LogInformation("Scan {ScanId} cancelled after {Elapsed}", scan.Id, stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            status = ScanStatus.Failed;
            logger.LogError(ex, "Scan {ScanId} failed", scan.Id);
        }

        stopwatch.Stop();
        var completed = DateTimeOffset.UtcNow;
        try
        {
            await store.SaveScanCheckpointAsync(scan.Id, SnapshotProgress(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            status = ScanStatus.Failed;
            logger.LogError(ex, "Could not persist the final checkpoint for scan {ScanId}", scan.Id);
        }

        try
        {
            await store.CompleteScanAsync(scan.Id, status, completed, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            status = ScanStatus.Failed;
            logger.LogError(ex, "Could not persist the final status for scan {ScanId}; startup recovery will reconcile it", scan.Id);
        }
        Report(true);
        logger.LogInformation("Scan {ScanId} finished with {Status} in {Elapsed}; {Entries} entries indexed and {Errors} recoverable errors",
            scan.Id, status, stopwatch.Elapsed, indexed, errors);
        return scan with { CompletedAt = completed, Status = status };
    }

    private FileEntry ReadMetadata(Guid targetId, FileSystemItem item)
    {
        FileSystemInfo info = item.Type == FileEntryType.File ? new FileInfo(item.FullPath) : new DirectoryInfo(item.FullPath);
        info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("Entry disappeared while its metadata was being read.", item.FullPath);
        var extension = item.Type == FileEntryType.File ? Path.GetExtension(item.FullPath) : null;
        var created = ToNullableTimestamp(info.CreationTimeUtc);
        var modified = ToNullableTimestamp(info.LastWriteTimeUtc);
        return new FileEntry
        {
            TargetId = targetId,
            Name = info.Name,
            FullPath = Path.GetFullPath(item.FullPath),
            ParentPath = Path.GetDirectoryName(Path.GetFullPath(item.FullPath)) ?? string.Empty,
            Extension = string.IsNullOrEmpty(extension) ? null : extension.ToLowerInvariant(),
            Type = item.Type,
            Size = info is FileInfo file ? file.Length : null,
            CreatedAt = created,
            ModifiedAt = modified,
            Category = classifier.Classify(extension, item.Type)
        };
    }

    private async Task FlushAsync(List<FileEntry> entries, List<ScanError> errors, Guid scanId, CancellationToken token)
    {
        if (entries.Count > 0) await store.UpsertEntriesAsync(scanId, entries, token).ConfigureAwait(false);
        if (errors.Count > 0) await store.AddScanErrorsAsync(errors, token).ConfigureAwait(false);
    }

    private static DateTimeOffset? ToNullableTimestamp(DateTime value) =>
        value == DateTime.MinValue ? null : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
