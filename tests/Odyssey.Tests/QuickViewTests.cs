using System.Text;
using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class QuickViewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-quick-view-{Guid.NewGuid():N}");

    [Fact]
    public async Task LargeText_IsReadInBoundedChunksWithStableVersion()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "large.txt");
        var block = Enumerable.Repeat((byte)'a', 64 * 1024).ToArray();
        await using (var writer = File.Create(path))
            for (var index = 0; index < 160; index++) await writer.WriteAsync(block);
        var service = new QuickViewService();

        var first = await service.ReadAsync(new QuickViewReadRequest
        {
            Path = path, MaximumBytes = 32 * 1024
        });
        var second = await service.ReadAsync(new QuickViewReadRequest
        {
            Path = path, Offset = first.NextOffset, MaximumBytes = 32 * 1024,
            ExpectedVersion = first.Version
        });

        Assert.Equal(10 * 1024 * 1024, first.Version.Length);
        Assert.Equal(32 * 1024, first.BytesRead);
        Assert.Equal(32 * 1024, second.BytesRead);
        Assert.Equal(first.NextOffset, second.Offset);
        Assert.True(first.HasNext);
        Assert.True(second.HasPrevious);
        Assert.Equal(32 * 1024, first.Content.Length);
    }

    [Fact]
    public async Task AutoEncoding_DetectsUtf8AndUtf16AndAllowsExplicitLatin1()
    {
        Directory.CreateDirectory(_root);
        var utf8 = Path.Combine(_root, "utf8.txt");
        var utf16 = Path.Combine(_root, "utf16.txt");
        var latin = Path.Combine(_root, "latin.txt");
        await File.WriteAllTextAsync(utf8, "Příliš žluťoučký kůň", new UTF8Encoding(true));
        await File.WriteAllTextAsync(utf16, "Český text", Encoding.Unicode);
        await File.WriteAllBytesAsync(latin, [0x63, 0x61, 0x66, 0xE9]);
        var service = new QuickViewService();

        var utf8Chunk = await service.ReadAsync(new QuickViewReadRequest { Path = utf8 });
        var utf16Chunk = await service.ReadAsync(new QuickViewReadRequest { Path = utf16 });
        var latinChunk = await service.ReadAsync(new QuickViewReadRequest
        {
            Path = latin, Mode = QuickViewDisplayMode.Text, EncodingName = "iso-8859-1"
        });

        Assert.Equal("utf-8", utf8Chunk.EncodingName);
        Assert.Equal("Příliš žluťoučký kůň", utf8Chunk.Content);
        Assert.Equal("utf-16", utf16Chunk.EncodingName);
        Assert.Equal("Český text", utf16Chunk.Content);
        Assert.Equal("café", latinChunk.Content);
    }

    [Fact]
    public async Task Utf8CharacterSplitAtBlockBoundaryIsReReadWithoutReplacementOrLoss()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "boundary.txt");
        var expected = new string('a', 1023) + "€tail";
        await File.WriteAllTextAsync(path, expected, new UTF8Encoding(false));
        var service = new QuickViewService(1024);

        var first = await service.ReadAsync(new QuickViewReadRequest
        {
            Path = path, MaximumBytes = 1024
        });
        var second = await service.ReadAsync(new QuickViewReadRequest
        {
            Path = path, Offset = first.NextOffset, MaximumBytes = 1024,
            ExpectedVersion = first.Version
        });

        Assert.Equal(1023, first.BytesRead);
        Assert.Equal(expected, first.Content + second.Content);
        Assert.DoesNotContain('\uFFFD', first.Content + second.Content);
    }

    [Fact]
    public async Task AutoMode_UsesHexForBinaryAndFormatsExactOffsets()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "binary.bin");
        await File.WriteAllBytesAsync(path, [0x00, 0x01, 0x20, 0x41, 0xFF]);
        var service = new QuickViewService();

        var chunk = await service.ReadAsync(new QuickViewReadRequest { Path = path });

        Assert.Equal(QuickViewDisplayMode.Hex, chunk.EffectiveMode);
        Assert.True(chunk.IsBinary);
        Assert.Contains("0000000000000000  00 01 20 41 FF", chunk.Content, StringComparison.Ordinal);
        Assert.EndsWith("|.. A.|", chunk.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedSource_IsRejectedBeforeTheNextChunk()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "changing.txt");
        await File.WriteAllTextAsync(path, new string('x', 4096));
        var service = new QuickViewService();
        var first = await service.ReadAsync(new QuickViewReadRequest
        {
            Path = path, MaximumBytes = 1024
        });
        await File.AppendAllTextAsync(path, "changed");

        await Assert.ThrowsAsync<IOException>(() => service.ReadAsync(new QuickViewReadRequest
        {
            Path = path, Offset = first.NextOffset, MaximumBytes = 1024,
            ExpectedVersion = first.Version
        }));
    }

    [Fact]
    public async Task CancellationInterruptsReadWithoutChangingSource()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cancel.txt");
        await File.WriteAllTextAsync(path, "unchanged");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new QuickViewService
        {
            ReadOperation = async (_, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
        };
        using var cancellation = new CancellationTokenSource();
        var read = service.ReadAsync(new QuickViewReadRequest { Path = path }, cancellation.Token);
        await started.Task;
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ClosingViewerCancelsAnInFlightReadAndClearsContent()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "close.txt");
        await File.WriteAllTextAsync(path, "unchanged");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new QuickViewService
        {
            ReadOperation = async (_, _, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
        };
        var viewModel = new QuickViewViewModel(service,
            new LocalizationService(new ApplicationStorage(Path.Combine(_root, "settings"))));
        var opening = viewModel.OpenAsync(path);
        await started.Task;
        Assert.True(viewModel.IsBusy);

        viewModel.Close();
        await opening;

        Assert.False(viewModel.IsOpen);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(viewModel.Content);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LinkOrLinkedAncestor_IsRejected()
    {
        Directory.CreateDirectory(_root);
        var real = Path.Combine(_root, "real");
        var linked = Path.Combine(_root, "linked");
        Directory.CreateDirectory(real);
        var target = Path.Combine(real, "target.txt");
        await File.WriteAllTextAsync(target, "target");
        var fileLink = Path.Combine(_root, "file-link.txt");
        File.CreateSymbolicLink(fileLink, target);
        Directory.CreateSymbolicLink(linked, real);
        var service = new QuickViewService();

        await Assert.ThrowsAsync<IOException>(() => service.ReadAsync(
            new QuickViewReadRequest { Path = fileLink }));
        await Assert.ThrowsAsync<IOException>(() => service.ReadAsync(
            new QuickViewReadRequest { Path = Path.Combine(linked, "target.txt") }));
    }

    [Fact]
    public async Task InvalidOffsetChunkSizeAndEncodingAreRejected()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(path, "source");
        var service = new QuickViewService(2048);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadAsync(
            new QuickViewReadRequest { Path = path, Offset = -1 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.ReadAsync(
            new QuickViewReadRequest { Path = path, MaximumBytes = 2049 }));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ReadAsync(
            new QuickViewReadRequest
            {
                Path = path, MaximumBytes = 1024, EncodingName = "dangerous-code-page"
            }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
