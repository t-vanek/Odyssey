using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Infrastructure;
using Odyssey.Search;

namespace Odyssey.Tests;

[Trait("Category", "Stability")]
public sealed class StabilityScenarioTests
{
    private static readonly SystemPerformanceProfile Performance =
        SystemPerformanceProfile.ForHardware(8, 8L * 1024 * 1024 * 1024);

    [Fact]
    public async Task RescueJourney_ContentRemainsSearchableAndUnchangedAfterServiceRestart()
    {
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "customer-drive");
        var documentPath = Path.Combine(targetPath, "Projects", "Budget notes.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
        await File.WriteAllTextAsync(documentPath,
            "Winter campaign. The remembered approval phrase is aurora-lantern-4821.");
        var originalHash = await HashAsync(documentPath);

        var firstRun = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(firstRun.Store, targetPath);
        var scan = await firstRun.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None);
        Assert.Equal(ScanStatus.Completed, scan.Status);

        var backgroundOptions = new BackgroundAutomationOptions(
            TimeSpan.FromMilliseconds(25), TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));
        await using (var background = new BackgroundAutomationService(
                         firstRun.Store, firstRun.Scanner, new LocalContentExtractor(), Performance,
                         NullLogger<BackgroundAutomationService>.Instance, backgroundOptions))
        {
            await background.StartAsync(investigation.Session.Id, [investigation.Target]);
            background.NotifyTargetScanned(investigation.Target);
            await WaitUntilAsync(async () =>
            {
                var response = await firstRun.Search.SearchAsync(new SearchRequest
                {
                    Query = "aurora lantern 4821",
                    SessionId = investigation.Session.Id
                }, CancellationToken.None);
                return response.Results.Any(item => item.FullPath == documentPath);
            }, TimeSpan.FromSeconds(8));
            await background.StopAsync();
        }

        // Simulate a new application process by constructing every persistence,
        // search and scan service again over the same application-data directory.
        var secondRun = await OpenRuntimeAsync(workspace.Storage);
        var sessions = await secondRun.Store.GetSessionsAsync();
        var targets = await secondRun.Store.GetTargetsAsync(investigation.Session.Id);
        var byName = await secondRun.Search.SearchAsync(new SearchRequest
        {
            Query = "budget notes",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);
        var byRememberedContent = await secondRun.Search.SearchAsync(new SearchRequest
        {
            Query = "aurora lantern 4821",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);

        Assert.Contains(sessions, item => item.Id == investigation.Session.Id);
        Assert.Contains(targets, item => item.Id == investigation.Target.Id);
        Assert.Contains(byName.Results, item => item.FullPath == documentPath);
        Assert.Contains(byRememberedContent.Results, item =>
            item.FullPath == documentPath && item.MatchEvidence.HasFlag(SearchMatchEvidence.Content));
        Assert.Equal(originalHash, await HashAsync(documentPath));
    }

    [Fact]
    public async Task LargeCatalogue_AllPagesAreUniqueCompleteAndStable()
    {
        const int fileCount = 1_250;
        const int pageSize = 137;
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "large-catalogue");
        for (var index = 0; index < fileCount; index++)
        {
            var directory = Path.Combine(targetPath, $"batch-{index % 25:D2}");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                Path.Combine(directory, $"ScenarioDataset_{index:D4}.txt"),
                $"Scenario stability record {index:D4}");
        }

        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        var scan = await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None);
        Assert.Equal(ScanStatus.Completed, scan.Status);

        var firstPass = await ReadAllPagesAsync(
            runtime.Search, investigation.Session.Id, "scenariodataset", pageSize);
        var secondPass = await ReadAllPagesAsync(
            runtime.Search, investigation.Session.Id, "scenariodataset", pageSize);

        Assert.Equal(fileCount, firstPass.Count);
        Assert.Equal(fileCount, firstPass.Select(item => item.FileId).Distinct().Count());
        Assert.Equal(firstPass.Select(item => item.FileId), secondPass.Select(item => item.FileId));
        Assert.All(firstPass, item =>
        {
            Assert.False(item.IsMissing);
            Assert.True(File.Exists(item.FullPath));
        });
    }

    [Fact]
    public async Task ConcurrentSearchDuringRescan_StaysConsistentAndConvergesToDiskState()
    {
        const int initialFiles = 500;
        const int deletedFiles = 25;
        const int addedFiles = 40;
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "changing-catalogue");
        Directory.CreateDirectory(targetPath);
        for (var index = 0; index < initialFiles; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"ConcurrentScenario_{index:D4}.txt"), $"version one {index}");

        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);

        for (var index = 0; index < deletedFiles; index++)
            File.Delete(Path.Combine(targetPath, $"ConcurrentScenario_{index:D4}.txt"));
        for (var index = initialFiles; index < initialFiles + addedFiles; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"ConcurrentScenario_{index:D4}.txt"), $"new record {index}");
        for (var index = deletedFiles; index < deletedFiles + 25; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"ConcurrentScenario_{index:D4}.txt"), $"version two {index}");

        var rescan = runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None);
        var readers = Enumerable.Range(0, 8).Select(reader => Task.Run(async () =>
        {
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var response = await runtime.Search.SearchAsync(new SearchRequest
                {
                    Query = "concurrentscenario",
                    SessionId = investigation.Session.Id,
                    Limit = 80,
                    Offset = (reader * 17 + iteration * 11) % 300
                }, CancellationToken.None);
                Assert.Equal(response.Results.Count, response.Results.Select(item => item.FileId).Distinct().Count());
                Assert.All(response.Results, item => Assert.Equal(investigation.Target.Id, item.TargetId));
                await Task.Yield();
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(rescan));
        var rescanResult = await rescan;
        Assert.Equal(ScanStatus.Completed, rescanResult.Status);
        var final = await ReadAllPagesAsync(
            runtime.Search, investigation.Session.Id, "concurrentscenario", 250);

        Assert.Equal(initialFiles + addedFiles, final.Count);
        Assert.Equal(deletedFiles, final.Count(item => item.IsMissing));
        Assert.Equal(initialFiles - deletedFiles + addedFiles, final.Count(item => !item.IsMissing));
        Assert.All(final.Where(item => !item.IsMissing), item => Assert.True(File.Exists(item.FullPath)));
    }

    [Fact]
    public async Task TemporarilyUnavailableDrive_DoesNotTurnKnownWorkIntoFalseDeletion()
    {
        const int fileCount = 15;
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "removable-drive");
        Directory.CreateDirectory(targetPath);
        for (var index = 0; index < fileCount; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"UnavailableScenario_{index:D2}.txt"), $"important work {index}");

        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);

        var disconnectedPath = Path.Combine(workspace.Root, "drive-is-disconnected");
        Directory.Move(targetPath, disconnectedPath);
        var unavailableScan = await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None);
        var results = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "unavailablescenario",
            SessionId = investigation.Session.Id,
            Limit = 100
        }, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, unavailableScan.Status);
        Assert.Equal(fileCount, results.Results.Count);
        Assert.All(results.Results, item => Assert.False(item.IsMissing));
    }

    [Fact]
    public async Task ManagedCopyRenameAndUndo_KeepFilesystemAndSearchIndexTogether()
    {
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "managed-files");
        var sourceDirectory = Path.Combine(targetPath, "source");
        var destinationDirectory = Path.Combine(targetPath, "destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "Important_work.txt");
        await File.WriteAllTextAsync(source, "Odyssey integration scenario");

        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);
        var operations = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };

        var copy = await operations.CopyAsync(source, destinationDirectory);
        await runtime.Store.ApplyFileOperationsAsync([copy], [investigation.Target]);
        var copiedPath = copy.DestinationPath!;
        Assert.True(File.Exists(copiedPath));
        Assert.Equal(2, (await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "important work",
            SessionId = investigation.Session.Id
        }, CancellationToken.None)).Results.Count(item => !item.IsMissing));

        var rename = await operations.RenameAsync(copiedPath, "Recovered_final.txt");
        await runtime.Store.ApplyFileOperationsAsync([rename], [investigation.Target]);
        var renamedPath = rename.DestinationPath!;
        Assert.True(File.Exists(renamedPath));
        Assert.Contains((await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "recovered final",
            SessionId = investigation.Session.Id
        }, CancellationToken.None)).Results, item => item.FullPath == renamedPath && !item.IsMissing);

        var undo = Assert.IsType<FileOperationRecord>(await operations.UndoLastAsync());
        await runtime.Store.ApplyFileOperationsAsync([undo], [investigation.Target]);
        Assert.True(File.Exists(copiedPath));
        Assert.False(File.Exists(renamedPath));
        Assert.Contains((await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "important work",
            SessionId = investigation.Session.Id
        }, CancellationToken.None)).Results, item => item.FullPath == copiedPath && !item.IsMissing);

        operations.AccessMode = FileAccessMode.ReadOnly;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operations.RenameAsync(copiedPath, "must-not-change.txt"));
        Assert.True(File.Exists(copiedPath));
    }

    private static async Task<ScenarioRuntime> OpenRuntimeAsync(ApplicationStorage storage)
    {
        var connections = new SqliteConnectionFactory(storage, Performance, pooling: false);
        var store = new SqliteOdysseyStore(connections, NullLogger<SqliteOdysseyStore>.Instance);
        var search = new SqliteSearchService(connections, NullLogger<SqliteSearchService>.Instance);
        await store.InitializeAsync();
        await search.InitializeAsync();
        var scanner = new ScanCoordinator(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()),
            new ExtensionFileClassifier(), store,
            new ScanPipelineOptions(4, 256, 96, Performance),
            NullLogger<ScanCoordinator>.Instance);
        return new ScenarioRuntime(store, search, scanner);
    }

    private static async Task<(RescueSession Session, ScanTarget Target)> CreateInvestigationAsync(
        IOdysseyStore store,
        string targetPath)
    {
        var session = await store.SaveSessionAsync(new RescueSession
        {
            Id = Guid.NewGuid(),
            Name = "Real stability scenario",
            Mode = SessionMode.ForensicReadOnly,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var target = await store.SaveTargetAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RootPath = targetPath
        });
        return (session, target);
    }

    private static async Task<List<SearchResult>> ReadAllPagesAsync(
        ISearchService search,
        Guid sessionId,
        string query,
        int pageSize)
    {
        var results = new List<SearchResult>();
        while (true)
        {
            var page = await search.SearchAsync(new SearchRequest
            {
                Query = query,
                SessionId = sessionId,
                Limit = pageSize,
                Offset = results.Count
            }, CancellationToken.None);
            results.AddRange(page.Results);
            if (!page.HasMore) return results;
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"The scenario did not reach its expected state within {timeout}.");
    }

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private sealed record ScenarioRuntime(
        SqliteOdysseyStore Store,
        SqliteSearchService Search,
        ScanCoordinator Scanner);

    private sealed class ScenarioWorkspace : IAsyncDisposable
    {
        public ScenarioWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), $"odyssey-stability-{Guid.NewGuid():N}");
            Storage = new ApplicationStorage(Path.Combine(Root, "application-data"));
        }

        public string Root { get; }
        public ApplicationStorage Storage { get; }

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
