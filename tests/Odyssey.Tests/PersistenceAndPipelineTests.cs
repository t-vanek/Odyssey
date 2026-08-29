using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class PersistenceAndPipelineTests
{
    [Fact]
    public async Task InterruptedScan_CheckpointIsRecoveredOnce_WithLastDurableProgress()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var created = await environment.CreateInvestigationAsync(Path.Combine(environment.Root, "interrupted"));
        var checkpoint = new ScanProgress(
            FilesDiscovered: 127,
            DirectoriesDiscovered: 14,
            EntriesIndexed: 120,
            BytesObserved: 4_096,
            Errors: 2,
            Elapsed: TimeSpan.FromSeconds(3),
            CurrentPath: Path.Combine(created.Target.RootPath, "work"));
        await environment.Store.SaveScanCheckpointAsync(created.Scan.Id, checkpoint);

        var recovered = await environment.Store.RecoverInterruptedScansAsync(created.Session.Id);
        var recoveredAgain = await environment.Store.RecoverInterruptedScansAsync(created.Session.Id);

        var item = Assert.Single(recovered);
        Assert.Equal(created.Scan.Id, item.ScanId);
        Assert.Equal(created.Target.Id, item.TargetId);
        Assert.Equal(checkpoint, item.Checkpoint);
        Assert.Empty(recoveredAgain);
    }

    [Fact]
    public async Task SessionsTargetsAndScanSessions_Persist()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "target");
        var created = await environment.CreateInvestigationAsync(targetPath);
        await environment.Store.CompleteScanAsync(created.Scan.Id, ScanStatus.Completed, DateTimeOffset.UtcNow);

        var sessions = await environment.Store.GetSessionsAsync();
        var targets = await environment.Store.GetTargetsAsync(created.Session.Id);

        Assert.Equal(created.Session, Assert.Single(sessions));
        Assert.Equal(Path.GetFullPath(targetPath), Assert.Single(targets).RootPath);
        Assert.True(await environment.Store.HasCompletedScanAsync(created.Target.Id));
    }

    [Fact]
    public async Task BatchUpsert_IsIdempotent_UpdatesMetadata_AndMarksMissingHistory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "target");
        var first = await environment.CreateInvestigationAsync(targetPath);
        var aPath = Path.Combine(targetPath, "a.txt");
        var bPath = Path.Combine(targetPath, "b.txt");
        await environment.Store.UpsertEntriesAsync(first.Scan.Id,
            [TestEntries.File(first.Target.Id, aPath, 10), TestEntries.File(first.Target.Id, bPath, 20)]);
        await environment.Store.CompleteScanAsync(first.Scan.Id, ScanStatus.Completed, DateTimeOffset.UtcNow);
        await environment.Store.MarkMissingAsync(first.Target.Id, first.Scan.Id);

        var secondScan = new ScanSession { Id = Guid.NewGuid(), TargetId = first.Target.Id, StartedAt = DateTimeOffset.UtcNow, Status = ScanStatus.Running };
        await environment.Store.StartScanAsync(secondScan);
        await environment.Store.UpsertEntriesAsync(secondScan.Id, [TestEntries.File(first.Target.Id, aPath, 99, DateTimeOffset.UtcNow)]);
        await environment.Store.CompleteScanAsync(secondScan.Id, ScanStatus.Completed, DateTimeOffset.UtcNow);
        await environment.Store.MarkMissingAsync(first.Target.Id, secondScan.Id);

        var results = await environment.Search.SearchAsync(new SearchRequest { SessionId = first.Session.Id }, CancellationToken.None);
        Assert.Equal(2, results.Results.Count);
        Assert.Equal(99, results.Results.Single(x => x.Name == "a.txt").Size);
        Assert.True(results.Results.Single(x => x.Name == "b.txt").IsMissing);
    }

    [Fact]
    public async Task RealPipeline_IndexesRecursively_AndRescanDoesNotDuplicate()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "scan-target");
        Directory.CreateDirectory(Path.Combine(targetPath, "docs"));
        await File.WriteAllTextAsync(Path.Combine(targetPath, "docs", "Contract_Novak_2021.docx"), "contract");
        var session = await environment.Store.SaveSessionAsync(new RescueSession
        {
            Id = Guid.NewGuid(),
            Name = "Pipeline",
            Mode = SessionMode.ForensicReadOnly,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var target = await environment.Store.SaveTargetAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RootPath = targetPath
        });
        var coordinator = new ScanCoordinator(new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store, new ScanPipelineOptions(2, 8, 2), NullLogger<ScanCoordinator>.Instance);

        var first = await coordinator.ScanAsync(target, null, CancellationToken.None);
        var second = await coordinator.ScanAsync(target, null, CancellationToken.None);
        var results = await environment.Search.SearchAsync(new SearchRequest { Query = "novak", SessionId = session.Id }, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, first.Status);
        Assert.Equal(ScanStatus.Completed, second.Status);
        Assert.Single(results.Results);
    }

    [Theory]
    [InlineData(0.51, SearchConfidenceBand.Possible)]
    [InlineData(0.52, SearchConfidenceBand.Medium)]
    [InlineData(0.77, SearchConfidenceBand.Medium)]
    [InlineData(0.78, SearchConfidenceBand.High)]
    public void SearchConfidence_UsesStableEvidenceBands(double score, SearchConfidenceBand expected)
    {
        var result = new SearchResult
        {
            FileId = 1,
            Name = "work.txt",
            FullPath = "/work.txt",
            Type = FileEntryType.File,
            Category = FileCategory.Documents,
            Score = score,
            TargetId = Guid.NewGuid()
        };

        Assert.Equal(expected, result.Confidence);
    }

    [Fact]
    public async Task CancelledScan_DoesNotMarkPreviouslyIndexedEntriesMissing()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "cancel-target");
        var created = await environment.CreateInvestigationAsync(targetPath);
        var oldPath = Path.Combine(targetPath, "old.txt");
        await environment.Store.UpsertEntriesAsync(created.Scan.Id, [TestEntries.File(created.Target.Id, oldPath)]);
        await environment.Store.CompleteScanAsync(created.Scan.Id, ScanStatus.Completed, DateTimeOffset.UtcNow);

        var coordinator = new ScanCoordinator(new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), environment.Store, new ScanPipelineOptions(), NullLogger<ScanCoordinator>.Instance);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var scan = await coordinator.ScanAsync(created.Target, null, cancellation.Token);
        var result = await environment.Search.SearchAsync(new SearchRequest { Query = "old", SessionId = created.Session.Id }, CancellationToken.None);

        Assert.Equal(ScanStatus.Cancelled, scan.Status);
        Assert.False(Assert.Single(result.Results).IsMissing);
    }

    [Fact]
    public async Task CompletedScan_DoesNotMarkEntriesBelowAnUnreadablePathMissing()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "partially-readable");
        var created = await environment.CreateInvestigationAsync(targetPath);
        var protectedDirectory = Path.Combine(targetPath, "locked");
        var protectedPath = Path.Combine(protectedDirectory, "known.txt");
        var missingPath = Path.Combine(targetPath, "actually-gone.txt");
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
        [
            TestEntries.File(created.Target.Id, protectedPath),
            TestEntries.File(created.Target.Id, missingPath)
        ]);

        var secondScan = new ScanSession
        {
            Id = Guid.NewGuid(),
            TargetId = created.Target.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Status = ScanStatus.Running
        };
        await environment.Store.StartScanAsync(secondScan);
        await environment.Store.AddScanErrorsAsync(
            [new ScanError(secondScan.Id, protectedDirectory, "Access denied", DateTimeOffset.UtcNow)]);
        await environment.Store.CompleteScanAsync(secondScan.Id, ScanStatus.Completed, DateTimeOffset.UtcNow);
        await environment.Store.MarkMissingAsync(created.Target.Id, secondScan.Id);

        var results = await environment.Search.SearchAsync(
            new SearchRequest { SessionId = created.Session.Id }, CancellationToken.None);
        Assert.False(results.Results.Single(item => item.Name == "known.txt").IsMissing);
        Assert.True(results.Results.Single(item => item.Name == "actually-gone.txt").IsMissing);
    }
}
