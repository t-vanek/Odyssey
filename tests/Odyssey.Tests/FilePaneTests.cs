using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;
using System.IO.Compression;

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
    public void EmptyFolder_ExposesCalmEmptyPresentationState()
    {
        Directory.CreateDirectory(_root);
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService()) { SelectedTarget = Target(_root) };
        WaitForLoad(pane);

        Assert.True(pane.ShowEmptyState);
        Assert.False(pane.ShowInitialLoading);
        Assert.False(pane.ShowErrorState);
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

    [Fact]
    public void Pane_QuickFilterSupportsGlobRegexAndInvalidPatternSafety()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "alpha.cs"), "1");
        File.WriteAllText(Path.Combine(_root, "beta.md"), "2");
        File.WriteAllText(Path.Combine(_root, "gamma.cs"), "3");
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService()) { SelectedTarget = Target(_root) };
        WaitForLoad(pane);

        pane.FilterMode = FileNameFilterMode.Glob;
        pane.FilterText = "*.cs";
        WaitForLoad(pane);

        Assert.Equal(["alpha.cs", "gamma.cs"], pane.Entries.Select(item => item.Name).Order().ToArray());
        Assert.Equal(2, pane.TotalItems);

        pane.FilterMode = FileNameFilterMode.Regex;
        pane.FilterText = "[";

        Assert.True(pane.HasFilterError);
        Assert.Equal(["alpha.cs", "gamma.cs"], pane.Entries.Select(item => item.Name).Order().ToArray());

        pane.FilterText = "^beta\\.md$";
        WaitForLoad(pane);
        Assert.False(pane.HasFilterError);
        Assert.Equal("beta.md", Assert.Single(pane.Entries).Name);
    }

    [Fact]
    public void Pane_SelectionOperationsAreStableAndRestorePreviousSelection()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "alpha.cs"), "1");
        File.WriteAllText(Path.Combine(_root, "beta.md"), "2");
        File.WriteAllText(Path.Combine(_root, "gamma.cs"), "3");
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService()) { SelectedTarget = Target(_root) };
        WaitForLoad(pane);
        pane.SetSelection([pane.Entries.Single(item => item.Name == "alpha.cs")]);

        pane.SelectByExtension();
        Assert.Equal(["alpha.cs", "gamma.cs"], pane.SelectedEntries.Select(item => item.Name).Order().ToArray());

        pane.InvertSelection();
        Assert.Equal("beta.md", Assert.Single(pane.SelectedEntries).Name);

        pane.RestorePreviousSelection();
        Assert.Equal(["alpha.cs", "gamma.cs"], pane.SelectedEntries.Select(item => item.Name).Order().ToArray());

        Assert.True(pane.SelectByMask("*.md", FileNameFilterMode.Glob));
        Assert.Equal("beta.md", Assert.Single(pane.SelectedEntries).Name);
        Assert.False(pane.SelectByMask("[", FileNameFilterMode.Regex));
        Assert.Equal("beta.md", Assert.Single(pane.SelectedEntries).Name);
    }

    [Fact]
    public void Tabs_KeepIndependentPathsFiltersAndHistory()
    {
        var first = Path.Combine(_root, "first");
        var second = Path.Combine(_root, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(first, "alpha.txt"), "1");
        File.WriteAllText(Path.Combine(second, "beta.md"), "2");
        var target = Target(_root);
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService()) { SelectedTarget = target };
        pane.SetKnownTargets([target]);
        WaitForLoad(pane);
        pane.BrowseTo(first);
        WaitForLoad(pane);
        pane.FilterText = "alpha";
        WaitForLoad(pane);
        var firstTab = pane.SelectedTab;

        pane.NewTab();
        pane.BrowseTo(second);
        WaitForLoad(pane);
        pane.FilterMode = FileNameFilterMode.Glob;
        pane.FilterText = "*.md";
        WaitForLoad(pane);
        var secondTab = pane.SelectedTab;

        pane.SelectedTab = firstTab;
        WaitForLoad(pane);
        Assert.Equal(Path.GetFullPath(first), pane.CurrentPath);
        Assert.Equal("alpha", pane.FilterText);
        Assert.True(pane.CanGoBack);

        pane.SelectedTab = secondTab;
        WaitForLoad(pane);
        Assert.Equal(Path.GetFullPath(second), pane.CurrentPath);
        Assert.Equal("*.md", pane.FilterText);
        Assert.Equal(FileNameFilterMode.Glob, pane.FilterMode);
        Assert.True(pane.CanGoBack);
        pane.GoBack();
        WaitForLoad(pane);
        Assert.Equal(Path.GetFullPath(first), pane.CurrentPath);
    }

    [Fact]
    public void Tabs_CloseDuplicateAndReopenPreserveTheClosedState()
    {
        var nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        var target = Target(_root);
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService()) { SelectedTarget = target };
        pane.SetKnownTargets([target]);
        WaitForLoad(pane);
        pane.BrowseTo(nested);
        WaitForLoad(pane);
        pane.FilterText = "needle";
        WaitForLoad(pane);

        pane.DuplicateTab();
        WaitForLoad(pane);
        Assert.Equal(2, pane.Tabs.Count);
        Assert.Equal("needle", pane.FilterText);
        pane.CloseTab();
        Assert.Single(pane.Tabs);
        Assert.True(pane.CanReopenClosedTab);

        pane.ReopenClosedTab();
        WaitForLoad(pane);
        Assert.Equal(2, pane.Tabs.Count);
        Assert.Equal(Path.GetFullPath(nested), pane.CurrentPath);
        Assert.Equal("needle", pane.FilterText);
    }

    [Fact]
    public void WorkspaceRestore_KeepsUnavailableTabWithoutEscapingItsTarget()
    {
        Directory.CreateDirectory(_root);
        var missing = Path.Combine(_root, "disconnected");
        var target = Target(_root);
        var snapshot = new FilePaneWorkspaceSnapshot(
            [new FilePaneTabSnapshot(Guid.NewGuid(), missing, _root, string.Empty,
                FileNameFilterMode.Contains, false, [], [])], null);
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService());

        pane.RestoreWorkspace(snapshot, [target]);

        Assert.Single(pane.Tabs);
        Assert.Equal(Path.GetFullPath(missing), pane.CurrentPath);
        Assert.Equal(Path.GetFullPath(missing), pane.LastError);
        Assert.Empty(pane.Entries);
        Assert.Equal(target.Id, pane.SelectedTarget?.Id);
    }

    [Fact]
    public void WorkspaceRestore_DoesNotBrowsePersistedPathOutsideTargetBoundary()
    {
        var targetRoot = Path.Combine(_root, "target");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(targetRoot);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "must-not-appear.txt"), "secret");
        var target = Target(targetRoot);
        var tabId = Guid.NewGuid();
        var snapshot = new FilePaneWorkspaceSnapshot(
            [new FilePaneTabSnapshot(tabId, outside, targetRoot, string.Empty,
                FileNameFilterMode.Contains, false, [], [])], tabId);
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService());

        pane.RestoreWorkspace(snapshot, [target]);

        Assert.Equal(Path.GetFullPath(outside), pane.CurrentPath);
        Assert.Equal(Path.GetFullPath(outside), pane.LastError);
        Assert.Empty(pane.Entries);
        Assert.Equal(target.Id, pane.SelectedTarget?.Id);
    }

    [Fact]
    public void ArchiveTab_NavigatesFiltersAndRestoresItsVirtualLocation()
    {
        Directory.CreateDirectory(_root);
        var archivePath = Path.Combine(_root, "project.zip");
        using (var stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("src/main.cs").Open()))
                writer.Write("class Main;");
            using (var writer = new StreamWriter(archive.CreateEntry("src/readme.txt").Open()))
                writer.Write("read me");
        }
        var target = Target(_root);
        var archives = new SafeArchiveService();
        var pane = new FilePaneViewModel(new CachedDirectoryBrowserService(), archives)
            { SelectedTarget = target };
        pane.SetKnownTargets([target]);
        WaitForLoad(pane);

        pane.OpenArchive(archivePath);
        WaitForLoad(pane);
        Assert.True(pane.IsArchive);
        Assert.Equal("src", Assert.Single(pane.Entries).Name);
        pane.SetSelection([Assert.Single(pane.Entries)]);
        pane.OpenSelectedDirectory();
        WaitForLoad(pane);
        pane.FilterMode = FileNameFilterMode.Glob;
        pane.FilterText = "*.txt";
        WaitForLoad(pane);
        var filtered = pane.Entries.Where(item => !item.IsParent).ToArray();
        Assert.True(filtered.Length == 1,
            $"Expected one filtered archive entry, found: {string.Join(", ", filtered.Select(item => item.Name))}");
        Assert.Equal("readme.txt", filtered[0].Name);
        var snapshot = pane.CaptureWorkspace();

        var restored = new FilePaneViewModel(new CachedDirectoryBrowserService(), archives);
        restored.RestoreWorkspace(snapshot, [target]);
        WaitForLoad(restored);

        Assert.True(restored.IsArchive);
        Assert.Equal(Path.GetFullPath(archivePath), restored.ArchivePath);
        Assert.Equal("src", restored.ArchiveDirectory);
        Assert.Equal("*.txt", restored.FilterText);
        Assert.Equal("readme.txt", restored.Entries.Single(item => !item.IsParent).Name);
    }

    [Fact]
    public void CommanderWorkspaceAndHotlist_PersistAcrossServiceInstances()
    {
        Directory.CreateDirectory(_root);
        var storage = new ApplicationStorage(Path.Combine(_root, "app"));
        var tabId = Guid.NewGuid();
        var left = new FilePaneWorkspaceSnapshot(
            [new FilePaneTabSnapshot(tabId, _root, _root, "*.cs", FileNameFilterMode.Glob,
                true, [Path.Combine(_root, "before")], [])], tabId);
        var right = new FilePaneWorkspaceSnapshot(
            [new FilePaneTabSnapshot(Guid.NewGuid(), _root, _root, string.Empty,
                FileNameFilterMode.Contains, false, [], [])], null);

        new UserPreferencesService(storage).SaveCommanderWorkspace(left, right, [_root, _root]);
        var restored = new UserPreferencesService(storage);

        Assert.Equal(tabId, restored.LeftPaneWorkspace?.ActiveTabId);
        Assert.Equal("*.cs", Assert.Single(restored.LeftPaneWorkspace!.Tabs).FilterText);
        Assert.True(Assert.Single(restored.LeftPaneWorkspace.Tabs).FilterMatchCase);
        Assert.Single(restored.Hotlist);
        Assert.Equal(Path.GetFullPath(_root), restored.Hotlist[0]);
        Assert.DoesNotContain("Password", File.ReadAllText(Path.Combine(storage.DirectoryPath, "preferences.json")),
            StringComparison.OrdinalIgnoreCase);
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
