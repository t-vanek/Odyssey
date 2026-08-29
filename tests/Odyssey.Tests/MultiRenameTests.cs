using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class MultiRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-multi-rename-{Guid.NewGuid():N}");

    [Fact]
    public void Preview_AppliesTransformsSortingCountersDatesExtensionsAndManualNamesWithoutMutation()
    {
        Directory.CreateDirectory(_root);
        var first = CreateFile("zeta.TXT", "z");
        var second = CreateFile("alpha.txt", "a");
        var timestamp = new DateTime(2025, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(first, timestamp);
        File.SetLastWriteTimeUtc(second, timestamp);
        var service = WritableService();

        var plan = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first, second],
            Prefix = "{modified}_",
            Suffix = "_done",
            SearchText = "a",
            ReplacementText = "X",
            MatchCase = false,
            CaseMode = MultiRenameCaseMode.Upper,
            IncludeCounter = true,
            CounterStart = 5,
            CounterStep = 2,
            CounterPadding = 2,
            DateFormat = "yyyy-MM-dd",
            PreserveExtension = false,
            ExtensionReplacement = "log",
            SortMode = MultiRenameSortMode.Name,
            ManualNames = new Dictionary<string, string> { [first] = "manual.bin" }
        });

        Assert.True(plan.IsValid, string.Join("; ", plan.Errors.Concat(plan.Items.SelectMany(item => item.Errors))));
        Assert.Equal("2025-03-04_XLPHX_done_05.log", plan.Items[0].ProposedName);
        Assert.Equal("manual.bin", plan.Items[1].ProposedName);
        Assert.True(plan.Items[1].IsManual);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.False(File.Exists(Path.Combine(_root, plan.Items[0].ProposedName)));
    }

    [Fact]
    public void Preview_ReportsRegexDuplicateExistingAndCaseInsensitiveReservedNameErrors()
    {
        Directory.CreateDirectory(_root);
        var first = CreateFile("one.txt", "1");
        var second = CreateFile("two.txt", "2");
        CreateFile("occupied.txt", "existing");
        var service = WritableService();

        var invalidRegex = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first], SearchText = "(", UseRegex = true, Prefix = "x"
        });
        var duplicates = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first, second],
            CaseSensitivity = FileSystemCaseMode.CaseInsensitive,
            ManualNames = new Dictionary<string, string>
            {
                [first] = "Same.txt",
                [second] = "same.TXT"
            }
        });
        var occupied = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first],
            CaseSensitivity = FileSystemCaseMode.CaseInsensitive,
            ManualNames = new Dictionary<string, string> { [first] = "occupied.TXT" }
        });
        var reserved = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first],
            CaseSensitivity = FileSystemCaseMode.CaseInsensitive,
            ManualNames = new Dictionary<string, string> { [first] = "CON.txt" }
        });

        Assert.False(invalidRegex.IsValid);
        Assert.Contains(invalidRegex.Errors, error => error.Contains("Regular expression", StringComparison.Ordinal));
        Assert.All(duplicates.Items, item => Assert.Contains(item.Errors,
            error => error.Contains("same result", StringComparison.Ordinal)));
        Assert.Contains(Assert.Single(occupied.Items).Errors,
            error => error.Contains("collides", StringComparison.Ordinal));
        Assert.Contains(Assert.Single(reserved.Items).Errors,
            error => error.Contains("reserved Windows", StringComparison.Ordinal));
    }

    [Fact]
    public void Preview_RejectsOverlappingSourcesAndLinkedAncestorWithoutMutation()
    {
        Directory.CreateDirectory(_root);
        var parent = Path.Combine(_root, "parent");
        Directory.CreateDirectory(parent);
        var child = CreateFile(Path.Combine("parent", "child.txt"), "child");
        var service = WritableService();
        var overlap = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [parent, child], Prefix = "new-"
        });
        Assert.False(overlap.IsValid);
        Assert.Contains(overlap.Errors, error => error.Contains("descendants", StringComparison.Ordinal));
        Assert.True(File.Exists(child));

        var real = Path.Combine(_root, "real");
        var linked = Path.Combine(_root, "linked");
        Directory.CreateDirectory(real);
        var linkedSource = Path.Combine(real, "source.txt");
        File.WriteAllText(linkedSource, "linked");
        Directory.CreateSymbolicLink(linked, real);
        var throughLink = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [Path.Combine(linked, "source.txt")], Prefix = "new-"
        });

        Assert.False(throughLink.IsValid);
        Assert.Contains(throughLink.Errors, error => error.Contains("ancestor", StringComparison.Ordinal));
        Assert.True(File.Exists(linkedSource));
    }

    [Fact]
    public void Preview_InvalidDateAndOverflowingCounterBecomeValidationErrors()
    {
        Directory.CreateDirectory(_root);
        var source = CreateFile("source.txt", "source");
        var second = CreateFile("second.txt", "second");
        var service = WritableService();

        var plan = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [source, second],
            Prefix = "{created}_",
            DateFormat = "%",
            IncludeCounter = true,
            CounterStart = int.MaxValue,
            CounterStep = 1
        });

        Assert.False(plan.IsValid);
        Assert.Contains(plan.Items.SelectMany(item => item.Errors),
            error => error.Contains("date format", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plan.Items.SelectMany(item => item.Errors),
            error => error.Contains("integer range", StringComparison.OrdinalIgnoreCase));
        Assert.True(File.Exists(source));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task Execute_TwoPhaseRenameHandlesCyclesCaseOnlyChangesAndWholeBatchUndo()
    {
        Directory.CreateDirectory(_root);
        var first = CreateFile("a.txt", "first");
        var second = CreateFile("b.txt", "second");
        var caseOnly = CreateFile("lower.txt", "case");
        var service = WritableService();
        var plan = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first, second, caseOnly],
            CaseSensitivity = FileSystemCaseMode.CaseInsensitive,
            ManualNames = new Dictionary<string, string>
            {
                [first] = "b.txt",
                [second] = "a.txt",
                [caseOnly] = "LOWER.txt"
            }
        });

        var result = await service.ExecuteAsync(plan);

        Assert.Equal(3, result.RenamedItems);
        Assert.Equal("second", await File.ReadAllTextAsync(first));
        Assert.Equal("first", await File.ReadAllTextAsync(second));
        Assert.Equal("case", await File.ReadAllTextAsync(Path.Combine(_root, "LOWER.txt")));
        Assert.True(service.CanUndoLastBatch);
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root),
            path => Path.GetFileName(path).StartsWith(".odyssey-rename-", StringComparison.Ordinal));

        var undo = await service.UndoLastBatchAsync();

        Assert.NotNull(undo);
        Assert.Equal("first", await File.ReadAllTextAsync(first));
        Assert.Equal("second", await File.ReadAllTextAsync(second));
        Assert.Equal("case", await File.ReadAllTextAsync(caseOnly));
        Assert.False(service.CanUndoLastBatch);
    }

    [Fact]
    public async Task Execute_InjectedFailureRollsBackEveryNameAndLeavesNoStagingArtifacts()
    {
        Directory.CreateDirectory(_root);
        var first = CreateFile("first.txt", "first");
        var second = CreateFile("second.txt", "second");
        var service = new MultiRenameService
        {
            AccessMode = FileAccessMode.ManageFiles,
            Checkpoint = (checkpoint, index) =>
            {
                if (checkpoint == MultiRenameCheckpoint.DestinationPublished && index == 0)
                    throw new IOException("Injected publication failure.");
            }
        };
        var plan = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first, second], Prefix = "renamed-"
        });

        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(plan));

        Assert.Equal("first", await File.ReadAllTextAsync(first));
        Assert.Equal("second", await File.ReadAllTextAsync(second));
        Assert.False(File.Exists(Path.Combine(_root, "renamed-first.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "renamed-second.txt")));
        Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root),
            path => Path.GetFileName(path).StartsWith(".odyssey-rename-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_CancellationDuringStagingRollsBackAndReadOnlyModeNeverMutates()
    {
        Directory.CreateDirectory(_root);
        var first = CreateFile("first.txt", "first");
        var second = CreateFile("second.txt", "second");
        using var cancellation = new CancellationTokenSource();
        var service = new MultiRenameService
        {
            AccessMode = FileAccessMode.ManageFiles,
            Checkpoint = (checkpoint, index) =>
            {
                if (checkpoint == MultiRenameCheckpoint.SourceStaged && index == 0) cancellation.Cancel();
            }
        };
        var plan = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first, second], Suffix = "-new"
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExecuteAsync(plan, cancellationToken: cancellation.Token));
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));

        var readOnly = new MultiRenameService();
        var readOnlyPlan = readOnly.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [first], Prefix = "blocked-"
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => readOnly.ExecuteAsync(readOnlyPlan));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public async Task ExportImport_BindsPlanToIntegrityAndCurrentFilesystemSnapshot()
    {
        Directory.CreateDirectory(_root);
        var source = CreateFile("source.txt", "source");
        var service = WritableService();
        var plan = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [source], Prefix = "exported-"
        });
        var json = service.ExportPlan(plan);

        var imported = service.ImportPlan(json);
        Assert.True(imported.IsValid);
        Assert.Equal(plan.PlanHashSha256, imported.PlanHashSha256);

        var tampered = json.Replace("exported-source.txt", "forged-source.txt", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => service.ImportPlan(tampered));
        var malformedHash = json.Replace(plan.PlanHashSha256, "not-a-hex-digest", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => service.ImportPlan(malformedHash));

        await File.AppendAllTextAsync(source, " changed");
        Assert.Throws<IOException>(() => service.ImportPlan(json));
        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync(plan));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task Undo_RefusesWhenRenamedContentChanged()
    {
        Directory.CreateDirectory(_root);
        var source = CreateFile("source.txt", "before");
        var destination = Path.Combine(_root, "renamed-source.txt");
        var service = WritableService();
        var plan = service.CreatePlan(new MultiRenameRequest
        {
            SourcePaths = [source], Prefix = "renamed-"
        });
        await service.ExecuteAsync(plan);
        await File.AppendAllTextAsync(destination, "after");

        await Assert.ThrowsAsync<IOException>(() => service.UndoLastBatchAsync());

        Assert.False(File.Exists(source));
        Assert.Equal("beforeafter", await File.ReadAllTextAsync(destination));
        Assert.True(service.CanUndoLastBatch);
    }

    private MultiRenameService WritableService() => new() { AccessMode = FileAccessMode.ManageFiles };

    private string CreateFile(string name, string contents)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
