using System.IO.Compression;
using System.Formats.Tar;
using System.Text.Json;
using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class ArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-archive-{Guid.NewGuid():N}");

    [Fact]
    public async Task Zip_BrowsesVirtualDirectoriesAndAdvertisesOnlyImplementedCapabilities()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("sample.zip",
            ("folder/inside.txt", "inside"),
            ("root.txt", "root"));
        var service = new SafeArchiveService();

        var rootEntries = await service.ListAsync(archivePath);
        var folderEntries = await service.ListAsync(archivePath, "folder");

        Assert.Contains(rootEntries, item => item.Name == "folder" && item.Type == FileEntryType.Directory);
        Assert.Contains(rootEntries, item => item.Name == "root.txt" && item.Type == FileEntryType.File);
        Assert.Equal("inside.txt", Assert.Single(folderEntries).Name);
        Assert.Equal(ArchiveCapabilities.Browse | ArchiveCapabilities.Extract | ArchiveCapabilities.Create
                     | ArchiveCapabilities.Update | ArchiveCapabilities.DeleteEntries,
            service.GetCapabilities(archivePath));
        Assert.DoesNotContain(service.Formats.Where(format => format.Name != "ZIP"),
            format => format.Capabilities.HasFlag(ArchiveCapabilities.Update));
        Assert.Equal(ArchiveCapabilities.Browse | ArchiveCapabilities.Extract | ArchiveCapabilities.Create,
            service.Formats.Single(format => format.Name == "TAR").Capabilities);
        Assert.True(service.Formats.Single(format => format.Name == "TAR").IsAvailable);

        var provider = new ArchiveFileLocationProvider(service);
        Assert.Equal(FileLocationCapabilities.Browse | FileLocationCapabilities.Read, provider.Capabilities);
        Assert.Equal(2, (await provider.ListAsync(string.Empty, archivePath)).Count);
    }

    [Fact]
    public async Task Tar_CreationIsDeterministicBrowsableAndExtractableWithoutNativeEngine()
    {
        Directory.CreateDirectory(_root);
        var source = Directory.CreateDirectory(Path.Combine(_root, "package"));
        var nested = Directory.CreateDirectory(Path.Combine(source.FullName, "nested"));
        Directory.CreateDirectory(Path.Combine(source.FullName, "empty"));
        var file = Path.Combine(nested.FullName, "note.txt");
        await File.WriteAllTextAsync(file, "managed tar content");
        var timestamp = new DateTime(2025, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, timestamp);
        Directory.SetLastWriteTimeUtc(nested.FullName, timestamp);
        Directory.SetLastWriteTimeUtc(Path.Combine(source.FullName, "empty"), timestamp);
        Directory.SetLastWriteTimeUtc(source.FullName, timestamp);
        var first = Path.Combine(_root, "first.tar");
        var second = Path.Combine(_root, "second.tar");
        var destination = Directory.CreateDirectory(Path.Combine(_root, "extracted")).FullName;
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        await service.CreateAsync(first, [source.FullName]);
        await service.CreateAsync(second, [source.FullName]);

        Assert.Equal(await File.ReadAllBytesAsync(first), await File.ReadAllBytesAsync(second));
        Assert.Equal(ArchiveCapabilities.Browse | ArchiveCapabilities.Extract | ArchiveCapabilities.Create,
            service.GetCapabilities(first));
        Assert.Equal(["empty", "nested"],
            (await service.ListAsync(first, "package")).Select(item => item.Name).Order().ToArray());
        await service.ExtractAsync(first, "package", destination);
        Assert.Equal("managed tar content",
            await File.ReadAllTextAsync(Path.Combine(destination, "package", "nested", "note.txt")));
        Assert.True(Directory.Exists(Path.Combine(destination, "package", "empty")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), path =>
            Path.GetFileName(path).Contains(".odyssey-new-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("/absolute.txt")]
    public async Task Tar_PathTraversalAndAbsoluteNamesAreRejected(string hostileName)
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "hostile.tar");
        CreateTarAt(archivePath, (hostileName, "hostile"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SafeArchiveService().ListAsync(archivePath));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Tar_SpecialEntriesAreVisibleButNeverExtracted()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "link.tar");
        using (var stream = File.Create(archivePath))
        using (var writer = new TarWriter(stream, leaveOpen: false))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "link")
            {
                LinkName = "../../outside"
            });
        }
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        Assert.True(Assert.Single(await service.ListAsync(archivePath)).IsSymbolicLink);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ExtractAsync(archivePath, "link", destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task Tar_CorruptionAndCaseCollisionsAreRejected()
    {
        Directory.CreateDirectory(_root);
        var collision = Path.Combine(_root, "collision.tar");
        CreateTarAt(collision, ("Readme.txt", "one"), ("README.TXT", "two"));
        var corrupt = Path.Combine(_root, "corrupt.tar");
        var bytes = new byte[1024];
        Array.Fill(bytes, byte.MaxValue);
        await File.WriteAllBytesAsync(corrupt, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => new SafeArchiveService().ListAsync(collision));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SafeArchiveService().ListAsync(corrupt));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("folder/../../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\absolute.txt")]
    public async Task Zip_PathTraversalAndAbsoluteNamesAreRejected(string hostileName)
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("hostile.zip", (hostileName, "hostile"));
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ListAsync(archivePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task Zip_CaseCollidingNamesAreRejectedBeforeExtraction()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("collision.zip", ("Readme.txt", "one"), ("README.TXT", "two"));

        await Assert.ThrowsAsync<InvalidDataException>(() => new SafeArchiveService().ListAsync(archivePath));
    }

    [Fact]
    public async Task Zip_SymbolicLinkEntryCanBeInspectedButCannotBeExtracted()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "link.zip");
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = unchecked((0xA000 | 0x1FF) << 16);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("outside-target");
        }
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        Assert.True(Assert.Single(await service.ListAsync(archivePath)).IsSymbolicLink);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ExtractAsync(archivePath, "link", destination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ArchiveSourceAndDestinationLinksAreRejectedAtTheBoundary()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("source.zip", ("one.txt", "one"));
        var linkedArchive = Path.Combine(_root, "linked.zip");
        File.CreateSymbolicLink(linkedArchive, archivePath);
        var realDestination = Directory.CreateDirectory(Path.Combine(_root, "real-destination")).FullName;
        var linkedDestination = Path.Combine(_root, "linked-destination");
        Directory.CreateSymbolicLink(linkedDestination, realDestination);
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        await Assert.ThrowsAsync<IOException>(() => service.ListAsync(linkedArchive));
        await Assert.ThrowsAsync<IOException>(() => service.ExtractAsync(archivePath, "one.txt", linkedDestination));
        Assert.Empty(Directory.EnumerateFileSystemEntries(realDestination));
    }

    [Fact]
    public async Task Zip_ExpandedSizeQuotaAndCorruptionAreRejected()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("large.zip", ("large.txt", new string('x', 128)));
        var limited = new SafeArchiveService(new ArchiveSafetyLimits
        {
            MaximumEntries = 10,
            MaximumDepth = 8,
            MaximumEntryBytes = 64,
            MaximumExpandedBytes = 64,
            MaximumCompressionRatio = 1_000
        });
        var corrupt = Path.Combine(_root, "corrupt.zip");
        await File.WriteAllBytesAsync(corrupt, [0x50, 0x4b, 0x03, 0x04, 0xff]);

        await Assert.ThrowsAsync<InvalidDataException>(() => limited.ListAsync(archivePath));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SafeArchiveService().ListAsync(corrupt));
    }

    [Fact]
    public async Task Zip_ExtractsTreeThroughStagingAndLeavesOriginalArchiveUnchanged()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("tree.zip",
            ("folder/one.txt", "one"),
            ("folder/nested/two.txt", "two"));
        var before = await File.ReadAllBytesAsync(archivePath);
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        var outcome = await service.ExtractAsync(archivePath, "folder", destination);

        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "one.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(destination, "folder", "nested", "two.txt")));
        Assert.Equal(Path.Combine(destination, "folder"), outcome.DestinationPath);
        Assert.Equal(before, await File.ReadAllBytesAsync(archivePath));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(destination),
            path => Path.GetFileName(path).StartsWith(".odyssey-archive-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zip_ConflictFailurePreservesDestinationAndReplacePublishesOnlyAfterExtraction()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("replace.zip", ("one.txt", "new content"));
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var target = Path.Combine(destination, "one.txt");
        await File.WriteAllTextAsync(target, "original content");
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        await Assert.ThrowsAsync<IOException>(() => service.ExtractAsync(
            archivePath, "one.txt", destination, FileConflictPolicy.Fail));
        Assert.Equal("original content", await File.ReadAllTextAsync(target));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(destination),
            path => Path.GetFileName(path).StartsWith(".odyssey-archive-", StringComparison.Ordinal));

        await service.ExtractAsync(archivePath, "one.txt", destination, FileConflictPolicy.Replace);
        Assert.Equal("new content", await File.ReadAllTextAsync(target));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(destination),
            path => Path.GetFileName(path).StartsWith(".odyssey-archive-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zip_CancellationDuringStreamingPublishesNothingAndCleansStaging()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "stream.zip");
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("large.bin", CompressionLevel.NoCompression).Open())
        {
            var block = new byte[128 * 1024];
            for (var index = 0; index < 64; index++) entry.Write(block);
        }
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileOperationProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExtractAsync(
            archivePath, "large.bin", destination, progress: progress, cancellationToken: cancellation.Token));

        Assert.False(File.Exists(Path.Combine(destination, "large.bin")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ArchiveExtractionHonorsReadOnlyModeAndQueueCapabilityChecks()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("queued.zip", ("one.txt", "queued"));
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var archives = new SafeArchiveService();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            archives.ExtractAsync(archivePath, "one.txt", destination));

        var operations = new SafeFileOperationService(storage) { AccessMode = FileAccessMode.ManageFiles };
        await using var queue = new FileTransferQueueService(operations, storage, archives: archives);
        await queue.InitializeAsync();
        var job = Assert.Single(await queue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourceEndpoint = FileTransferEndpointKind.Archive,
            SourceConnectionKey = archivePath,
            SourcePath = "one.txt",
            DestinationEndpoint = FileTransferEndpointKind.Local,
            DestinationDirectory = destination
        }]));

        await WaitForStateAsync(queue, job.Id, FileTransferState.Completed);
        Assert.Equal("queued", await File.ReadAllTextAsync(Path.Combine(destination, "one.txt")));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Move,
            SourceEndpoint = FileTransferEndpointKind.Archive,
            SourceConnectionKey = archivePath,
            SourcePath = "one.txt",
            DestinationEndpoint = FileTransferEndpointKind.Archive,
            DestinationConnectionKey = archivePath,
            DestinationDirectory = string.Empty
        }]));
    }

    [Fact]
    public async Task Zip_AddCreatesArchiveAndPreservesDirectoryStructure()
    {
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(_root, "project"));
        Directory.CreateDirectory(Path.Combine(sourceDirectory.FullName, "empty"));
        Directory.CreateDirectory(Path.Combine(sourceDirectory.FullName, "src"));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory.FullName, "src", "main.cs"), "class Main;");
        var archivePath = Path.Combine(_root, "created.zip");
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        await service.AddAsync(archivePath, sourceDirectory.FullName, "packages");

        Assert.True(File.Exists(archivePath));
        Assert.Contains(await service.ListAsync(archivePath, "packages/project"),
            entry => entry.Name == "empty" && entry.Type == FileEntryType.Directory);
        Assert.Equal("main.cs", Assert.Single(await service.ListAsync(archivePath, "packages/project/src")).Name);
        Assert.DoesNotContain(Directory.EnumerateFiles(_root),
            path => Path.GetFileName(path).Contains("odyssey-new", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zip_CreateRejectsCrossSelectionAndCaseCollisionsBeforePublishing()
    {
        var firstDirectory = Directory.CreateDirectory(Path.Combine(_root, "first"));
        var secondDirectory = Directory.CreateDirectory(Path.Combine(_root, "second"));
        var first = Path.Combine(firstDirectory.FullName, "same.txt");
        var second = Path.Combine(secondDirectory.FullName, "same.txt");
        await File.WriteAllTextAsync(first, "one");
        await File.WriteAllTextAsync(second, "two");
        var archivePath = Path.Combine(_root, "collision-create.zip");
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(archivePath, [first, second]));
        Assert.False(File.Exists(archivePath));

        if (OperatingSystem.IsWindows()) return;
        var caseDirectory = Directory.CreateDirectory(Path.Combine(_root, "case"));
        await File.WriteAllTextAsync(Path.Combine(caseDirectory.FullName, "Name.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(caseDirectory.FullName, "name.txt"), "two");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(archivePath, [caseDirectory.FullName]));
        Assert.False(File.Exists(archivePath));
    }

    [Fact]
    public async Task Zip_ConflictPoliciesAndDeleteUseCompleteValidatedRewrite()
    {
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(_root, "source"));
        var source = Path.Combine(sourceDirectory.FullName, "one.txt");
        await File.WriteAllTextAsync(source, "new");
        var archivePath = CreateZip("mutable.zip", ("one.txt", "original"), ("keep.txt", "keep"));
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };
        var before = await File.ReadAllBytesAsync(archivePath);

        await Assert.ThrowsAsync<IOException>(() => service.AddAsync(
            archivePath, source, conflictPolicy: FileConflictPolicy.Fail));
        Assert.Equal(before, await File.ReadAllBytesAsync(archivePath));

        var kept = await service.AddAsync(archivePath, source, conflictPolicy: FileConflictPolicy.KeepBoth);
        Assert.EndsWith("one (2).txt", kept.DestinationPath, StringComparison.Ordinal);
        await service.AddAsync(archivePath, source, conflictPolicy: FileConflictPolicy.Replace);
        Assert.Equal(["keep.txt", "one (2).txt", "one.txt"],
            (await service.ListAsync(archivePath)).Select(item => item.Name).Order().ToArray());

        var extract = Directory.CreateDirectory(Path.Combine(_root, "extract")).FullName;
        await service.ExtractAsync(archivePath, "one.txt", extract);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(extract, "one.txt")));
        await service.DeleteAsync(archivePath, "one (2).txt");
        Assert.Equal(["keep.txt", "one.txt"],
            (await service.ListAsync(archivePath)).Select(item => item.Name).Order().ToArray());
    }

    [Fact]
    public async Task Zip_WriteCancellationPreservesOriginalAndCleansTemporaryArchive()
    {
        Directory.CreateDirectory(_root);
        var random = new byte[256 * 1024];
        Random.Shared.NextBytes(random);
        var archivePath = CreateZip("cancel-write.zip", ("existing.txt", Convert.ToBase64String(random)));
        var source = Path.Combine(_root, "large.bin");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);
        var before = await File.ReadAllBytesAsync(archivePath);
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileOperationProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.AddAsync(
            archivePath, source, progress: progress, cancellationToken: cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(archivePath));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root),
            path => Path.GetFileName(path).Contains("odyssey-new", StringComparison.Ordinal)
                    || Path.GetFileName(path).Contains("odyssey-backup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Zip_WriteRejectsSourceLinksAndReadOnlyModeWithoutChangingArchive()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("link-write.zip", ("existing.txt", "existing"));
        var real = Path.Combine(_root, "real.txt");
        var link = Path.Combine(_root, "link.txt");
        var realDirectory = Directory.CreateDirectory(Path.Combine(_root, "real-directory"));
        var linkedDirectory = Path.Combine(_root, "linked-directory");
        await File.WriteAllTextAsync(real, "linked");
        await File.WriteAllTextAsync(Path.Combine(realDirectory.FullName, "nested.txt"), "nested");
        File.CreateSymbolicLink(link, real);
        Directory.CreateSymbolicLink(linkedDirectory, realDirectory.FullName);
        var before = await File.ReadAllBytesAsync(archivePath);
        var readOnly = new SafeArchiveService();
        var writable = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.AddAsync(archivePath, real));
        await Assert.ThrowsAsync<IOException>(() => writable.AddAsync(archivePath, link));
        await Assert.ThrowsAsync<IOException>(() => writable.AddAsync(
            archivePath, Path.Combine(linkedDirectory, "nested.txt")));
        Assert.Equal(before, await File.ReadAllBytesAsync(archivePath));
    }

    [Fact]
    public async Task Tar_CancellationReadOnlyAndSourceLinkGuardsPublishNothing()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "large.bin");
        await File.WriteAllBytesAsync(source, new byte[4 * 1024 * 1024]);
        var link = Path.Combine(_root, "source-link.bin");
        File.CreateSymbolicLink(link, source);
        var cancelledArchive = Path.Combine(_root, "cancelled.tar");
        var linkedArchive = Path.Combine(_root, "linked.tar");
        var readOnlyArchive = Path.Combine(_root, "readonly.tar");
        var writable = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<FileOperationProgress>(_ => cancellation.Cancel());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writable.CreateAsync(
            cancelledArchive, [source], progress, cancellation.Token));
        await Assert.ThrowsAsync<IOException>(() => writable.CreateAsync(linkedArchive, [link]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new SafeArchiveService().CreateAsync(readOnlyArchive, [source]));

        Assert.False(File.Exists(cancelledArchive));
        Assert.False(File.Exists(linkedArchive));
        Assert.False(File.Exists(readOnlyArchive));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root), path =>
            Path.GetFileName(path).Contains(".odyssey-new-", StringComparison.Ordinal)
            || Path.GetFileName(path).Contains(".odyssey-backup-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tar_DoesNotAdvertiseOrAcceptUpdateAndDeleteMutations()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(source, "source");
        var archivePath = Path.Combine(_root, "created.tar");
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };
        await service.CreateAsync(archivePath, [source]);
        var before = await File.ReadAllBytesAsync(archivePath);

        Assert.False(service.GetCapabilities(archivePath).HasFlag(ArchiveCapabilities.Update));
        Assert.False(service.GetCapabilities(archivePath).HasFlag(ArchiveCapabilities.DeleteEntries));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.AddAsync(archivePath, source));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.DeleteAsync(archivePath, "source.txt"));
        Assert.Equal(before, await File.ReadAllBytesAsync(archivePath));
    }

    [Fact]
    public async Task Zip_ReplaceRemovesConflictingFileAncestorAndKeepBothRefusesInvalidTree()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("ancestor.zip", ("folder", "not a directory"), ("keep.txt", "keep"));
        var source = Path.Combine(_root, "item.bin");
        await File.WriteAllBytesAsync(source, new byte[2 * 1024 * 1024]);
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };
        var before = await File.ReadAllBytesAsync(archivePath);

        await Assert.ThrowsAsync<IOException>(() => service.AddAsync(
            archivePath, source, "folder", FileConflictPolicy.KeepBoth));
        Assert.Equal(before, await File.ReadAllBytesAsync(archivePath));

        await service.AddAsync(archivePath, source, "folder", FileConflictPolicy.Replace);
        Assert.Equal("item.bin", Assert.Single(await service.ListAsync(archivePath, "folder")).Name);
        Assert.Contains(await service.ListAsync(archivePath), item => item.Name == "keep.txt");
    }

    [Fact]
    public async Task Zip_ConcurrentMutationsAreSerializedWithoutLosingEitherResult()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("serialized.zip", ("existing.txt", "existing"));
        var first = Path.Combine(_root, "first.txt");
        var second = Path.Combine(_root, "second.txt");
        await File.WriteAllTextAsync(first, "first");
        await File.WriteAllTextAsync(second, "second");
        var service = new SafeArchiveService { AccessMode = FileAccessMode.ManageFiles };

        await Task.WhenAll(
            service.AddAsync(archivePath, first),
            service.AddAsync(archivePath, second));

        Assert.Equal(["existing.txt", "first.txt", "second.txt"],
            (await service.ListAsync(archivePath)).Select(item => item.Name).Order().ToArray());
    }

    [Fact]
    public async Task TransferQueue_RoutesLocalItemsIntoTransactionalZipWriter()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "queued.txt");
        await File.WriteAllTextAsync(source, "queued into zip");
        var archivePath = CreateZip("queue-write.zip", ("existing.txt", "existing"));
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var operations = new SafeFileOperationService(storage) { AccessMode = FileAccessMode.ManageFiles };
        var archives = new SafeArchiveService();
        await using var queue = new FileTransferQueueService(
            operations, storage, archives: archives, archiveMutations: archives);
        await queue.InitializeAsync();

        var job = Assert.Single(await queue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = source,
            SourceEndpoint = FileTransferEndpointKind.Local,
            DestinationDirectory = "incoming",
            DestinationEndpoint = FileTransferEndpointKind.Archive,
            DestinationConnectionKey = archivePath
        }]));
        await WaitForStateAsync(queue, job.Id, FileTransferState.Completed);

        Assert.Equal("queued.txt", Assert.Single(await archives.ListAsync(archivePath, "incoming")).Name);
        var persisted = await ReadAllTextWhenAvailableAsync(
            Path.Combine(storage.DirectoryPath, "transfer-queue.json"));
        Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransferQueue_RelaysArchiveTreeIntoZipAndCleansControlledTemporaryData()
    {
        Directory.CreateDirectory(_root);
        var sourceArchive = CreateZip("relay-source.zip",
            ("folder/one.txt", "one"), ("folder/nested/two.txt", "two"));
        var destinationArchive = CreateZip("relay-destination.zip", ("existing.txt", "existing"));
        var sourceBefore = await File.ReadAllBytesAsync(sourceArchive);
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var operations = new SafeFileOperationService(storage) { AccessMode = FileAccessMode.ManageFiles };
        var archives = new SafeArchiveService();
        await using var queue = new FileTransferQueueService(
            operations, storage, archives: archives, archiveMutations: archives);
        await queue.InitializeAsync();

        var job = Assert.Single(await queue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = "folder",
            SourceEndpoint = FileTransferEndpointKind.Archive,
            SourceConnectionKey = sourceArchive,
            DestinationDirectory = "incoming",
            DestinationEndpoint = FileTransferEndpointKind.Archive,
            DestinationConnectionKey = destinationArchive
        }]));
        await WaitForStateAsync(queue, job.Id, FileTransferState.Completed);

        Assert.Equal("folder", Assert.Single(await archives.ListAsync(destinationArchive, "incoming")).Name);
        Assert.Equal(["nested", "one.txt"],
            (await archives.ListAsync(destinationArchive, "incoming/folder")).Select(item => item.Name).Order().ToArray());
        Assert.Equal(sourceBefore, await File.ReadAllBytesAsync(sourceArchive));
        var relayRoot = Path.Combine(storage.DirectoryPath, "archive-relay");
        Assert.True(!Directory.Exists(relayRoot) || !Directory.EnumerateFileSystemEntries(relayRoot).Any());
    }

    [Fact]
    public async Task ArchiveQueue_RestartRequeuesInterruptedExtractionAndCompletesSafely()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("restart.zip", ("resume.txt", "after restart"));
        var destination = Directory.CreateDirectory(Path.Combine(_root, "destination")).FullName;
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var operations = new SafeFileOperationService(storage) { AccessMode = FileAccessMode.ManageFiles };
        var blockingArchives = new BlockingArchiveService();
        var firstQueue = new FileTransferQueueService(operations, storage, archives: blockingArchives);
        await firstQueue.InitializeAsync();
        var job = Assert.Single(await firstQueue.EnqueueAsync([new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourceEndpoint = FileTransferEndpointKind.Archive,
            SourceConnectionKey = archivePath,
            SourcePath = "resume.txt",
            DestinationEndpoint = FileTransferEndpointKind.Local,
            DestinationDirectory = destination
        }]));
        await blockingArchives.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await firstQueue.DisposeAsync();
        Assert.False(File.Exists(Path.Combine(destination, "resume.txt")));

        await using var resumedQueue = new FileTransferQueueService(
            operations, storage, archives: new SafeArchiveService());
        await resumedQueue.InitializeAsync();
        await WaitForStateAsync(resumedQueue, job.Id, FileTransferState.Completed);

        Assert.Equal("after restart", await File.ReadAllTextAsync(Path.Combine(destination, "resume.txt")));
        Assert.Equal(2, resumedQueue.Items.Single(item => item.Id == job.Id).Attempt);
    }

    [Fact]
    public async Task ArchiveRecovery_CompletesInterruptedReplacementBeforeTransferWorkerStarts()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("recover.zip", ("value.txt", "old"));
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var transactionId = Guid.NewGuid();
        var temporary = RecoveryTemporaryPath(archivePath, transactionId);
        var backup = archivePath + $".odyssey-backup-{transactionId:N}";
        CreateZipAt(temporary, ("value.txt", "new"));
        File.Move(archivePath, backup);
        WriteRecoveryManifest(storage, new ArchiveRecoveryManifest
        {
            SchemaVersion = ArchiveRecoveryManifest.CurrentSchemaVersion,
            TransactionId = transactionId,
            DestinationPath = archivePath,
            TemporaryPath = temporary,
            BackupPath = backup,
            OriginalExisted = true,
            State = ArchiveRecoveryState.OriginalBackedUp,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var operations = new SafeFileOperationService(storage) { AccessMode = FileAccessMode.ManageFiles };
        var archives = new SafeArchiveService(storage: storage) { AccessMode = FileAccessMode.ManageFiles };

        await using var queue = new FileTransferQueueService(
            operations, storage, archives: archives, archiveMutations: archives);
        await queue.InitializeAsync();

        Assert.Equal("new", await ReadZipTextAsync(archivePath, "value.txt"));
        Assert.False(File.Exists(temporary));
        Assert.False(File.Exists(backup));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(storage.DirectoryPath, "archive-recovery")));
    }

    [Fact]
    public async Task ArchiveRecovery_CompletesValidatedTarReplacementAndRemovesBackup()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "recover.tar");
        CreateTarAt(archivePath, ("value.txt", "old"));
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var transactionId = Guid.NewGuid();
        var temporary = RecoveryTemporaryPath(archivePath, transactionId);
        var backup = archivePath + $".odyssey-backup-{transactionId:N}";
        CreateTarAt(temporary, ("value.txt", "new"));
        File.Move(archivePath, backup);
        WriteRecoveryManifest(storage, new ArchiveRecoveryManifest
        {
            SchemaVersion = ArchiveRecoveryManifest.CurrentSchemaVersion,
            TransactionId = transactionId,
            DestinationPath = archivePath,
            TemporaryPath = temporary,
            BackupPath = backup,
            OriginalExisted = true,
            State = ArchiveRecoveryState.OriginalBackedUp,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var service = new SafeArchiveService(storage: storage);

        var report = await service.RecoverAsync();

        Assert.True(report.IsSuccessful);
        Assert.Equal(1, report.RecoveredTransactions);
        Assert.Equal("new", await ReadTarTextAsync(archivePath, "value.txt"));
        Assert.False(File.Exists(temporary));
        Assert.False(File.Exists(backup));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(storage.DirectoryPath, "archive-recovery")));
    }

    [Fact]
    public async Task Zip_PublicationFailureAfterBackupRestoresByteIdenticalOriginalAndClearsJournal()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("publication-failure.zip", ("original.txt", "original"));
        var before = await File.ReadAllBytesAsync(archivePath);
        var source = Path.Combine(_root, "addition.txt");
        await File.WriteAllTextAsync(source, "addition");
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var service = new SafeArchiveService(storage: storage)
        {
            AccessMode = FileAccessMode.ManageFiles,
            PublicationCheckpoint = checkpoint =>
            {
                if (checkpoint == ArchivePublicationCheckpoint.OriginalBackedUp)
                    throw new IOException("Injected publication failure.");
            }
        };

        await Assert.ThrowsAsync<IOException>(() => service.AddAsync(archivePath, source));

        Assert.Equal(before, await File.ReadAllBytesAsync(archivePath));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), path =>
            Path.GetFileName(path).Contains(".odyssey-new-", StringComparison.Ordinal)
            || Path.GetFileName(path).Contains(".odyssey-backup-", StringComparison.Ordinal));
        var recoveryRoot = Path.Combine(storage.DirectoryPath, "archive-recovery");
        Assert.True(!Directory.Exists(recoveryRoot) || !Directory.EnumerateFileSystemEntries(recoveryRoot).Any());
    }

    [Fact]
    public async Task ArchiveRecovery_RestoresValidatedBackupWhenPublishedDestinationIsMissing()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("restore.zip", ("value.txt", "original"));
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var transactionId = Guid.NewGuid();
        var temporary = RecoveryTemporaryPath(archivePath, transactionId);
        var backup = archivePath + $".odyssey-backup-{transactionId:N}";
        File.Move(archivePath, backup);
        WriteRecoveryManifest(storage, new ArchiveRecoveryManifest
        {
            SchemaVersion = ArchiveRecoveryManifest.CurrentSchemaVersion,
            TransactionId = transactionId,
            DestinationPath = archivePath,
            TemporaryPath = temporary,
            BackupPath = backup,
            OriginalExisted = true,
            State = ArchiveRecoveryState.Published,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var service = new SafeArchiveService(storage: storage);

        var report = await service.RecoverAsync();

        Assert.True(report.IsSuccessful);
        Assert.Equal(1, report.RecoveredTransactions);
        Assert.Equal("original", await ReadZipTextAsync(archivePath, "value.txt"));
        Assert.False(File.Exists(backup));
    }

    [Fact]
    public async Task ArchiveRecovery_RejectsCorruptReplacementAndPreservesOriginalBackup()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("corrupt-recovery.zip", ("value.txt", "original"));
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var transactionId = Guid.NewGuid();
        var temporary = RecoveryTemporaryPath(archivePath, transactionId);
        var backup = archivePath + $".odyssey-backup-{transactionId:N}";
        File.Move(archivePath, backup);
        await File.WriteAllBytesAsync(temporary, [0x50, 0x4b, 0x03, 0x04, 0xff]);
        var manifestPath = WriteRecoveryManifest(storage, new ArchiveRecoveryManifest
        {
            SchemaVersion = ArchiveRecoveryManifest.CurrentSchemaVersion,
            TransactionId = transactionId,
            DestinationPath = archivePath,
            TemporaryPath = temporary,
            BackupPath = backup,
            OriginalExisted = true,
            State = ArchiveRecoveryState.OriginalBackedUp,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var service = new SafeArchiveService(storage: storage);

        var report = await service.RecoverAsync();

        Assert.False(report.IsSuccessful);
        Assert.False(File.Exists(archivePath));
        Assert.Equal("original", await ReadZipTextAsync(backup, "value.txt"));
        Assert.True(File.Exists(temporary));
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public async Task ArchiveRecovery_RejectsForgedArtifactPathsWithoutTouchingVictim()
    {
        Directory.CreateDirectory(_root);
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var archivePath = Path.Combine(_root, "forged.zip");
        var victim = CreateZip("victim.zip", ("value.txt", "untouched"));
        var before = await File.ReadAllBytesAsync(victim);
        var transactionId = Guid.NewGuid();
        var manifestPath = WriteRecoveryManifest(storage, new ArchiveRecoveryManifest
        {
            SchemaVersion = ArchiveRecoveryManifest.CurrentSchemaVersion,
            TransactionId = transactionId,
            DestinationPath = archivePath,
            TemporaryPath = victim,
            BackupPath = null,
            OriginalExisted = false,
            State = ArchiveRecoveryState.Prepared,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var service = new SafeArchiveService(storage: storage);

        var report = await service.RecoverAsync();

        Assert.False(report.IsSuccessful);
        Assert.Equal(before, await File.ReadAllBytesAsync(victim));
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public async Task TransferQueue_RemovesOnlyGuidNamedStaleRelaysWithoutFollowingLinks()
    {
        Directory.CreateDirectory(_root);
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var relayRoot = Directory.CreateDirectory(Path.Combine(storage.DirectoryPath, "archive-relay")).FullName;
        var stale = Directory.CreateDirectory(Path.Combine(relayRoot, $"relay-{Guid.NewGuid():N}")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-relay")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "keep.txt"), "keep");
        Directory.CreateSymbolicLink(Path.Combine(stale, "outside-link"), outside);
        await File.WriteAllTextAsync(Path.Combine(stale, "temporary.txt"), "temporary");
        var unrelated = Directory.CreateDirectory(Path.Combine(relayRoot, "relay-not-a-guid")).FullName;
        var operations = new SafeFileOperationService(storage) { AccessMode = FileAccessMode.ManageFiles };

        await using var queue = new FileTransferQueueService(operations, storage);
        await queue.InitializeAsync();

        Assert.False(Directory.Exists(stale));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(outside, "keep.txt")));
        Assert.True(Directory.Exists(unrelated));
    }

    private static string RecoveryTemporaryPath(string archivePath, Guid transactionId) =>
        Path.Combine(Path.GetDirectoryName(archivePath)!,
            $".{Path.GetFileNameWithoutExtension(archivePath)}.odyssey-new-{transactionId:N}{Path.GetExtension(archivePath)}");

    private static string WriteRecoveryManifest(ApplicationStorage storage, ArchiveRecoveryManifest manifest)
    {
        var root = Directory.CreateDirectory(Path.Combine(storage.DirectoryPath, "archive-recovery")).FullName;
        var path = Path.Combine(root, $".odyssey-archive-recovery-{manifest.TransactionId:N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }

    private static void CreateZipAt(string path, params (string Path, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
    }

    private static void CreateTarAt(string path, params (string Path, string Content)[] entries)
    {
        using var stream = File.Create(path);
        using var archive = new TarWriter(stream, leaveOpen: false);
        foreach (var item in entries)
        {
            using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(item.Content));
            archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.Path)
            {
                DataStream = data,
                ModificationTime = DateTimeOffset.UnixEpoch
            });
        }
    }

    private static async Task<string> ReadZipTextAsync(string archivePath, string entryPath)
    {
        await using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        await using var entry = archive.GetEntry(entryPath)!.Open();
        using var reader = new StreamReader(entry);
        return await reader.ReadToEndAsync();
    }

    private static async Task<string> ReadTarTextAsync(string archivePath, string entryPath)
    {
        await using var stream = File.OpenRead(archivePath);
        await using var archive = new TarReader(stream, leaveOpen: false);
        while (await archive.GetNextEntryAsync(copyData: false) is { } entry)
        {
            if (!string.Equals(entry.Name, entryPath, StringComparison.Ordinal)) continue;
            Assert.NotNull(entry.DataStream);
            using var reader = new StreamReader(entry.DataStream!, leaveOpen: true);
            return await reader.ReadToEndAsync();
        }
        throw new Xunit.Sdk.XunitException($"TAR entry was not found: {entryPath}");
    }

    private string CreateZip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path, CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
        return path;
    }

    private static async Task WaitForStateAsync(IFileTransferQueueService queue, Guid id, FileTransferState expected)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeout)
        {
            if (queue.Items.Single(item => item.Id == id).State == expected) return;
            await Task.Delay(20);
        }
        Assert.Equal(expected, queue.Items.Single(item => item.Id == id).State);
    }

    private static async Task<string> ReadAllTextWhenAvailableAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { return await File.ReadAllTextAsync(path); }
            catch (Exception exception) when (DateTime.UtcNow < deadline
                                               && exception is IOException or UnauthorizedAccessException)
            {
                // The queue publishes atomically. Windows can briefly deny a reader while
                // the destination name is being replaced, so observe the completed snapshot
                // after that bounded publication window instead of racing the writer.
                await Task.Delay(20);
            }
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class BlockingArchiveService : IArchiveService
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileAccessMode AccessMode { get; set; }
        public IReadOnlyList<ArchiveFormatSupport> Formats => [];
        public bool CanOpen(string archivePath) => true;
        public ArchiveCapabilities GetCapabilities(string archivePath) =>
            ArchiveCapabilities.Browse | ArchiveCapabilities.Extract;
        public Task<IReadOnlyList<ArchiveEntry>> ListAsync(
            string archivePath, string directoryPath = "", CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ArchiveEntry>>([]);

        public async Task<FileTransferOutcome> ExtractAsync(
            string archivePath,
            string entryPath,
            string destinationDirectory,
            FileConflictPolicy conflictPolicy = FileConflictPolicy.Fail,
            IProgress<FileOperationProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocking test archive service unexpectedly resumed.");
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
