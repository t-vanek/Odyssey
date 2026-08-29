using Odyssey.Core;
using Odyssey.Desktop;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class FileOperationTests
{
    [Fact]
    public async Task ReadOnlyMode_BlocksEveryMutationBeforeTouchingFilesystem()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "source.txt");
        var destination = Path.Combine(environment.Root, "destination");
        await File.WriteAllTextAsync(source, "Odyssey");
        Directory.CreateDirectory(destination);
        var service = new SafeFileOperationService(environment.Storage);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CopyAsync(source, destination));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CreateDirectoryAsync(destination, "new-folder"));

        Assert.False(File.Exists(Path.Combine(destination, "source.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "new-folder")));
        Assert.Empty(service.History);
    }

    [Fact]
    public async Task ManageMode_CopiesFoldersWithoutOverwritingConflicts()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var source = Path.Combine(environment.Root, "source");
        var destination = Path.Combine(environment.Root, "destination");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "report.txt"), "first");
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };

        var record = await service.CopyAsync(source, destination);

        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(destination, "source", "nested", "report.txt")));
        Assert.Equal(FileOperationKind.Copy, record.Kind);
        await Assert.ThrowsAsync<IOException>(() => service.CopyAsync(source, destination));
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(destination, "source", "nested", "report.txt")));
    }

    [Fact]
    public async Task RenameAndMove_CanBeUndoneSafely()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var firstDirectory = Path.Combine(environment.Root, "first");
        var secondDirectory = Path.Combine(environment.Root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var original = Path.Combine(firstDirectory, "old.txt");
        await File.WriteAllTextAsync(original, "content");
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };

        var rename = await service.RenameAsync(original, "new.txt");
        Assert.True(File.Exists(rename.DestinationPath));
        await service.UndoLastAsync();
        Assert.True(File.Exists(original));

        var move = await service.MoveAsync(original, secondDirectory);
        Assert.True(File.Exists(move.DestinationPath));
        await service.UndoLastAsync();
        Assert.True(File.Exists(original));
        Assert.False(File.Exists(Path.Combine(secondDirectory, "old.txt")));
    }

    [Fact]
    public async Task CreateFolder_CanOnlyBeUndoneWhileEmpty()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var parent = Path.Combine(environment.Root, "parent");
        Directory.CreateDirectory(parent);
        var service = new SafeFileOperationService(environment.Storage) { AccessMode = FileAccessMode.ManageFiles };

        var created = await service.CreateDirectoryAsync(parent, "new-folder");
        await File.WriteAllTextAsync(Path.Combine(created.SourcePath, "keep.txt"), "data");

        await Assert.ThrowsAsync<IOException>(() => service.UndoLastAsync());
        Assert.True(Directory.Exists(created.SourcePath));
    }

    [Fact]
    public async Task AccessModePreference_IsReadOnlyByDefaultAndPersistsChoice()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var preferences = new UserPreferencesService(environment.Storage);
        Assert.True(preferences.ReadOnlyMode);

        preferences.ReadOnlyMode = false;

        Assert.False(new UserPreferencesService(environment.Storage).ReadOnlyMode);
    }
}
