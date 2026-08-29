using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class FilePaneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-pane-{Guid.NewGuid():N}");

    [Fact]
    public void Pane_BrowsesIndependentlyAndNeverNavigatesAboveTarget()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_root, "one.txt"), "1");
        File.WriteAllText(Path.Combine(nested, "two.txt"), "22");
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService()) { SelectedTarget = Target(_root) };
        WaitForLoad(pane);

        Assert.Contains(pane.Entries, item => item.Name == "nested" && item.Type == FileEntryType.Directory);
        pane.SetSelection([pane.Entries.Single(item => item.Name == "nested")]);
        pane.OpenSelectedDirectory();
        WaitForLoad(pane);

        Assert.Equal(Path.GetFullPath(nested), pane.CurrentPath);
        Assert.Contains(pane.Entries, item => item.IsParent);
        Assert.True(pane.CanGoBack);
        pane.GoBack();
        WaitForLoad(pane);
        Assert.Equal(Path.GetFullPath(_root), pane.CurrentPath);
        Assert.True(pane.CanGoForward);
        pane.GoForward();
        WaitForLoad(pane);
        Assert.Equal(Path.GetFullPath(nested), pane.CurrentPath);
        pane.NavigateUp();
        WaitForLoad(pane);
        pane.NavigateUp();
        Assert.Equal(Path.GetFullPath(_root), pane.CurrentPath);
    }

    [Fact]
    public void Pane_MultipleSelectionReportsCountAndBytes()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "one.txt"), "1");
        File.WriteAllText(Path.Combine(_root, "two.txt"), "22");
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService()) { SelectedTarget = Target(_root) };
        WaitForLoad(pane);

        pane.SetSelection(pane.Entries.Where(item => item.Type == FileEntryType.File));

        Assert.Equal(2, pane.SelectedEntries.Count);
        Assert.Contains("2", pane.SelectionSummary, StringComparison.Ordinal);
        Assert.Contains("3 B", pane.SelectionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPanes_KeepSeparateTargetsAndPaths()
    {
        var leftRoot = Path.Combine(_root, "left");
        var rightRoot = Path.Combine(_root, "right");
        Directory.CreateDirectory(leftRoot);
        Directory.CreateDirectory(rightRoot);
        var browser = new CachedDirectoryBrowserService();
        var left = new FilePaneViewModel(browser) { SelectedTarget = Target(leftRoot) };
        var right = new FilePaneViewModel(browser) { SelectedTarget = Target(rightRoot) };
        WaitForLoad(left); WaitForLoad(right);

        Assert.Equal(Path.GetFullPath(leftRoot), left.CurrentPath);
        Assert.Equal(Path.GetFullPath(rightRoot), right.CurrentPath);
    }

    [Fact]
    public async Task Pane_LoadsLargeDirectoryInBoundedPages()
    {
        Directory.CreateDirectory(_root);
        var pane = new FilePaneViewModel(new FixedPagedBrowser(450)) { SelectedTarget = Target(_root) };
        WaitForLoad(pane);

        Assert.Equal(400, pane.Entries.Count);
        Assert.True(pane.HasMore);

        await pane.LoadNextPageAsync();
        Assert.Equal(450, pane.Entries.Count);
        Assert.False(pane.HasMore);
    }

    private static ScanTarget Target(string path) => new()
    {
        Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), RootPath = path
    };

    private static void WaitForLoad(FilePaneViewModel pane)
    {
        Assert.True(SpinWait.SpinUntil(() => !pane.IsLoading && (!pane.HasMore || pane.Entries.Count > 0), TimeSpan.FromSeconds(3)));
    }

    private sealed class FixedPagedBrowser(int count) : IDirectoryBrowserService
    {
        private readonly DirectoryItem[] _items = Enumerable.Range(0, count)
            .Select(index => new DirectoryItem($"file-{index}.txt", $"/virtual/file-{index}.txt",
                FileEntryType.File, index, DateTimeOffset.UtcNow, "txt", "----"))
            .ToArray();

        public Task<DirectoryPage> GetPageAsync(string path, int offset, int pageSize, CancellationToken cancellationToken = default)
        {
            var page = _items.Skip(offset).Take(pageSize).ToArray();
            return Task.FromResult(new DirectoryPage(page, offset, _items.Length, offset + page.Length < _items.Length));
        }

        public void Invalidate(string path) { }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
