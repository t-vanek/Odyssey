using Odyssey.Core;

namespace Odyssey.Tests;

public sealed class IncrementalIndexTests
{
    [Fact]
    public async Task FileOperations_UpdateSearchIndexWithoutFullRescan()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var root = Path.Combine(environment.Root, "target");
        var source = Path.Combine(root, "source.txt");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(source, "content");
        var investigation = await environment.CreateInvestigationAsync(root);
        await environment.Store.UpsertEntriesAsync(investigation.Scan.Id,
            [TestEntries.File(investigation.Target.Id, source, 7)]);
        await environment.Store.CompleteScanAsync(investigation.Scan.Id, ScanStatus.Completed, DateTimeOffset.UtcNow);

        var copied = Path.Combine(root, "copy.txt");
        await environment.Store.ApplyFileOperationsAsync([Operation(FileOperationKind.Copy, source, copied)], [investigation.Target]);
        var copySearch = await environment.Search.SearchAsync(new SearchRequest
        {
            Query = "copy", SessionId = investigation.Session.Id
        }, CancellationToken.None);
        Assert.Contains(copySearch.Results, item => item.FullPath == copied);

        var moved = Path.Combine(root, "moved.txt");
        await environment.Store.ApplyFileOperationsAsync([Operation(FileOperationKind.Move, copied, moved)], [investigation.Target]);
        var moveSearch = await environment.Search.SearchAsync(new SearchRequest
        {
            Query = "moved", SessionId = investigation.Session.Id
        }, CancellationToken.None);
        Assert.Contains(moveSearch.Results, item => item.FullPath == moved);

        await environment.Store.ApplyFileOperationsAsync([Operation(FileOperationKind.Trash, moved, null)], [investigation.Target]);
        var trashedSearch = await environment.Search.SearchAsync(new SearchRequest
        {
            Query = "moved", SessionId = investigation.Session.Id
        }, CancellationToken.None);
        Assert.All(trashedSearch.Results, item => Assert.True(item.IsMissing));
    }

    private static FileOperationRecord Operation(FileOperationKind kind, string source, string? destination) => new()
    {
        Id = Guid.NewGuid(), Kind = kind, SourcePath = source, DestinationPath = destination,
        CompletedAt = DateTimeOffset.UtcNow
    };
}
