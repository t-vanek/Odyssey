using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class FileSystemScannerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-scanner-{Guid.NewGuid():N}");
    private readonly PortableFileSystemScanner _scanner = new(new ConfigurableExclusionPolicy());

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "nested"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        File.WriteAllText(Path.Combine(_root, "root.txt"), "root");
        File.WriteAllText(Path.Combine(_root, "nested", "child.pdf"), "child");
        File.WriteAllText(Path.Combine(_root, "node_modules", "ignored.js"), "ignored");
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RecursiveScan_StreamsNestedFiles_AndHonorsDefaultExclusions()
    {
        var items = await ReadAsync(new ScanTarget { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), RootPath = _root });

        Assert.Contains(items, x => x.FullPath.EndsWith("child.pdf", StringComparison.Ordinal));
        Assert.DoesNotContain(items, x => x.FullPath.Contains("node_modules", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(items, x => x.Type == FileEntryType.Directory && x.FullPath.EndsWith("nested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonRecursiveScan_DoesNotEnterChildDirectory()
    {
        var items = await ReadAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            RootPath = _root,
            Recursive = false
        });

        Assert.Contains(items, x => x.FullPath.EndsWith("root.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(items, x => x.FullPath.EndsWith("child.pdf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConfiguredWildcardExclusion_IsApplied()
    {
        var items = await ReadAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            RootPath = _root,
            ExcludedPatterns = ["*.txt"]
        });
        Assert.DoesNotContain(items, x => x.FullPath.EndsWith("root.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IncludedExtensions_FilterFilesButNotTraversal()
    {
        var items = await ReadAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            RootPath = _root,
            IncludedExtensions = ["pdf"]
        });
        Assert.Contains(items, x => x.FullPath.EndsWith("child.pdf", StringComparison.Ordinal));
        Assert.DoesNotContain(items, x => x.FullPath.EndsWith("root.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancellation_IsObserved()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ReadAsync(
            new ScanTarget { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), RootPath = _root }, cancellation.Token));
    }

    [Fact]
    public async Task UnavailableOrDisappearedTarget_IsReportedWithoutCrash()
    {
        var missing = Path.Combine(_root, "disappeared");
        var items = await ReadAsync(new ScanTarget { Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), RootPath = missing });
        var error = Assert.Single(items);
        Assert.NotNull(error.Error);
    }

    private async Task<List<FileSystemItem>> ReadAsync(ScanTarget target, CancellationToken token = default)
    {
        var result = new List<FileSystemItem>();
        await foreach (var item in _scanner.EnumerateAsync(target, token)) result.Add(item);
        return result;
    }
}
