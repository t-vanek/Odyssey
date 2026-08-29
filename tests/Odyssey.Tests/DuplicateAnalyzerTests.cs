using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class DuplicateAnalyzerTests
{
    [Fact]
    public async Task OnlySameSizeCandidatesAreHashed_EqualContentIsGrouped_AndFilesAreUntouched()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "duplicates");
        var created = await environment.CreateInvestigationAsync(targetPath);
        var firstPath = Path.Combine(targetPath, "copy-a.bin");
        var secondPath = Path.Combine(targetPath, "copy-b.bin");
        var differentPath = Path.Combine(targetPath, "different.bin");
        var content = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(firstPath, content);
        await File.WriteAllBytesAsync(secondPath, content);
        await File.WriteAllBytesAsync(differentPath, [1, 2, 3]);
        var beforeWrite = File.GetLastWriteTimeUtc(firstPath);
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
        [
            TestEntries.File(created.Target.Id, firstPath, content.Length),
            TestEntries.File(created.Target.Id, secondPath, content.Length),
            TestEntries.File(created.Target.Id, differentPath, 3)
        ]);
        var analyzer = new DuplicateAnalyzer(environment.Store, NullLogger<DuplicateAnalyzer>.Instance);

        var groups = await analyzer.FindAsync(created.Session.Id, CancellationToken.None);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(group.Files.First().Fingerprint, group.Files.Last().Fingerprint);
        Assert.Equal(content, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(firstPath));
        Assert.True(File.Exists(firstPath)); Assert.True(File.Exists(secondPath)); Assert.True(File.Exists(differentPath));

        await using var connection = await environment.Connections.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Fingerprint FROM Files WHERE FullPath=$path;";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(differentPath));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
    }

    [Fact]
    public async Task SameSizeDifferentContent_IsNotReportedAsDuplicate()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "different-content");
        var created = await environment.CreateInvestigationAsync(targetPath);
        var firstPath = Path.Combine(targetPath, "a.bin");
        var secondPath = Path.Combine(targetPath, "b.bin");
        await File.WriteAllBytesAsync(firstPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(secondPath, [4, 3, 2, 1]);
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
        [
            TestEntries.File(created.Target.Id, firstPath, 4),
            TestEntries.File(created.Target.Id, secondPath, 4)
        ]);
        var analyzer = new DuplicateAnalyzer(environment.Store, NullLogger<DuplicateAnalyzer>.Instance);
        var groups = await analyzer.FindAsync(created.Session.Id, CancellationToken.None);
        Assert.Empty(groups);
    }

    [Fact]
    public async Task AnalysisRechecksCurrentBytes_InsteadOfTrustingAnOldFingerprint()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var targetPath = Path.Combine(environment.Root, "changed-content");
        var created = await environment.CreateInvestigationAsync(targetPath);
        var firstPath = Path.Combine(targetPath, "a.bin");
        var secondPath = Path.Combine(targetPath, "b.bin");
        await File.WriteAllBytesAsync(firstPath, [1, 2, 3, 4]);
        await File.WriteAllBytesAsync(secondPath, [4, 3, 2, 1]);
        await environment.Store.UpsertEntriesAsync(created.Scan.Id,
        [
            TestEntries.File(created.Target.Id, firstPath, 4),
            TestEntries.File(created.Target.Id, secondPath, 4)
        ]);
        var analyzer = new DuplicateAnalyzer(environment.Store, NullLogger<DuplicateAnalyzer>.Instance);
        Assert.Empty(await analyzer.FindAsync(created.Session.Id, CancellationToken.None));

        await File.WriteAllBytesAsync(firstPath, [9, 8, 7, 6]);
        await File.WriteAllBytesAsync(secondPath, [9, 8, 7, 6]);
        var groups = await analyzer.FindAsync(created.Session.Id, CancellationToken.None);

        Assert.Equal(2, Assert.Single(groups).Files.Count);
    }
}
