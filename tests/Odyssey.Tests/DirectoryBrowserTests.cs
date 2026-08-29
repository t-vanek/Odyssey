using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class DirectoryBrowserTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-browser-{Guid.NewGuid():N}");

    [Fact]
    public async Task DirectoryPages_AreBoundedSortedAndIncremental()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "folder"));
        for (var index = 0; index < 25; index++)
            await File.WriteAllTextAsync(Path.Combine(_root, $"file-{index:00}.txt"), index.ToString());
        var browser = new CachedDirectoryBrowserService();

        var first = await browser.GetPageAsync(_root, 0, 10);
        var second = await browser.GetPageAsync(_root, 10, 10);

        Assert.Equal(10, first.Items.Count);
        Assert.Equal(26, first.TotalCount);
        Assert.True(first.HasMore);
        Assert.Equal("folder", first.Items[0].Name);
        Assert.Empty(first.Items.Select(item => item.FullPath).Intersect(second.Items.Select(item => item.FullPath)));
    }

    [Fact]
    public async Task Invalidate_RefreshesCachedDirectorySnapshot()
    {
        Directory.CreateDirectory(_root);
        var browser = new CachedDirectoryBrowserService();
        Assert.Empty((await browser.GetPageAsync(_root, 0, 100)).Items);
        await File.WriteAllTextAsync(Path.Combine(_root, "new.txt"), "new");

        browser.Invalidate(_root);
        var refreshed = await browser.GetPageAsync(_root, 0, 100);

        Assert.Single(refreshed.Items);
    }

    [Fact]
    public async Task LocationRegistry_ProvidesLocalBrowserAndHidesTransferArtifacts()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "visible.txt"), "visible");
        await File.WriteAllTextAsync(Path.Combine(_root, $"visible.txt.odyssey-part-{Guid.NewGuid():N}"), "partial");
        var local = new LocalFileLocationProvider();
        var registry = new FileLocationProviderRegistry([local]);

        var entries = await registry.Get(FileTransferEndpointKind.Local).ListAsync(_root);

        Assert.Single(entries);
        Assert.Equal("visible.txt", entries[0].Name);
        Assert.True(local.Capabilities.HasFlag(FileLocationCapabilities.Write));
    }

    [Fact]
    public async Task FilteredBrowser_PaginatesAcrossTheCompleteDirectorySnapshot()
    {
        Directory.CreateDirectory(_root);
        for (var index = 0; index < 425; index++)
            File.WriteAllText(Path.Combine(_root, $"match-{index:000}.cs"), "x");
        for (var index = 0; index < 25; index++)
            File.WriteAllText(Path.Combine(_root, $"other-{index:000}.md"), "x");
        var browser = new CachedDirectoryBrowserService();

        var first = await browser.GetFilteredPageAsync(_root, 0, 400,
            new DirectoryNameFilter("*.cs", FileNameFilterMode.Glob));
        var second = await browser.GetFilteredPageAsync(_root, 400, 400,
            new DirectoryNameFilter("*.cs", FileNameFilterMode.Glob));

        Assert.Equal(425, first.TotalCount);
        Assert.Equal(400, first.Items.Count);
        Assert.True(first.HasMore);
        Assert.Equal(25, second.Items.Count);
        Assert.False(second.HasMore);
        Assert.All(first.Items.Concat(second.Items), item => Assert.Equal("cs", item.Extension));
    }

    [Fact]
    public async Task FilteredBrowser_RejectsInvalidRegexWithoutEnumeratingResults()
    {
        Directory.CreateDirectory(_root);
        var browser = new CachedDirectoryBrowserService();

        await Assert.ThrowsAsync<ArgumentException>(() => browser.GetFilteredPageAsync(
            _root, 0, 100, new DirectoryNameFilter("[", FileNameFilterMode.Regex)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
