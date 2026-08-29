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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
