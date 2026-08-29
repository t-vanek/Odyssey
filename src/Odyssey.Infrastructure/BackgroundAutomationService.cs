using System.Collections.Concurrent;
using System.Xml;
using Microsoft.Extensions.Logging;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed record BackgroundAutomationOptions(
    TimeSpan ChangeDebounce,
    TimeSpan InitialValidationDelay,
    TimeSpan RetryDelay,
    TimeSpan PeriodicInterval)
{
    public static BackgroundAutomationOptions Default { get; } = new(
        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(15),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(30));
}

public sealed class BackgroundAutomationService : IBackgroundAutomationService, IDisposable
{
    private readonly IOdysseyStore _store;
    private readonly IScanCoordinator _scanner;
    private readonly IContentExtractor _extractor;
    private readonly SystemPerformanceProfile _performance;
    private readonly ILogger<BackgroundAutomationService> _logger;
    private readonly BackgroundAutomationOptions _options;
    private readonly object _sync = new();
    private readonly PriorityQueue<BackgroundJob, int> _jobs = new();
    private readonly HashSet<string> _queuedKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, ScanTarget> _targets = [];
    private readonly Dictionary<Guid, FileSystemWatcher> _watchers = [];
    private readonly Dictionary<Guid, CancellationTokenSource> _debounces = [];
    private readonly HashSet<Guid> _verifiedTargets = [];
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private Task? _periodic;
    private Guid _sessionId;
    private DateTimeOffset _interactiveUntil;

    public BackgroundAutomationService(
        IOdysseyStore store,
        IScanCoordinator scanner,
        IContentExtractor extractor,
        SystemPerformanceProfile performance,
        ILogger<BackgroundAutomationService> logger,
        BackgroundAutomationOptions? options = null)
    {
        _store = store;
        _scanner = scanner;
        _extractor = extractor;
        _performance = performance;
        _logger = logger;
        _options = options ?? BackgroundAutomationOptions.Default;
    }

    public event EventHandler<BackgroundAutomationStatus>? StatusChanged;

    public async Task StartAsync(Guid sessionId, IReadOnlyCollection<ScanTarget> targets, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lifetime is not null) return;
        _sessionId = sessionId;
        _lifetime = new CancellationTokenSource();
        _worker = RunWorkerAsync(_lifetime.Token);
        _periodic = RunPeriodicAsync(_lifetime.Token);
        UpdateTargets(targets);
        var recoveries = (await _store.RecoverInterruptedScansAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .GroupBy(item => item.TargetId)
            .Select(group => group.MaxBy(item => item.UpdatedAt)!)
            .ToArray();
        var recoveringTargets = recoveries.Select(item => item.TargetId).ToHashSet();
        foreach (var recovery in recoveries)
            Enqueue(new BackgroundJob(BackgroundJobKind.Scan, sessionId, recovery.TargetId, recovery.ScanId), priority: 0);
        Enqueue(new BackgroundJob(BackgroundJobKind.Content, sessionId), priority: 2);
        Enqueue(new BackgroundJob(BackgroundJobKind.Maintenance, sessionId), priority: 3);
        foreach (var target in targets)
            if (!recoveringTargets.Contains(target.Id)) ScheduleInitialValidation(target, _lifetime.Token);
        if (recoveries.Length > 0)
            Publish(BackgroundActivity.ResumingScan, targets.FirstOrDefault(item => item.Id == recoveries[0].TargetId)?.RootPath,
                detail: recoveries[0].Checkpoint.CurrentPath);
        else
            Publish(BackgroundActivity.Watching, completed: targets.Count);
    }

    public void UpdateTargets(IReadOnlyCollection<ScanTarget> targets)
    {
        lock (_sync)
        {
            var incoming = targets.ToDictionary(target => target.Id);
            foreach (var removed in _targets.Keys.Except(incoming.Keys).ToArray())
            {
                if (_watchers.Remove(removed, out var watcher)) watcher.Dispose();
                if (_debounces.Remove(removed, out var debounce)) { debounce.Cancel(); debounce.Dispose(); }
                _targets.Remove(removed);
                _verifiedTargets.Remove(removed);
            }
            foreach (var target in targets)
            {
                var changed = !_targets.TryGetValue(target.Id, out var existing) || existing != target;
                _targets[target.Id] = target;
                if (!changed || _lifetime is null) continue;
                if (_watchers.Remove(target.Id, out var previous)) previous.Dispose();
                TryCreateWatcherLocked(target);
            }
        }
    }

    public void NotifyTargetScanned(ScanTarget target)
    {
        lock (_sync) _verifiedTargets.Add(target.Id);
        Enqueue(new BackgroundJob(BackgroundJobKind.Content, _sessionId), priority: 2);
    }

    public void NotifyUserActivity() => _interactiveUntil = DateTimeOffset.UtcNow.AddSeconds(5);

