using Microsoft.Data.Sqlite;
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
        var connections = new SqliteConnectionFactory(storage);
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
        SqliteConnection.ClearAllPools();
        await Task.Yield();
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
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
