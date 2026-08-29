using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class DirectoryComparisonTests
{
    [Fact]
    public async Task MetadataComparison_FindsDifferencesAndCollapsesOneSidedTrees()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var left = Path.Combine(environment.Root, "left");
        var right = Path.Combine(environment.Root, "right");
        Directory.CreateDirectory(Path.Combine(left, "new-folder", "nested"));
        Directory.CreateDirectory(right);
        await File.WriteAllTextAsync(Path.Combine(left, "new-folder", "nested", "child.txt"), "child");
        await File.WriteAllTextAsync(Path.Combine(left, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(right, "same.txt"), "same");
        await File.WriteAllTextAsync(Path.Combine(left, "changed.txt"), "left");
        await File.WriteAllTextAsync(Path.Combine(right, "changed.txt"), "right");
        await File.WriteAllTextAsync(Path.Combine(right, "right-only.txt"), "right only");
        var timestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(left, "same.txt"), timestamp);
        File.SetLastWriteTimeUtc(Path.Combine(right, "same.txt"), timestamp);
        File.SetLastWriteTimeUtc(Path.Combine(left, "changed.txt"), timestamp);
        File.SetLastWriteTimeUtc(Path.Combine(right, "changed.txt"), timestamp.AddMinutes(1));

        var result = await new DirectoryComparisonService().CompareAsync(new DirectoryComparisonRequest
        {
            LeftPath = left,
            RightPath = right,
            Mode = DirectoryComparisonMode.SizeAndModifiedTime
        });

        Assert.Equal(DirectoryDifferenceKind.Identical, Entry(result, "same.txt").Difference);
        Assert.Equal(DirectoryDifferenceKind.Different, Entry(result, "changed.txt").Difference);
        Assert.Equal(DirectoryDifferenceKind.OnlyLeft, Entry(result, "new-folder").Difference);
        Assert.DoesNotContain(result.Entries, item => item.RelativePath.Contains("child.txt", StringComparison.Ordinal));
        Assert.Equal(DirectoryDifferenceKind.OnlyRight, Entry(result, "right-only.txt").Difference);
    }

    [Fact]
    public async Task ContentComparison_UsesBytesInsteadOfTimestamps()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var left = Path.Combine(environment.Root, "left");
        var right = Path.Combine(environment.Root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        await File.WriteAllTextAsync(Path.Combine(left, "equal.bin"), "12345");
        await File.WriteAllTextAsync(Path.Combine(right, "equal.bin"), "12345");
        await File.WriteAllTextAsync(Path.Combine(left, "different.bin"), "abcde");
        await File.WriteAllTextAsync(Path.Combine(right, "different.bin"), "vwxyz");
        File.SetLastWriteTimeUtc(Path.Combine(left, "equal.bin"), DateTime.UtcNow.AddDays(-5));

        var result = await new DirectoryComparisonService().CompareAsync(new DirectoryComparisonRequest
        {
            LeftPath = left,
            RightPath = right,
            Mode = DirectoryComparisonMode.Content
        });

        Assert.Equal(DirectoryDifferenceKind.Identical, Entry(result, "equal.bin").Difference);
        Assert.Equal(DirectoryDifferenceKind.Different, Entry(result, "different.bin").Difference);
    }

    [Fact]
    public async Task Comparison_RejectsTheSameDirectory()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var service = new DirectoryComparisonService();

        await Assert.ThrowsAsync<IOException>(() => service.CompareAsync(new DirectoryComparisonRequest
        {
            LeftPath = environment.Root,
            RightPath = environment.Root
        }));
    }

    [Fact]
    public async Task SynchronizationPlan_UsesSafeReplaceAndConvergesSelectedDifferences()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var left = Path.Combine(environment.Root, "left");
        var right = Path.Combine(environment.Root, "right");
        Directory.CreateDirectory(Path.Combine(left, "new-folder"));
        Directory.CreateDirectory(right);
        await File.WriteAllTextAsync(Path.Combine(left, "changed.txt"), "new value");
        await File.WriteAllTextAsync(Path.Combine(right, "changed.txt"), "old value");
        await File.WriteAllTextAsync(Path.Combine(left, "new-folder", "inside.txt"), "inside");
        var comparer = new DirectoryComparisonService();
        var before = await comparer.CompareAsync(new DirectoryComparisonRequest
        {
            LeftPath = left,
            RightPath = right,
            Mode = DirectoryComparisonMode.Content
        });
        var plan = new DirectorySynchronizationPlanner().CreatePlan(
            before,
            before.Entries.Where(item => item.Difference != DirectoryDifferenceKind.Identical),
            DirectorySyncDirection.LeftToRight,
            verifyAfterCopy: true);

        Assert.Contains(plan, item => item.SourcePath.EndsWith("changed.txt", StringComparison.Ordinal)
                                      && item.ConflictPolicy == FileConflictPolicy.Replace);
        Assert.Contains(plan, item => item.SourcePath.EndsWith("new-folder", StringComparison.Ordinal)
                                      && item.ConflictPolicy == FileConflictPolicy.Fail);

        var operations = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };
        foreach (var request in plan) await operations.TransferAsync(request);
        var after = await comparer.CompareAsync(new DirectoryComparisonRequest
        {
            LeftPath = left,
            RightPath = right,
            Mode = DirectoryComparisonMode.Content
        });

        Assert.All(after.Entries, item => Assert.Equal(DirectoryDifferenceKind.Identical, item.Difference));
    }

    [Fact]
    public async Task SynchronizationPlan_RejectsPathTraversal()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var left = Path.Combine(environment.Root, "left");
        var right = Path.Combine(environment.Root, "right");
        Directory.CreateDirectory(left);
        Directory.CreateDirectory(right);
        var source = Path.Combine(left, "source.txt");
        await File.WriteAllTextAsync(source, "content");
        var malicious = new DirectoryComparisonEntry
        {
            RelativePath = Path.Combine("..", "outside.txt"),
            Left = new DirectoryComparisonSide(source, FileEntryType.File, 7, DateTimeOffset.UtcNow),
            Difference = DirectoryDifferenceKind.OnlyLeft
        };
        var comparison = new DirectoryComparisonResult(left, right, [malicious], TimeSpan.Zero);

        Assert.Throws<IOException>(() => new DirectorySynchronizationPlanner().CreatePlan(
            comparison, [malicious], DirectorySyncDirection.LeftToRight, verifyAfterCopy: true));
    }

    private static DirectoryComparisonEntry Entry(DirectoryComparisonResult result, string relativePath) =>
        Assert.Single(result.Entries, item => item.RelativePath == relativePath);
}
