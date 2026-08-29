using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.IO.Compression;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Infrastructure;
using Odyssey.Search;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using S = DocumentFormat.OpenXml.Spreadsheet;
using W = DocumentFormat.OpenXml.Wordprocessing;

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

    [Fact]
    public async Task CancelledLargeCopy_LeavesSourceIntactAndNoPartialDestination()
    {
        const int fileSize = 24 * 1024 * 1024;
        await using var workspace = new ScenarioWorkspace();
        var sourceDirectory = Path.Combine(workspace.Root, "large-copy-source");
        var destinationDirectory = Path.Combine(workspace.Root, "large-copy-destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "valuable-work.bin");
        await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(fileSize);
        var originalHash = await HashAsync(source);
        var operations = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress<FileOperationProgress>(item =>
        {
            if (item.BytesCompleted >= 1024 * 1024) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operations.TransferAsync(
            new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = source,
                DestinationDirectory = destinationDirectory,
                VerifyAfterCopy = true
            }, progress, cancellation.Token));

        Assert.Equal(originalHash, await HashAsync(source));
        Assert.False(File.Exists(Path.Combine(destinationDirectory, Path.GetFileName(source))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory));
        Assert.Empty(operations.History);
    }

    [Fact]
    public async Task UnicodeNamesAndDirectoryLink_AreIndexedWithoutFollowingALoop()
    {
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "unicode-catalogue");
        var nested = Path.Combine(targetPath, "Zakázky – Brno 📁", "Přílohy");
        Directory.CreateDirectory(nested);
        var documentPath = Path.Combine(nested, "Žluťoučký_kůň_účtenka_2026.txt");
        await File.WriteAllTextAsync(documentPath, "Příliš žluťoučký kůň úpěl ďábelské ódy.");
        var linkPath = Path.Combine(nested, "loop-back");
        var linkCreated = TryCreateDirectoryLink(linkPath, targetPath);

        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var scan = await runtime.Scanner.ScanAsync(investigation.Target, null, timeout.Token);
        var result = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "účtenka 2026",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, scan.Status);
        Assert.Contains(result.Results, item => item.FullPath == documentPath && !item.IsMissing);
        var catalogue = await ReadAllPagesAsync(runtime.Search, investigation.Session.Id, string.Empty, 100);
        Assert.Equal(catalogue.Count, catalogue.Select(item => item.FullPath).Distinct(PathComparer).Count());
        if (linkCreated)
        {
            Assert.Contains(catalogue, item => item.FullPath == linkPath && item.Type == FileEntryType.Directory);
            Assert.DoesNotContain(catalogue, item =>
                item.FullPath.StartsWith(linkPath + Path.DirectorySeparatorChar, PathComparison));
        }
    }

    [Fact]
    public async Task ConcurrentScansOfSameTarget_AreSerializedAndRemainIdempotent()
    {
        const int fileCount = 160;
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "serialized-scans");
        Directory.CreateDirectory(targetPath);
        for (var index = 0; index < fileCount; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"SerializedScenario_{index:D3}.txt"), $"record {index}");

        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        var observedScanner = new ConcurrencyObservingScanner(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()));
        var coordinator = new ScanCoordinator(
            observedScanner, new ExtensionFileClassifier(), runtime.Store,
            new ScanPipelineOptions(4, 128, 48, Performance),
            NullLogger<ScanCoordinator>.Instance);

        var first = coordinator.ScanAsync(investigation.Target, null, CancellationToken.None);
        await observedScanner.FirstEnumerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = coordinator.ScanAsync(investigation.Target, null, CancellationToken.None);
        var completed = await Task.WhenAll(first, second);
        var results = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "serializedscenario",
            SessionId = investigation.Session.Id,
            Limit = 500
        }, CancellationToken.None);

        Assert.All(completed, scan => Assert.Equal(ScanStatus.Completed, scan.Status));
        Assert.Equal(1, observedScanner.MaximumConcurrency);
        Assert.Equal(fileCount, results.Results.Count);
        Assert.Equal(fileCount, results.Results.Select(item => item.FileId).Distinct().Count());
    }

    [Fact]
    public async Task ShortSqliteWriteLock_DelaysWriteButKeepsSearchResponsive()
    {
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "database-contention");
        Directory.CreateDirectory(targetPath);
        await File.WriteAllTextAsync(Path.Combine(targetPath, "DatabaseScenario.txt"), "known work");
        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);

        await using var blocker = await runtime.Connections.OpenAsync();
        await using var begin = blocker.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        await begin.ExecuteNonQueryAsync();
        var newSession = new RescueSession
        {
            Id = Guid.NewGuid(),
            Name = "Wait for short lock",
            Mode = SessionMode.ForensicReadOnly,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var pendingWrite = Task.Run(() => runtime.Store.SaveSessionAsync(newSession));

        await Task.Delay(150);
        Assert.False(pendingWrite.IsCompleted);
        var search = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "databasescenario",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);
        Assert.Single(search.Results);
        await using var commit = blocker.CreateCommand();
        commit.CommandText = "COMMIT;";
        await commit.ExecuteNonQueryAsync();

        var saved = await pendingWrite.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(newSession.Id, saved.Id);
        Assert.Contains(await runtime.Store.GetSessionsAsync(), item => item.Id == newSession.Id);
    }

    [Fact]
    public async Task TransferQueue_RestartsPersistedRunningJobAndCompletesASecondAttempt()
    {
        await using var workspace = new ScenarioWorkspace();
        var sourceDirectory = Path.Combine(workspace.Root, "queue-source");
        var destinationDirectory = Path.Combine(workspace.Root, "queue-destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "InterruptedQueueScenario.bin");
        await using (var stream = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(4 * 1024 * 1024);
        var expectedHash = await HashAsync(source);
        var job = new FileTransferJob
        {
            Id = Guid.NewGuid(),
            Request = new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = source,
                DestinationDirectory = destinationDirectory,
                VerifyAfterCopy = true
            },
            State = FileTransferState.Running,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            BytesCompleted = 1024 * 1024,
            TotalBytes = 4 * 1024 * 1024,
            CurrentPath = source,
            Attempt = 1
        };
        var queuePath = Path.Combine(workspace.Storage.DirectoryPath, "transfer-queue.json");
        await File.WriteAllTextAsync(queuePath, JsonSerializer.Serialize(
            new[] { job }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var operations = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };

        await using var queue = new FileTransferQueueService(operations, workspace.Storage);
        await queue.InitializeAsync();
        await WaitUntilAsync(() => Task.FromResult(
                queue.Items.Single(item => item.Id == job.Id).State == FileTransferState.Completed),
            TimeSpan.FromSeconds(5));

        var completed = queue.Items.Single(item => item.Id == job.Id);
        var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
        Assert.Equal(2, completed.Attempt);
        Assert.Null(completed.Error);
        Assert.Equal(destination, completed.DestinationPath);
        Assert.Equal(expectedHash, await HashAsync(destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationDirectory, "*.odyssey-part-*"));
    }

    [Fact]
    public async Task ReplaceThenUndo_RestoresOriginalFileAndItsActiveSearchIndexEntry()
    {
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "replace-and-undo");
        var sourceDirectory = Path.Combine(targetPath, "new-version");
        var destinationDirectory = Path.Combine(targetPath, "customer-work");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "ReportScenario.txt");
        var destination = Path.Combine(destinationDirectory, "ReportScenario.txt");
        await File.WriteAllTextAsync(source, "replacement");
        await File.WriteAllTextAsync(destination, "the customer's substantially longer original work");
        var originalHash = await HashAsync(destination);
        var originalLength = new FileInfo(destination).Length;
        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);
        var operations = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };

        var replacement = await operations.TransferAsync(new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = source,
            DestinationDirectory = destinationDirectory,
            ConflictPolicy = FileConflictPolicy.Replace,
            VerifyAfterCopy = true
        });
        await runtime.Store.ApplyFileOperationsAsync([replacement.Operation!], [investigation.Target]);
        Assert.Equal("replacement", await File.ReadAllTextAsync(destination));

        var undo = Assert.IsType<FileOperationRecord>(await operations.UndoLastAsync());
        await runtime.Store.ApplyFileOperationsAsync([undo], [investigation.Target]);
        var results = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "reportscenario",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);
        var restored = results.Results.Single(item => PathComparer.Equals(item.FullPath, destination));

        Assert.Equal(originalHash, await HashAsync(destination));
        Assert.False(restored.IsMissing);
        Assert.Equal(originalLength, restored.Size);

        var movement = await operations.TransferAsync(new FileTransferRequest
        {
            Kind = FileOperationKind.Move,
            SourcePath = source,
            DestinationDirectory = destinationDirectory,
            ConflictPolicy = FileConflictPolicy.Replace,
            VerifyAfterCopy = true
        });
        await runtime.Store.ApplyFileOperationsAsync([movement.Operation!], [investigation.Target]);
        Assert.False(File.Exists(source));
        Assert.Equal("replacement", await File.ReadAllTextAsync(destination));

        var undoMove = Assert.IsType<FileOperationRecord>(await operations.UndoLastAsync());
        await runtime.Store.ApplyFileOperationsAsync([undoMove], [investigation.Target]);
        var afterMoveUndo = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "reportscenario",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);

        Assert.True(File.Exists(source));
        Assert.Equal(originalHash, await HashAsync(destination));
        Assert.Equal(2, afterMoveUndo.Results.Count(item => !item.IsMissing));
        Assert.Contains(afterMoveUndo.Results, item =>
            PathComparer.Equals(item.FullPath, destination) && !item.IsMissing && item.Size == originalLength);
    }

    [Fact]
    public async Task BurstOfFilesystemChanges_IsDebouncedAndConvergesWithoutDuplicates()
    {
        const int fileCount = 120;
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "watcher-burst");
        Directory.CreateDirectory(targetPath);
        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);
        var options = new BackgroundAutomationOptions(
            TimeSpan.FromMilliseconds(250), TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));
        await using var background = new BackgroundAutomationService(
            runtime.Store, runtime.Scanner, new LocalContentExtractor(), Performance,
            NullLogger<BackgroundAutomationService>.Instance, options);
        var verificationScans = 0;
        background.StatusChanged += (_, status) =>
        {
            if (status.Activity == BackgroundActivity.ScanningChanges)
                Interlocked.Increment(ref verificationScans);
        };
        await background.StartAsync(investigation.Session.Id, [investigation.Target]);
        background.NotifyTargetScanned(investigation.Target);

        for (var index = 0; index < fileCount; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"BurstScenario_{index:D3}.txt"), $"initial {index}");
        for (var index = 0; index < 20; index++)
        {
            var original = Path.Combine(targetPath, $"BurstScenario_{index:D3}.txt");
            var renamed = Path.Combine(targetPath, $"BurstScenarioRenamed_{index:D3}.txt");
            File.Move(original, renamed);
            await File.AppendAllTextAsync(renamed, " updated");
        }

        await WaitUntilAsync(async () =>
        {
            var response = await runtime.Search.SearchAsync(new SearchRequest
            {
                Query = "burstscenario",
                SessionId = investigation.Session.Id,
                Limit = 500
            }, CancellationToken.None);
            return response.Results.Count(item => !item.IsMissing) == fileCount;
        }, TimeSpan.FromSeconds(8));
        await Task.Delay(400);
        var final = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "burstscenario",
            SessionId = investigation.Session.Id,
            Limit = 500
        }, CancellationToken.None);

        Assert.InRange(Volatile.Read(ref verificationScans), 1, 4);
        Assert.Equal(fileCount, final.Results.Count(item => !item.IsMissing));
        Assert.Equal(fileCount, final.Results.Where(item => !item.IsMissing)
            .Select(item => item.FullPath).Distinct(PathComparer).Count());
    }

    [Fact]
    public async Task CancellationMidScan_PreservesKnownResultsAndNextScanConverges()
    {
        const int knownFiles = 80;
        const int addedFiles = 320;
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "mid-scan-cancellation");
        Directory.CreateDirectory(targetPath);
        for (var index = 0; index < knownFiles; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"MidScanScenario_{index:D4}.txt"), $"known work {index}");

        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);
        for (var index = knownFiles; index < knownFiles + addedFiles; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"MidScanScenario_{index:D4}.txt"), $"new work {index}");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancellingScanner = new CancellingScanner(
            new PortableFileSystemScanner(new ConfigurableExclusionPolicy()), cancellation, 180);
        var interruptedCoordinator = new ScanCoordinator(
            cancellingScanner, new ExtensionFileClassifier(), runtime.Store,
            new ScanPipelineOptions(2, 64, 32, Performance),
            NullLogger<ScanCoordinator>.Instance);
        var interrupted = await interruptedCoordinator.ScanAsync(
            investigation.Target, null, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));
        var afterCancellation = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "midscanscenario",
            SessionId = investigation.Session.Id,
            Limit = 1_000
        }, CancellationToken.None);

        Assert.Equal(ScanStatus.Cancelled, interrupted.Status);
        Assert.True(afterCancellation.Results.Count(item => !item.IsMissing) >= knownFiles);
        Assert.All(afterCancellation.Results.Where(item => item.Name.Contains("MidScanScenario_", StringComparison.Ordinal)),
            item => Assert.False(item.IsMissing));

        var recovered = await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None);
        var final = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "midscanscenario",
            SessionId = investigation.Session.Id,
            Limit = 1_000
        }, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, recovered.Status);
        Assert.Equal(knownFiles + addedFiles, final.Results.Count(item => !item.IsMissing));
        Assert.Equal(knownFiles + addedFiles, final.Results.Where(item => !item.IsMissing)
            .Select(item => item.FullPath).Distinct(PathComparer).Count());
    }

    [Fact]
    public async Task SqliteFull_FailsCalmlyPreservesOldIndexAndRecoversAfterSpaceReturns()
    {
        const int addedFiles = 900;
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "full-database");
        Directory.CreateDirectory(targetPath);
        var preservedPath = Path.Combine(targetPath, "PreservedBeforeFull.txt");
        await File.WriteAllTextAsync(preservedPath, "customer work that must remain searchable");
        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);

        for (var index = 0; index < addedFiles; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"FullStorageScenario_{index:D4}.txt"), $"new work {index}");
        var originalMaximum = await ReadPragmaAsync(runtime, "max_page_count");
        var currentPages = await ReadPragmaAsync(runtime, "page_count");

        ScanSession constrained;
        await using (var limiter = await runtime.Connections.OpenAsync())
        {
            await ConfigureStorageLimitAsync(limiter, currentPages + 8);
            try
            {
                constrained = await runtime.Scanner.ScanAsync(
                    investigation.Target, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                await SetMaxPageCountAsync(limiter, originalMaximum);
            }
        }
        await EnableWalAsync(runtime);

        var preserved = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "preservedbeforefull",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);
        Assert.Equal(ScanStatus.Failed, constrained.Status);
        Assert.Contains(preserved.Results, item =>
            PathComparer.Equals(item.FullPath, preservedPath) && !item.IsMissing);

        var recovered = await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None);
        var final = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "fullstoragescenario",
            SessionId = investigation.Session.Id,
            Limit = 2_000
        }, CancellationToken.None);

        Assert.Equal(ScanStatus.Completed, recovered.Status);
        Assert.Equal(addedFiles, final.Results.Count(item => !item.IsMissing));
        Assert.Equal(addedFiles, final.Results.Where(item => !item.IsMissing)
            .Select(item => item.FullPath).Distinct(PathComparer).Count());
    }

    [Fact]
    public async Task RestartAfterInterruptedScan_ResumesFromCheckpointAndConverges()
    {
        const int baselineFiles = 60;
        const int addedFiles = 240;
        const int durablePartialFiles = 75;
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "process-crash-recovery");
        Directory.CreateDirectory(targetPath);
        for (var index = 0; index < baselineFiles; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"CrashRecoveryScenario_{index:D4}.txt"), $"baseline {index}");

        var firstRun = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(firstRun.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await firstRun.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);
        for (var index = baselineFiles; index < baselineFiles + addedFiles; index++)
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, $"CrashRecoveryScenario_{index:D4}.txt"), $"new work {index}");

        var interrupted = new ScanSession
        {
            Id = Guid.NewGuid(),
            TargetId = investigation.Target.Id,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            Status = ScanStatus.Running
        };
        await firstRun.Store.StartScanAsync(interrupted);
        var durablePaths = Enumerable.Range(baselineFiles, durablePartialFiles)
            .Select(index => Path.Combine(targetPath, $"CrashRecoveryScenario_{index:D4}.txt"))
            .ToArray();
        await firstRun.Store.UpsertEntriesAsync(interrupted.Id,
            durablePaths.Select(path => CreateFileEntry(investigation.Target.Id, path)).ToArray());
        var checkpoint = new ScanProgress(
            durablePartialFiles, 1, durablePartialFiles,
            durablePaths.Sum(path => new FileInfo(path).Length), 0,
            TimeSpan.FromSeconds(4), durablePaths[^1]);
        await firstRun.Store.SaveScanCheckpointAsync(interrupted.Id, checkpoint);

        var secondRun = await OpenRuntimeAsync(workspace.Storage);
        var options = new BackgroundAutomationOptions(
            TimeSpan.FromMilliseconds(25), TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(25), TimeSpan.FromHours(1));
        await using var background = new BackgroundAutomationService(
            secondRun.Store, secondRun.Scanner, new LocalContentExtractor(), Performance,
            NullLogger<BackgroundAutomationService>.Instance, options);
        await background.StartAsync(investigation.Session.Id, [investigation.Target]);
        await WaitUntilAsync(async () =>
        {
            var response = await secondRun.Search.SearchAsync(new SearchRequest
            {
                Query = "crashrecoveryscenario",
                SessionId = investigation.Session.Id,
                Limit = 1_000
            }, CancellationToken.None);
            return response.Results.Count(item => !item.IsMissing) == baselineFiles + addedFiles
                   && await CountResumedCompletedScansAsync(secondRun, interrupted.Id) == 1;
        }, TimeSpan.FromSeconds(8));
        await background.StopAsync();

        var oldStatus = await ReadScanStatusAsync(secondRun, interrupted.Id);
        var resumedScans = await CountResumedCompletedScansAsync(secondRun, interrupted.Id);
        var final = await secondRun.Search.SearchAsync(new SearchRequest
        {
            Query = "crashrecoveryscenario",
            SessionId = investigation.Session.Id,
            Limit = 1_000
        }, CancellationToken.None);

        Assert.Equal(ScanStatus.Interrupted, oldStatus);
        Assert.Equal(1, resumedScans);
        Assert.Equal(baselineFiles + addedFiles, final.Results.Count(item => !item.IsMissing));
        Assert.Equal(baselineFiles + addedFiles, final.Results.Where(item => !item.IsMissing)
            .Select(item => item.FullPath).Distinct(PathComparer).Count());
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[null]")]
    public async Task CorruptTransferQueue_IsQuarantinedAndDoesNotBlockStartup(string invalidState)
    {
        await using var workspace = new ScenarioWorkspace();
        Directory.CreateDirectory(workspace.Storage.DirectoryPath);
        var queuePath = Path.Combine(workspace.Storage.DirectoryPath, "transfer-queue.json");
        await File.WriteAllTextAsync(queuePath, invalidState);
        var operations = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };

        await using var queue = new FileTransferQueueService(operations, workspace.Storage);
        await queue.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(queue.Items);
        Assert.Equal("[]", (await File.ReadAllTextAsync(queuePath)).Trim());
        var quarantine = Assert.Single(Directory.EnumerateFiles(
            workspace.Storage.DirectoryPath, "transfer-queue.corrupt-*.json"));
        Assert.Equal(invalidState, await File.ReadAllTextAsync(quarantine));
    }

    [Fact]
    public async Task CompleteTemporaryQueueSnapshot_IsPublishedAndResumedAfterRestart()
    {
        await using var workspace = new ScenarioWorkspace();
        var sourceDirectory = Path.Combine(workspace.Root, "temporary-queue-source");
        var destinationDirectory = Path.Combine(workspace.Root, "temporary-queue-destination");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var source = Path.Combine(sourceDirectory, "RecoveredTemporaryQueueScenario.txt");
        await File.WriteAllTextAsync(source, "the newest durable queue snapshot");
        var job = new FileTransferJob
        {
            Id = Guid.NewGuid(),
            Request = new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = source,
                DestinationDirectory = destinationDirectory,
                VerifyAfterCopy = true
            },
            State = FileTransferState.Running,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10),
            Attempt = 1
        };
        Directory.CreateDirectory(workspace.Storage.DirectoryPath);
        var queuePath = Path.Combine(workspace.Storage.DirectoryPath, "transfer-queue.json");
        await File.WriteAllTextAsync(queuePath, "[]");
        await File.WriteAllTextAsync(queuePath + ".tmp", JsonSerializer.Serialize(
            new[] { job }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var operations = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };

        await using var queue = new FileTransferQueueService(operations, workspace.Storage);
        await queue.InitializeAsync();
        await WaitUntilAsync(() => Task.FromResult(
                queue.Items.Single(item => item.Id == job.Id).State == FileTransferState.Completed),
            TimeSpan.FromSeconds(5));

        var completed = Assert.Single(queue.Items);
        Assert.Equal(2, completed.Attempt);
        Assert.Equal("the newest durable queue snapshot", await File.ReadAllTextAsync(
            Path.Combine(destinationDirectory, Path.GetFileName(source))));
        Assert.False(File.Exists(queuePath + ".tmp"));
        Assert.Empty(Directory.EnumerateFiles(workspace.Storage.DirectoryPath, "transfer-queue*.corrupt-*.json"));
    }

    [Fact]
    public async Task TruncatedTemporaryQueueSnapshot_PreservesLastPublishedQueue()
    {
        await using var workspace = new ScenarioWorkspace();
        Directory.CreateDirectory(workspace.Storage.DirectoryPath);
        var queuePath = Path.Combine(workspace.Storage.DirectoryPath, "transfer-queue.json");
        var publishedJob = new FileTransferJob
        {
            Id = Guid.NewGuid(),
            Request = new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = Path.Combine(workspace.Root, "not-running-source.txt"),
                DestinationDirectory = workspace.Root
            },
            State = FileTransferState.Paused,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(queuePath, JsonSerializer.Serialize(
            new[] { publishedJob }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        const string truncatedSnapshot = "[{\"id\":\"unfinished";
        await File.WriteAllTextAsync(queuePath + ".tmp", truncatedSnapshot);
        var operations = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };

        await using var queue = new FileTransferQueueService(operations, workspace.Storage);
        await queue.InitializeAsync();

        var retained = Assert.Single(queue.Items);
        Assert.Equal(publishedJob.Id, retained.Id);
        Assert.Equal(FileTransferState.Paused, retained.State);
        var quarantine = Assert.Single(Directory.EnumerateFiles(
            workspace.Storage.DirectoryPath, "transfer-queue.tmp.corrupt-*.json"));
        Assert.Equal(truncatedSnapshot, await File.ReadAllTextAsync(quarantine));
        Assert.False(File.Exists(queuePath + ".tmp"));
    }

    [Fact]
    public async Task OwnedPartialTransferArtifact_IsRemovedAfterRestartButSimilarForeignFileSurvives()
    {
        await using var workspace = new ScenarioWorkspace();
        var destinationDirectory = Path.Combine(workspace.Root, "artifact-recovery");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "CustomerWork.txt");
        var ownedId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        var ownedArtifact = destination + $".odyssey-part-{ownedId:N}";
        var similarForeignFile = destination + $".odyssey-part-{foreignId:N}";
        await File.WriteAllTextAsync(ownedArtifact, "incomplete Odyssey bytes");
        await File.WriteAllTextAsync(similarForeignFile, "must not be inferred as owned");
        var journalPath = Path.Combine(workspace.Storage.DirectoryPath, "transfer-artifacts.json");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new[]
        {
            new
            {
                id = ownedId,
                artifactPath = ownedArtifact,
                destinationPath = destination,
                createdAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                ownerProcessId = int.MaxValue
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        _ = new SafeFileOperationService(workspace.Storage);

        Assert.False(File.Exists(ownedArtifact));
        Assert.Equal("must not be inferred as owned", await File.ReadAllTextAsync(similarForeignFile));
        Assert.Equal("[]", (await File.ReadAllTextAsync(journalPath)).Trim());
    }

    [Fact]
    public async Task TamperedTransferArtifactJournal_IsQuarantinedWithoutDeletingUserFile()
    {
        await using var workspace = new ScenarioWorkspace();
        var destinationDirectory = Path.Combine(workspace.Root, "artifact-guard");
        Directory.CreateDirectory(destinationDirectory);
        var valuableFile = Path.Combine(destinationDirectory, "Thesis-final.txt");
        await File.WriteAllTextAsync(valuableFile, "irreplaceable user work");
        var journalPath = Path.Combine(workspace.Storage.DirectoryPath, "transfer-artifacts.json");
        var invalidJournal = JsonSerializer.Serialize(new[]
        {
            new
            {
                id = Guid.NewGuid(),
                artifactPath = valuableFile,
                destinationPath = valuableFile,
                createdAt = DateTimeOffset.UtcNow,
                ownerProcessId = int.MaxValue
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(journalPath, invalidJournal);

        _ = new SafeFileOperationService(workspace.Storage);

        Assert.Equal("irreplaceable user work", await File.ReadAllTextAsync(valuableFile));
        Assert.False(File.Exists(journalPath));
        var quarantine = Assert.Single(Directory.EnumerateFiles(
            workspace.Storage.DirectoryPath, "transfer-artifacts.corrupt-*.json"));
        Assert.Equal(invalidJournal, await File.ReadAllTextAsync(quarantine));
    }

    [Fact]
    public async Task ArtifactOwnedByLiveProcess_IsNeverRemovedByAnotherServiceInstance()
    {
        await using var workspace = new ScenarioWorkspace();
        var destinationDirectory = Path.Combine(workspace.Root, "live-artifact-owner");
        Directory.CreateDirectory(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, "ActiveTransfer.bin");
        var artifactId = Guid.NewGuid();
        var artifact = destination + $".odyssey-part-{artifactId:N}";
        await File.WriteAllTextAsync(artifact, "transfer still in progress");
        var journalPath = Path.Combine(workspace.Storage.DirectoryPath, "transfer-artifacts.json");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(new[]
        {
            new
            {
                id = artifactId,
                artifactPath = artifact,
                destinationPath = destination,
                createdAt = DateTimeOffset.UtcNow,
                ownerProcessId = Environment.ProcessId
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));

        _ = new SafeFileOperationService(workspace.Storage);

        Assert.Equal("transfer still in progress", await File.ReadAllTextAsync(artifact));
        Assert.True(File.Exists(journalPath));
        Assert.Contains(artifactId.ToString(), await File.ReadAllTextAsync(journalPath),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentOperationServices_ShareArtifactJournalWithoutLosingOwnership()
    {
        const int fileSize = 8 * 1024 * 1024;
        await using var workspace = new ScenarioWorkspace();
        var sourceDirectory = Path.Combine(workspace.Root, "parallel-service-sources");
        var firstDestination = Path.Combine(workspace.Root, "parallel-destination-a");
        var secondDestination = Path.Combine(workspace.Root, "parallel-destination-b");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(firstDestination);
        Directory.CreateDirectory(secondDestination);
        var firstSource = Path.Combine(sourceDirectory, "ParallelArtifactA.bin");
        var secondSource = Path.Combine(sourceDirectory, "ParallelArtifactB.bin");
        await using (var stream = new FileStream(firstSource, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(fileSize);
        await using (var stream = new FileStream(secondSource, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(fileSize);
        var firstService = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };
        var secondService = new SafeFileOperationService(workspace.Storage)
        {
            AccessMode = FileAccessMode.ManageFiles
        };

        var completed = await Task.WhenAll(
            firstService.TransferAsync(new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = firstSource,
                DestinationDirectory = firstDestination,
                VerifyAfterCopy = true
            }),
            secondService.TransferAsync(new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = secondSource,
                DestinationDirectory = secondDestination,
                VerifyAfterCopy = true
            }));

        Assert.All(completed, outcome => Assert.True(outcome.Verified));
        Assert.Equal(fileSize, new FileInfo(Path.Combine(firstDestination, Path.GetFileName(firstSource))).Length);
        Assert.Equal(fileSize, new FileInfo(Path.Combine(secondDestination, Path.GetFileName(secondSource))).Length);
        Assert.Empty(Directory.EnumerateFileSystemEntries(firstDestination, "*.odyssey-part-*"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(secondDestination, "*.odyssey-part-*"));
        Assert.Equal("[]", (await File.ReadAllTextAsync(
            Path.Combine(workspace.Storage.DirectoryPath, "transfer-artifacts.json"))).Trim());
    }

    [Fact]
    public async Task RealCatalogueFiltersAndPages_MatchIndependentFilesystemTruth()
    {
        const int primaryFiles = 600;
        const int secondaryFiles = 160;
        const int pageSize = 37;
        var extensions = new[] { ".txt", ".jpg", ".cs", ".zip" };
        var baseTime = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
        await using var workspace = new ScenarioWorkspace();
        var primaryPath = Path.Combine(workspace.Root, "differential-primary");
        var secondaryPath = Path.Combine(workspace.Root, "differential-secondary");
        Directory.CreateDirectory(primaryPath);
        Directory.CreateDirectory(secondaryPath);
        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, primaryPath);
        var secondaryTarget = await runtime.Store.SaveTargetAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = investigation.Session.Id,
            RootPath = secondaryPath
        });
        var manifest = new List<DifferentialFile>(primaryFiles);

        for (var index = 0; index < primaryFiles; index++)
        {
            var directory = Path.Combine(primaryPath, $"group-{index % 12:D2}");
            Directory.CreateDirectory(directory);
            var extension = extensions[index % extensions.Length];
            var path = Path.Combine(directory, $"RecoveryDataset_Žluťoučký_{index:D4}{extension}");
            var size = 256L + index * 97L % 8_192L;
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(size);
            File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(index).UtcDateTime);
            var info = new FileInfo(path);
            manifest.Add(new DifferentialFile(
                info.FullName, info.Name, extension, info.Length,
                new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc))));
        }
        for (var index = 0; index < secondaryFiles; index++)
        {
            var directory = Path.Combine(secondaryPath, $"group-{index % 4:D2}");
            Directory.CreateDirectory(directory);
            var extension = extensions[index % extensions.Length];
            var path = Path.Combine(directory, $"RecoveryDataset_Žluťoučký_secondary_{index:D4}{extension}");
            var size = 256L + index * 97L % 8_192L;
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.SetLength(size);
            File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(200 + index).UtcDateTime);
        }

        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(secondaryTarget, null, CancellationToken.None)).Status);

        var filteredRequest = new SearchRequest
        {
            Query = "recoverydataset zlutoucky",
            SessionId = investigation.Session.Id,
            TargetId = investigation.Target.Id,
            Extension = "TXT",
            Category = FileCategory.Documents,
            MinimumSize = 1_500,
            MaximumSize = 6_500,
            ModifiedFrom = baseTime.AddMinutes(100),
            ModifiedTo = baseTime.AddMinutes(500)
        };
        var expectedFiltered = manifest.Where(item =>
                item.Extension == ".txt"
                && item.Size is >= 1_500 and <= 6_500
                && item.ModifiedAt >= filteredRequest.ModifiedFrom
                && item.ModifiedAt <= filteredRequest.ModifiedTo)
            .Select(item => item.FullPath)
            .ToHashSet(PathComparer);
        var firstPass = await ReadAllPagesAsync(runtime.Search, filteredRequest, pageSize);
        var secondPass = await ReadAllPagesAsync(runtime.Search, filteredRequest, pageSize);

        Assert.True(expectedFiltered.Count > pageSize);
        Assert.Equal(expectedFiltered.Count, firstPass.Count);
        Assert.True(expectedFiltered.SetEquals(firstPass.Select(item => item.FullPath)));
        Assert.Equal(firstPass.Select(item => item.FileId), secondPass.Select(item => item.FileId));
        Assert.Equal(firstPass.Count, firstPass.Select(item => item.FileId).Distinct().Count());
        var unscoped = await runtime.Search.SearchAsync(filteredRequest with
        {
            TargetId = null,
            Limit = 1_000
        }, CancellationToken.None);
        Assert.True(unscoped.Results.Count > firstPass.Count);

        var catalogueRequest = new SearchRequest
        {
            SessionId = investigation.Session.Id,
            TargetId = investigation.Target.Id,
            Extension = ".jpg",
            Category = FileCategory.Images,
            MinimumSize = 2_000,
            MaximumSize = 7_000,
            ModifiedFrom = baseTime.AddMinutes(50),
            ModifiedTo = baseTime.AddMinutes(550)
        };
        var expectedOrder = manifest.Where(item =>
                item.Extension == ".jpg"
                && item.Size is >= 2_000 and <= 7_000
                && item.ModifiedAt >= catalogueRequest.ModifiedFrom
                && item.ModifiedAt <= catalogueRequest.ModifiedTo)
            .OrderByDescending(item => item.ModifiedAt)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.FullPath)
            .ToArray();
        var actualOrder = await ReadAllPagesAsync(runtime.Search, catalogueRequest, pageSize);

        Assert.True(expectedOrder.Length > pageSize);
        Assert.Equal(expectedOrder, actualOrder.Select(item => item.FullPath));
        Assert.Equal(expectedOrder.Length, actualOrder.Select(item => item.FileId).Distinct().Count());
    }

    [Fact]
    public async Task PdfOfficeAndZipContent_BecomesSearchableWhileCorruptDocumentIsIsolated()
    {
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "document-content");
        Directory.CreateDirectory(targetPath);
        var pdfPath = Path.Combine(targetPath, "artifact-a.pdf");
        var docxPath = Path.Combine(targetPath, "artifact-b.docx");
        var xlsxPath = Path.Combine(targetPath, "artifact-c.xlsx");
        var pptxPath = Path.Combine(targetPath, "artifact-d.pptx");
        var zipPath = Path.Combine(targetPath, "artifact-e.zip");
        var corruptPath = Path.Combine(targetPath, "artifact-corrupt.pdf");
        await CreatePdfAsync(pdfPath, "apollo compass 731");
        CreateDocx(docxPath, "morava lantern 482");
        CreateXlsx(xlsxPath, "brno meridian 953");
        CreatePptx(pptxPath, "odyssey atlas 264");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            archive.CreateEntry("contracts/forgotten-project-nimbus-815.txt");
        await File.WriteAllBytesAsync(corruptPath, "%PDF-corrupted-customer-file"u8.ToArray());
        var sourcePaths = new[] { pdfPath, docxPath, xlsxPath, pptxPath, zipPath, corruptPath };
        var originalHashes = new Dictionary<string, string>(PathComparer);
        var originalModified = new Dictionary<string, DateTime>(PathComparer);
        foreach (var path in sourcePaths)
        {
            originalHashes[path] = await HashAsync(path);
            originalModified[path] = File.GetLastWriteTimeUtc(path);
        }

        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);
        var automationOptions = new BackgroundAutomationOptions(
            TimeSpan.FromMilliseconds(25), TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));
        await using var background = new BackgroundAutomationService(
            runtime.Store, runtime.Scanner, new LocalContentExtractor(), Performance,
            NullLogger<BackgroundAutomationService>.Instance, automationOptions);
        await background.StartAsync(investigation.Session.Id, [investigation.Target]);
        background.NotifyTargetScanned(investigation.Target);
        var expectedQueries = new Dictionary<string, string>(PathComparer)
        {
            [pdfPath] = "apollo compass 731",
            [docxPath] = "morava lantern 482",
            [xlsxPath] = "brno meridian 953",
            [pptxPath] = "odyssey atlas 264",
            [zipPath] = "forgotten project nimbus 815"
        };
        await WaitUntilAsync(async () =>
        {
            foreach (var expected in expectedQueries)
            {
                var response = await runtime.Search.SearchAsync(new SearchRequest
                {
                    Query = expected.Value,
                    SessionId = investigation.Session.Id
                }, CancellationToken.None);
                if (!response.Results.Any(item =>
                        PathComparer.Equals(item.FullPath, expected.Key)
                        && item.MatchEvidence.HasFlag(SearchMatchEvidence.Content))) return false;
            }
            var corruptState = await ReadContentStateAsync(runtime, corruptPath);
            return corruptState.Status == 2 && !string.IsNullOrWhiteSpace(corruptState.Error);
        }, TimeSpan.FromSeconds(10));
        await background.StopAsync();

        foreach (var path in sourcePaths)
        {
            Assert.Equal(originalHashes[path], await HashAsync(path));
            Assert.Equal(originalModified[path], File.GetLastWriteTimeUtc(path));
        }
        var corruptByName = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "artifact corrupt",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);
        Assert.Contains(corruptByName.Results, item =>
            PathComparer.Equals(item.FullPath, corruptPath) && item.MatchEvidence.HasFlag(SearchMatchEvidence.Name));
    }

    [Fact]
    public async Task FileChangedBetweenCandidateCheckAndExtraction_IsRescannedBeforeContentPublication()
    {
        await using var workspace = new ScenarioWorkspace();
        var targetPath = Path.Combine(workspace.Root, "content-version-race");
        Directory.CreateDirectory(targetPath);
        var documentPath = Path.Combine(targetPath, "unknown-document.txt");
        await File.WriteAllTextAsync(documentPath, "obsolete nebula phrase 111");
        var runtime = await OpenRuntimeAsync(workspace.Storage);
        var investigation = await CreateInvestigationAsync(runtime.Store, targetPath);
        Assert.Equal(ScanStatus.Completed,
            (await runtime.Scanner.ScanAsync(investigation.Target, null, CancellationToken.None)).Status);
        var extractor = new BlockingFirstExtractor(new LocalContentExtractor(), documentPath);
        var automationOptions = new BackgroundAutomationOptions(
            TimeSpan.FromHours(1), TimeSpan.FromHours(1),
            TimeSpan.FromMilliseconds(50), TimeSpan.FromHours(1));
        await using var background = new BackgroundAutomationService(
            runtime.Store, runtime.Scanner, extractor, Performance,
            NullLogger<BackgroundAutomationService>.Instance, automationOptions);
        await background.StartAsync(investigation.Session.Id, [investigation.Target]);
        background.NotifyTargetScanned(investigation.Target);
        await extractor.FirstExtractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var previousModified = File.GetLastWriteTimeUtc(documentPath);
        await File.WriteAllTextAsync(documentPath,
            "current quasar recovery phrase 928 with a deliberately different length");
        File.SetLastWriteTimeUtc(documentPath, previousModified.AddSeconds(5));
        var currentHash = await HashAsync(documentPath);
        extractor.ReleaseFirstExtraction.TrySetResult();

        await WaitUntilAsync(async () =>
        {
            var response = await runtime.Search.SearchAsync(new SearchRequest
            {
                Query = "current quasar 928",
                SessionId = investigation.Session.Id
            }, CancellationToken.None);
            return extractor.Calls >= 2 && response.Results.Any(item =>
                PathComparer.Equals(item.FullPath, documentPath)
                && item.MatchEvidence.HasFlag(SearchMatchEvidence.Content));
        }, TimeSpan.FromSeconds(8));
        await background.StopAsync();

        var currentInfo = new FileInfo(documentPath);
        var currentResult = Assert.Single((await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "current quasar 928",
            SessionId = investigation.Session.Id
        }, CancellationToken.None)).Results);
        var obsolete = await runtime.Search.SearchAsync(new SearchRequest
        {
            Query = "obsolete nebula 111",
            SessionId = investigation.Session.Id
        }, CancellationToken.None);

        Assert.True(extractor.Calls >= 2);
        Assert.Equal(currentInfo.Length, currentResult.Size);
        Assert.Equal(currentInfo.LastWriteTimeUtc, currentResult.ModifiedAt?.UtcDateTime);
        Assert.Empty(obsolete.Results);
        Assert.Equal(currentHash, await HashAsync(documentPath));
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
        return new ScenarioRuntime(connections, store, search, scanner);
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
        int pageSize) => await ReadAllPagesAsync(search, new SearchRequest
        {
            Query = query,
            SessionId = sessionId
        }, pageSize);

    private static async Task<List<SearchResult>> ReadAllPagesAsync(
        ISearchService search,
        SearchRequest request,
        int pageSize)
    {
        var results = new List<SearchResult>();
        while (true)
        {
            var page = await search.SearchAsync(request with
            {
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

    private static async Task<long> ReadPragmaAsync(ScenarioRuntime runtime, string pragma)
    {
        await using var connection = await runtime.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static FileEntry CreateFileEntry(Guid targetId, string path)
    {
        var file = new FileInfo(path);
        return new FileEntry
        {
            TargetId = targetId,
            Name = file.Name,
            FullPath = file.FullName,
            ParentPath = file.DirectoryName!,
            Extension = file.Extension.ToLowerInvariant(),
            Type = FileEntryType.File,
            Category = new ExtensionFileClassifier().Classify(file.Extension, FileEntryType.File),
            Size = file.Length,
            CreatedAt = file.CreationTimeUtc,
            ModifiedAt = file.LastWriteTimeUtc
        };
    }

    private static async Task<ScanStatus> ReadScanStatusAsync(ScenarioRuntime runtime, Guid scanId)
    {
        await using var connection = await runtime.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Status FROM ScanSessions WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", scanId.ToString());
        return (ScanStatus)Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<int> CountResumedCompletedScansAsync(ScenarioRuntime runtime, Guid scanId)
    {
        await using var connection = await runtime.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM ScanSessions WHERE ResumedFromScanId=$id AND Status=$status;";
        command.Parameters.AddWithValue("$id", scanId.ToString());
        command.Parameters.AddWithValue("$status", (int)ScanStatus.Completed);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<(int Status, string? Error)> ReadContentStateAsync(
        ScenarioRuntime runtime,
        string path)
    {
        await using var connection = await runtime.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ContentStatus, ContentError FROM Files WHERE FullPath=$path;";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task CreatePdfAsync(string path, string text)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText(text, 12, new PdfPoint(40, 760), font);
        await File.WriteAllBytesAsync(path, builder.Build());
    }

    private static void CreateDocx(string path, string text)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body(new W.Paragraph(
            new W.Run(new W.Text(text)))));
        main.Document.Save();
    }

    private static void CreateXlsx(string path, string text)
    {
        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbook = document.AddWorkbookPart();
        workbook.Workbook = new S.Workbook();
        var shared = workbook.AddNewPart<SharedStringTablePart>();
        shared.SharedStringTable = new S.SharedStringTable(new S.SharedStringItem(new S.Text(text)));
        var worksheet = workbook.AddNewPart<WorksheetPart>();
        worksheet.Worksheet = new S.Worksheet(new S.SheetData(new S.Row(new S.Cell
        {
            DataType = S.CellValues.SharedString,
            CellValue = new S.CellValue("0")
        })));
        workbook.Workbook.AppendChild(new S.Sheets(new S.Sheet
        {
            Id = workbook.GetIdOfPart(worksheet),
            SheetId = 1,
            Name = "Recovered work"
        }));
        workbook.Workbook.Save();
    }

    private static void CreatePptx(string path, string text)
    {
        using var document = PresentationDocument.Create(path, PresentationDocumentType.Presentation);
        var presentation = document.AddPresentationPart();
        presentation.Presentation = new P.Presentation();
        var slide = presentation.AddNewPart<SlidePart>();
        slide.Slide = new P.Slide(new P.CommonSlideData(new P.ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = string.Empty },
                new P.NonVisualGroupShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.GroupShapeProperties(new A.TransformGroup()),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2, Name = "Recovered work" },
                    new P.NonVisualShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(),
                new P.TextBody(new A.BodyProperties(), new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(text))))))));
        var slideIds = presentation.Presentation.AppendChild(new P.SlideIdList());
        slideIds.Append(new P.SlideId
        {
            Id = 256,
            RelationshipId = presentation.GetIdOfPart(slide)
        });
        presentation.Presentation.Save();
    }

    private static async Task SetMaxPageCountAsync(SqliteConnection connection, long pages)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA max_page_count={pages};";
        var applied = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(pages, applied);
    }

    private static async Task ConfigureStorageLimitAsync(SqliteConnection connection, long pages)
    {
        await using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await checkpoint.ExecuteNonQueryAsync();
        }
        await using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE;";
            Assert.Equal("delete", Convert.ToString(await journal.ExecuteScalarAsync()), ignoreCase: true);
        }
        await using var limit = connection.CreateCommand();
        limit.CommandText = $"PRAGMA max_page_count={pages};";
        Assert.Equal(pages, Convert.ToInt64(await limit.ExecuteScalarAsync()));
    }

    private static async Task EnableWalAsync(ScenarioRuntime runtime)
    {
        await using var connection = await runtime.Connections.OpenAsync();
        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode=WAL;";
        Assert.Equal("wal", Convert.ToString(await journal.ExecuteScalarAsync()), ignoreCase: true);
    }

    private static async Task<string> HashAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or
                                          PlatformNotSupportedException or NotSupportedException)
        {
            return false;
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record ScenarioRuntime(
        SqliteConnectionFactory Connections,
        SqliteOdysseyStore Store,
        SqliteSearchService Search,
        ScanCoordinator Scanner);

    private sealed record DifferentialFile(
        string FullPath,
        string Name,
        string Extension,
        long Size,
        DateTimeOffset ModifiedAt);

    private sealed class ImmediateProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class ConcurrencyObservingScanner(IFileSystemScanner inner) : IFileSystemScanner
    {
        private readonly object _sync = new();
        private int _active;

        public int MaximumConcurrency { get; private set; }
        public TaskCompletionSource FirstEnumerationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<FileSystemItem> EnumerateAsync(
            ScanTarget target,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _active++;
                MaximumConcurrency = Math.Max(MaximumConcurrency, _active);
            }
            FirstEnumerationStarted.TrySetResult();
            try
            {
                await foreach (var item in inner.EnumerateAsync(target, cancellationToken))
                {
                    await Task.Delay(1, cancellationToken);
                    yield return item;
                }
            }
            finally
            {
                lock (_sync) _active--;
            }
        }
    }

    private sealed class CancellingScanner(
        IFileSystemScanner inner,
        CancellationTokenSource cancellation,
        int cancelAfterItems) : IFileSystemScanner
    {
        public async IAsyncEnumerable<FileSystemItem> EnumerateAsync(
            ScanTarget target,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var yielded = 0;
            await foreach (var item in inner.EnumerateAsync(target, cancellationToken))
            {
                yield return item;
                if (Interlocked.Increment(ref yielded) == cancelAfterItems)
                    cancellation.Cancel();
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private sealed class BlockingFirstExtractor(IContentExtractor inner, string controlledPath) : IContentExtractor
    {
        private int _calls;

        public IReadOnlySet<string> SupportedExtensions => inner.SupportedExtensions;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource FirstExtractionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstExtraction { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanHandle(string extension) => inner.CanHandle(extension);

        public async Task<ExtractedContent?> ExtractAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1 && PathComparer.Equals(Path.GetFullPath(path), Path.GetFullPath(controlledPath)))
            {
                FirstExtractionStarted.TrySetResult();
                await ReleaseFirstExtraction.Task.WaitAsync(cancellationToken);
            }
            return await inner.ExtractAsync(path, cancellationToken);
        }
    }

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
