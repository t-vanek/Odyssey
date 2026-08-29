using System.IO.Compression;
using System.Formats.Tar;
using System.Text;
using Odyssey.Core;
using Odyssey.Infrastructure;
using SharpSevenZip;

namespace Odyssey.Tests;

public sealed class ArchiveQuickViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-archive-preview-{Guid.NewGuid():N}");

    [Fact]
    public async Task ZipEntry_IsPreviewedInBoundedUtf8BlocksWithoutTemporaryFiles()
    {
        Directory.CreateDirectory(_root);
        var expected = new string('a', 1023) + "€" + new string('b', 4096);
        var archivePath = CreateZip("large.zip", ("folder/large.txt", expected));
        var service = new SafeArchiveService();
        var quickView = new QuickViewService(service);

        var first = await quickView.ReadAsync(new QuickViewReadRequest
        {
            Path = $"{archivePath}!/folder/large.txt",
            Endpoint = FileTransferEndpointKind.Archive,
            ContainerPath = archivePath,
            EntryPath = "folder/large.txt",
            MaximumBytes = 1024
        });
        var second = await quickView.ReadAsync(new QuickViewReadRequest
        {
            Path = $"{archivePath}!/folder/large.txt",
            Endpoint = FileTransferEndpointKind.Archive,
            ContainerPath = archivePath,
            EntryPath = "folder/large.txt",
            Offset = first.NextOffset,
            MaximumBytes = 1024,
            ExpectedVersion = first.Version
        });

        Assert.Equal(1023, first.BytesRead);
        Assert.Equal(new string('a', 1023) + "€" + new string('b', 1021), first.Content + second.Content);
        Assert.NotNull(first.Version.ContainerLength);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root),
            path => Path.GetFileName(path).Contains("preview", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TarEntry_SupportsTextAndHexBlocks()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateTar("sample.tar", ("folder/value.bin", "hello\0world"));
        var service = new QuickViewService(new SafeArchiveService());

        var automatic = await service.ReadAsync(ArchiveRequest(archivePath, "folder/value.bin"));
        var text = await service.ReadAsync(ArchiveRequest(archivePath, "folder/value.bin") with
        {
            Mode = QuickViewDisplayMode.Text,
            EncodingName = "utf-8"
        });

        Assert.Equal(QuickViewDisplayMode.Hex, automatic.EffectiveMode);
        Assert.Contains("68 65 6C 6C 6F 00 77 6F", automatic.Content, StringComparison.Ordinal);
        Assert.Contains("72 6C 64", automatic.Content, StringComparison.Ordinal);
        Assert.Equal("hello\0world", text.Content);
    }

    [Fact]
    public async Task ChangedArchive_InvalidatesThePreviewVersion()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("changing.zip", ("value.txt", new string('a', 4096)));
        var service = new QuickViewService(new SafeArchiveService());
        var first = await service.ReadAsync(ArchiveRequest(archivePath, "value.txt") with
        {
            MaximumBytes = 1024
        });
        CreateZipAt(archivePath, ("value.txt", new string('b', 8192)));

        await Assert.ThrowsAsync<IOException>(() => service.ReadAsync(
            ArchiveRequest(archivePath, "value.txt") with
            {
                Offset = first.NextOffset,
                MaximumBytes = 1024,
                ExpectedVersion = first.Version
            }));
    }

    [Fact]
    public async Task TraversalLinkAndCorruptArchivesAreRejectedBeforePreviewPublication()
    {
        Directory.CreateDirectory(_root);
        var traversal = CreateZip("traversal.zip", ("../escape.txt", "hostile"));
        var linked = Path.Combine(_root, "link.zip");
        using (var stream = File.Create(linked))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = unchecked((0xA000 | 0x1FF) << 16);
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync("outside");
        }
        var corrupt = Path.Combine(_root, "corrupt.zip");
        await File.WriteAllBytesAsync(corrupt, [1, 2, 3, 4, 5]);
        var service = new SafeArchiveService();

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadBlockAsync(
            traversal, "../escape.txt", 0, 1024));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadBlockAsync(
            linked, "link", 0, 1024));
        await Assert.ThrowsAsync<InvalidDataException>(() => service.ReadBlockAsync(
            corrupt, "anything", 0, 1024));
        Assert.False(File.Exists(Path.Combine(_root, "escape.txt")));
    }

    [Fact]
    public async Task ArchiveBelowLinkedAncestorIsRejected()
    {
        Directory.CreateDirectory(_root);
        var realDirectory = Path.Combine(_root, "real");
        Directory.CreateDirectory(realDirectory);
        var archivePath = Path.Combine(realDirectory, "linked-parent.zip");
        CreateZipAt(archivePath, ("inside.txt", "never cross the link"));
        var linkedDirectory = Path.Combine(_root, "linked");
        Directory.CreateSymbolicLink(linkedDirectory, realDirectory);

        await Assert.ThrowsAsync<IOException>(() => new SafeArchiveService().ReadBlockAsync(
            Path.Combine(linkedDirectory, "linked-parent.zip"), "inside.txt", 0, 1024));
    }

    [Fact]
    public async Task CancellationDuringArchiveStreamingStopsWithoutArtifacts()
    {
        Directory.CreateDirectory(_root);
        var archivePath = CreateZip("cancel.zip", ("large.txt", new string('x', 256 * 1024)));
        using var cancellation = new CancellationTokenSource();
        var service = new SafeArchiveService
        {
            PreviewReadCheckpoint = _ => cancellation.Cancel()
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadBlockAsync(
            archivePath, "large.txt", 128 * 1024, 64 * 1024,
            cancellationToken: cancellation.Token));

        Assert.Equal([archivePath], Directory.EnumerateFiles(_root).ToArray());
    }

    [Fact]
    public async Task NativeSevenZipEntry_IsBoundedWhenEngineIsAvailable()
    {
        Directory.CreateDirectory(_root);
        var archiveService = new SafeArchiveService();
        if (archiveService.Formats.Single(item => item.Name == "7z").IsAvailable is false) return;
        var source = Path.Combine(_root, "native-preview.txt");
        await File.WriteAllTextAsync(source, new string('n', 8192));
        var archivePath = Path.Combine(_root, "native.7z");
        new SharpSevenZipCompressor { ArchiveFormat = OutArchiveFormat.SevenZip }
            .CompressFiles(archivePath, source);

        var chunk = await new QuickViewService(archiveService).ReadAsync(
            ArchiveRequest(archivePath, Path.GetFileName(source)) with { MaximumBytes = 1024 });

        Assert.Equal(1024, chunk.BytesRead);
        Assert.Equal(new string('n', 1024), chunk.Content);
        Assert.True(chunk.HasNext);
    }

    private static QuickViewReadRequest ArchiveRequest(string archivePath, string entryPath) => new()
    {
        Path = $"{archivePath}!/{entryPath}",
        Endpoint = FileTransferEndpointKind.Archive,
        ContainerPath = archivePath,
        EntryPath = entryPath,
        MaximumBytes = 1024
    };

    private string CreateZip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        CreateZipAt(path, entries);
        return path;
    }

    private static void CreateZipAt(string path, params (string Path, string Content)[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path, System.IO.Compression.CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(item.Content);
        }
    }

    private string CreateTar(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var stream = File.Create(path);
        using var archive = new TarWriter(stream, leaveOpen: false);
        foreach (var item in entries)
        {
            using var data = new MemoryStream(Encoding.UTF8.GetBytes(item.Content));
            archive.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, item.Path)
            {
                DataStream = data,
                ModificationTime = DateTimeOffset.UnixEpoch
            });
        }
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
