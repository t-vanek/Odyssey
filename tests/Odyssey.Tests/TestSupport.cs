using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Infrastructure;
using Odyssey.Search;

namespace Odyssey.Tests;

internal sealed class TestEnvironment : IAsyncDisposable
{
    private TestEnvironment(string root, ApplicationStorage storage, SqliteConnectionFactory connections,
        SqliteOdysseyStore store, SqliteSearchService search)
    {
        Root = root; Storage = storage; Connections = connections; Store = store; Search = search;
    }

    public string Root { get; }
    public ApplicationStorage Storage { get; }
    public SqliteConnectionFactory Connections { get; }
    public SqliteOdysseyStore Store { get; }
    public SqliteSearchService Search { get; }

    public static async Task<TestEnvironment> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), $"odyssey-tests-{Guid.NewGuid():N}");
        var storage = new ApplicationStorage(Path.Combine(root, "app"));
        var connections = new SqliteConnectionFactory(storage, pooling: false);
        var store = new SqliteOdysseyStore(connections, NullLogger<SqliteOdysseyStore>.Instance);
        var search = new SqliteSearchService(connections, NullLogger<SqliteSearchService>.Instance);
        await store.InitializeAsync();
        await search.InitializeAsync();
        return new TestEnvironment(root, storage, connections, store, search);
    }

    public async Task<(RescueSession Session, ScanTarget Target, ScanSession Scan)> CreateInvestigationAsync(string targetPath)
    {
        Directory.CreateDirectory(targetPath);
        var session = await Store.SaveSessionAsync(new RescueSession
        {
            Id = Guid.NewGuid(),
            Name = "Test rescue",
            Mode = SessionMode.ForensicReadOnly,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var target = await Store.SaveTargetAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RootPath = targetPath
        });
        var scan = new ScanSession
        {
            Id = Guid.NewGuid(),
            TargetId = target.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanStatus.Running
        };
        await Store.StartScanAsync(scan);
        return (session, target, scan);
    }

    public async ValueTask DisposeAsync()
    {
        if (!Directory.Exists(Root)) return;

        const int attempts = 8;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.Delete(Root, true);
                return;
            }
            catch (Exception exception) when (attempt < attempts
                                               && exception is IOException or UnauthorizedAccessException)
            {
                // Windows can keep a just-disposed SQLite or scanner handle alive for a short time.
                // Cleanup is test infrastructure, so wait asynchronously instead of making the
                // product tests flaky or hiding a persistent lock after the final attempt.
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt));
            }
        }
    }
}

internal static class TestWait
{
    public static async Task<bool> UntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan? pollingInterval = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var interval = pollingInterval ?? TimeSpan.FromMilliseconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(interval);
        }

        return condition();
    }
}

internal static class TestEntries
{
    public static FileEntry File(Guid targetId, string path, long size = 100, DateTimeOffset? modified = null) => new()
    {
        TargetId = targetId,
        Name = Path.GetFileName(path),
        FullPath = Path.GetFullPath(path),
        ParentPath = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty,
        Extension = Path.GetExtension(path).ToLowerInvariant(),
        Type = FileEntryType.File,
        Size = size,
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
        ModifiedAt = modified ?? DateTimeOffset.UtcNow.AddDays(-1),
        Category = new ExtensionFileClassifier().Classify(Path.GetExtension(path), FileEntryType.File)
    };
}

internal sealed class NullBackgroundAutomationService : IBackgroundAutomationService
{
    public event EventHandler<BackgroundAutomationStatus>? StatusChanged { add { } remove { } }
    public Task StartAsync(Guid sessionId, IReadOnlyCollection<ScanTarget> targets, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void UpdateTargets(IReadOnlyCollection<ScanTarget> targets) { }
    public void NotifyTargetScanned(ScanTarget target) { }
    public void NotifyUserActivity() { }
    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class DisabledOcrCapability : IOcrCapability
{
    public bool IsAvailable => false;
    public IReadOnlyCollection<string> Languages => Array.Empty<string>();
    public string? UnavailableReason => "Unavailable in test.";
}