    public async Task StopAsync()
    {
        var lifetime = _lifetime;
        if (lifetime is null) return;
        _lifetime = null;
        lifetime.Cancel();
        lock (_sync)
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();
            foreach (var debounce in _debounces.Values) { debounce.Cancel(); debounce.Dispose(); }
            _debounces.Clear();
            _jobs.Clear();
            _queuedKeys.Clear();
        }
        try
        {
            if (_worker is not null) await _worker.ConfigureAwait(false);
            if (_periodic is not null) await _periodic.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        lifetime.Dispose();
        Publish(BackgroundActivity.Idle);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
    public void Dispose() => _lifetime?.Cancel();

    private void TryCreateWatcherLocked(ScanTarget target)
    {
        if (!Directory.Exists(target.RootPath))
        {
            Publish(BackgroundActivity.WaitingForTarget, target.RootPath);
            return;
        }
        try
        {
            var watcher = new FileSystemWatcher(target.RootPath)
            {
                IncludeSubdirectories = target.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };
            watcher.Created += (_, _) => DebounceScan(target.Id);
            watcher.Changed += (_, _) => DebounceScan(target.Id);
            watcher.Deleted += (_, _) => DebounceScan(target.Id);
            watcher.Renamed += (_, _) => DebounceScan(target.Id);
            watcher.Error += (_, args) =>
            {
                _logger.LogWarning(args.GetException(), "Filesystem watcher overflowed for {Target}", target.RootPath);
                DebounceScan(target.Id, TimeSpan.FromMilliseconds(250));
            };
            _watchers[target.Id] = watcher;
        }
        catch (Exception ex) when (PortableFileSystemScanner.IsRecoverable(ex))
        {
            _logger.LogDebug(ex, "Target {Target} cannot currently be watched", target.RootPath);
            Publish(BackgroundActivity.WaitingForTarget, target.RootPath, detail: ex.Message);
        }
    }

    private void DebounceScan(Guid targetId, TimeSpan? delay = null)
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_lifetime is null || !_targets.ContainsKey(targetId)) return;
            if (_debounces.Remove(targetId, out var previous)) { previous.Cancel(); previous.Dispose(); }
            var debounce = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _debounces[targetId] = debounce;
            token = debounce.Token;
        }
        _ = ScheduleAsync(new BackgroundJob(BackgroundJobKind.Scan, _sessionId, targetId),
            priority: 1, delay ?? _options.ChangeDebounce, token);
    }

    private void ScheduleInitialValidation(ScanTarget target, CancellationToken token) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_options.InitialValidationDelay, token).ConfigureAwait(false);
                lock (_sync) if (_verifiedTargets.Contains(target.Id)) return;
                Enqueue(new BackgroundJob(BackgroundJobKind.Scan, _sessionId, target.Id), priority: 3);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

    private async Task ScheduleAsync(BackgroundJob job, int priority, TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token).ConfigureAwait(false);
            Enqueue(job, priority);
        }
        catch (OperationCanceledException) { }
    }

    private void Enqueue(BackgroundJob job, int priority)
    {
        lock (_sync)
        {
            if (_lifetime is null || !_queuedKeys.Add(job.Key)) return;
            _jobs.Enqueue(job, priority);
        }
        _signal.Release();
    }

    private async Task RunWorkerAsync(CancellationToken token)
    {
        while (true)
        {
            await _signal.WaitAsync(token).ConfigureAwait(false);
            BackgroundJob job;
            lock (_sync)
            {
                if (!_jobs.TryDequeue(out job, out _)) continue;
                _queuedKeys.Remove(job.Key);
            }
            try
            {
                if (job.Kind is not BackgroundJobKind.Scan && DateTimeOffset.UtcNow < _interactiveUntil)
                {
                    var remaining = _interactiveUntil - DateTimeOffset.UtcNow;
                    _ = ScheduleAsync(job, 2, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, token);
                    continue;
                }
                switch (job.Kind)
                {
                    case BackgroundJobKind.Scan: await RunScanAsync(job, token).ConfigureAwait(false); break;
                    case BackgroundJobKind.Content: await RunContentAsync(job, token).ConfigureAwait(false); break;
                    case BackgroundJobKind.Maintenance: await RunMaintenanceAsync(token).ConfigureAwait(false); break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background job {Job} failed", job.Key);
                Publish(BackgroundActivity.Failed, detail: ex.Message);
            }
        }
    }

    private async Task RunScanAsync(BackgroundJob job, CancellationToken token)
    {
        ScanTarget? target;
        lock (_sync) _targets.TryGetValue(job.TargetId, out target);
        if (target is null) return;
        if (!Directory.Exists(target.RootPath))
        {
            Publish(BackgroundActivity.WaitingForTarget, target.RootPath);
            await ScheduleAsync(job, 3, _options.RetryDelay, token).ConfigureAwait(false);
            return;
        }
        Publish(job.ResumeFromScanId is null ? BackgroundActivity.ScanningChanges : BackgroundActivity.ResumingScan,
            target.RootPath);
        var result = await _scanner.ScanAsync(target, null, token, job.ResumeFromScanId).ConfigureAwait(false);
        if (result.Status == ScanStatus.Completed)
        {
            lock (_sync) _verifiedTargets.Add(target.Id);
            Enqueue(new BackgroundJob(BackgroundJobKind.Content, _sessionId), priority: 2);
            Publish(BackgroundActivity.Watching, target.RootPath);
        }
        else if (result.Status == ScanStatus.Failed)
            _ = ScheduleAsync(job with { ResumeFromScanId = result.Id }, 2, _options.RetryDelay, token);
    }

    private async Task RunContentAsync(BackgroundJob job, CancellationToken token)
    {
        const int batchSize = 64;
        var candidates = await _store.GetContentExtractionCandidatesAsync(
            job.SessionId, _extractor.SupportedExtensions, batchSize, token).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            Publish(BackgroundActivity.Watching);
            return;
        }
        Publish(BackgroundActivity.IndexingContent, completed: 0);
        var updates = new ConcurrentBag<ContentIndexUpdate>();
        var dirtyTargets = new ConcurrentDictionary<Guid, byte>();
        var completed = 0;
        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = Math.Clamp(_performance.ProcessorCount / 2, 1, 4)
        }, async (candidate, cancellation) =>
        {
            try
            {
                var info = new FileInfo(candidate.FullPath);
                info.Refresh();
                var indexedModified = candidate.ModifiedAt?.UtcDateTime;
                if (!info.Exists || info.Length != candidate.Size ||
                    (indexedModified is not null && info.LastWriteTimeUtc != indexedModified.Value))
                {
                    var target = FindTarget(candidate.FullPath);
                    if (target is not null) dirtyTargets.TryAdd(target.Id, 0);
                    return;
                }
                var extracted = await _extractor.ExtractAsync(candidate.FullPath, cancellation).ConfigureAwait(false);
                info.Refresh();
                if (!info.Exists || info.Length != candidate.Size ||
                    (indexedModified is not null && info.LastWriteTimeUtc != indexedModified.Value))
                {
                    var target = FindTarget(candidate.FullPath);
                    if (target is not null) dirtyTargets.TryAdd(target.Id, 0);
                    return;
                }
                updates.Add(new ContentIndexUpdate(candidate.FileId, candidate.ModifiedAt, extracted?.Text ?? string.Empty, null));
            }
            catch (Exception ex) when (PortableFileSystemScanner.IsRecoverable(ex) || ex is InvalidDataException or XmlException)
            {
                updates.Add(new ContentIndexUpdate(candidate.FileId, candidate.ModifiedAt, null, ex.Message));
            }
            var count = Interlocked.Increment(ref completed);
            if ((count & 0xf) == 0) Publish(BackgroundActivity.IndexingContent, candidate.FullPath, count);
        }).ConfigureAwait(false);

        await _store.UpdateExtractedContentsAsync(updates.ToArray(), token).ConfigureAwait(false);
        foreach (var targetId in dirtyTargets.Keys) DebounceScan(targetId, TimeSpan.FromMilliseconds(250));
        Publish(BackgroundActivity.IndexingContent, completed: completed);
        if (candidates.Count == batchSize)
            _ = ScheduleAsync(job, 2, TimeSpan.FromMilliseconds(200), token);
        else
            Publish(BackgroundActivity.Watching);
    }

    private async Task RunMaintenanceAsync(CancellationToken token)
    {
        Publish(BackgroundActivity.MaintainingIndex);
        await _store.MaintainIndexAsync(token).ConfigureAwait(false);
        Publish(BackgroundActivity.Watching);
    }

    private async Task RunPeriodicAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(_options.PeriodicInterval);
        var ticks = 0;
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            Enqueue(new BackgroundJob(BackgroundJobKind.Content, _sessionId), 2);
            Enqueue(new BackgroundJob(BackgroundJobKind.Maintenance, _sessionId), 3);
            if (++ticks % 12 != 0) continue;
            ScanTarget[] targets;
            lock (_sync) targets = _targets.Values.ToArray();
            foreach (var target in targets)
                Enqueue(new BackgroundJob(BackgroundJobKind.Scan, _sessionId, target.Id), 3);
        }
    }

    private ScanTarget? FindTarget(string path)
    {
        lock (_sync)
            return _targets.Values.Where(target => IsInside(path, target.RootPath))
                .OrderByDescending(target => target.RootPath.Length).FirstOrDefault();
    }

    private static bool IsInside(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return fullPath.Equals(fullRoot, comparison) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison);
    }

    private void Publish(BackgroundActivity activity, string? path = null, int completed = 0, string? detail = null) =>
        StatusChanged?.Invoke(this, new BackgroundAutomationStatus(activity, path, completed, detail));

    private enum BackgroundJobKind { Scan, Content, Maintenance }
    private readonly record struct BackgroundJob(
        BackgroundJobKind Kind,
        Guid SessionId,
        Guid TargetId = default,
        Guid? ResumeFromScanId = null)
    {
        public string Key => $"{Kind}:{(Kind == BackgroundJobKind.Scan ? TargetId : SessionId)}";
    }
}
