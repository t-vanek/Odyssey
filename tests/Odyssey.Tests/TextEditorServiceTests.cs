using System.Text;
using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class TextEditorServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-editor-{Guid.NewGuid():N}");

    [Fact]
    public async Task Open_DetectsEncodingLanguageAndVersionWithoutChangingTheFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "Program.cs");
        var bytes = new UTF8Encoding(true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("public class Program { }\n"))
            .ToArray();
        await File.WriteAllBytesAsync(path, bytes);

        var document = await new SafeTextEditorService().OpenAsync(path);

        Assert.Equal(Path.GetFullPath(path), document.Path);
        Assert.Equal("csharp", document.LanguageId);
        Assert.Equal("utf-8", document.EncodingName);
        Assert.True(document.HasByteOrderMark);
        Assert.Equal("public class Program { }\n", document.Content);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), document.Version.Sha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Save_IsAtomicPreservesBomAndLeavesNoTransactionArtifacts()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "notes.md");
        await File.WriteAllTextAsync(path, "# original", new UTF8Encoding(true));
        var service = new SafeTextEditorService { AccessMode = FileAccessMode.ManageFiles };
        var opened = await service.OpenAsync(path);

        var saved = await service.SaveAsync(Request(opened, "# changed\nOdyssey"));

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..]);
        Assert.Equal("# changed\nOdyssey", (await service.OpenAsync(path)).Content);
        Assert.NotEqual(opened.Version.Sha256, saved.Sha256);
        AssertNoArtifacts();
    }

    [Fact]
    public async Task Save_ReadOnlyModeCannotMutateTheFile()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "read-only.txt");
        await File.WriteAllTextAsync(path, "original");
        var service = new SafeTextEditorService();
        var opened = await service.OpenAsync(path);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SaveAsync(Request(opened, "changed")));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        AssertNoArtifacts();
    }

    [Fact]
    public async Task Save_ChangedSourceIsRejectedWithoutOverwritingExternalContent()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "conflict.txt");
        await File.WriteAllTextAsync(path, "original");
        var service = new SafeTextEditorService { AccessMode = FileAccessMode.ManageFiles };
        var opened = await service.OpenAsync(path);
        await File.WriteAllTextAsync(path, "external change");

        await Assert.ThrowsAsync<TextDocumentChangedException>(() =>
            service.SaveAsync(Request(opened, "editor change")));

        Assert.Equal("external change", await File.ReadAllTextAsync(path));
        AssertNoArtifacts();
    }

    [Fact]
    public async Task Save_FailureAfterPublicationRollsBackTheOriginal()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "rollback.txt");
        await File.WriteAllTextAsync(path, "valuable original");
        var service = new SafeTextEditorService(
            8 * 1024 * 1024,
            (_, _) => throw new IOException("Injected verification failure"))
        {
            AccessMode = FileAccessMode.ManageFiles
        };
        var opened = await service.OpenAsync(path);

        await Assert.ThrowsAsync<IOException>(() =>
            service.SaveAsync(Request(opened, "unsafe replacement")));

        Assert.Equal("valuable original", await File.ReadAllTextAsync(path));
        AssertNoArtifacts();
    }

    [Fact]
    public async Task Open_RejectsOversizedBinaryAndLinkedSources()
    {
        Directory.CreateDirectory(_root);
        var oversized = Path.Combine(_root, "large.txt");
        await File.WriteAllBytesAsync(oversized, new byte[2048]);
        var binary = Path.Combine(_root, "binary.dat");
        await File.WriteAllBytesAsync(binary, [0x41, 0x00, 0x42]);
        var service = new SafeTextEditorService(1024);

        await Assert.ThrowsAsync<TextEditorUnsupportedException>(() => service.OpenAsync(oversized));
        await Assert.ThrowsAsync<TextEditorUnsupportedException>(() => service.OpenAsync(binary));

        var target = Path.Combine(_root, "target.txt");
        var link = Path.Combine(_root, "link.txt");
        await File.WriteAllTextAsync(target, "text");
        try { File.CreateSymbolicLink(link, target); }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                          or PlatformNotSupportedException or NotSupportedException) { return; }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.OpenAsync(link));
    }

    [Fact]
    public async Task Save_PreCancelledRequestLeavesOriginalAndNoArtifact()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "cancel.txt");
        await File.WriteAllTextAsync(path, "original");
        var service = new SafeTextEditorService { AccessMode = FileAccessMode.ManageFiles };
        var opened = await service.OpenAsync(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SaveAsync(Request(opened, new string('x', 500_000)), cancellation.Token));

        Assert.Equal("original", await File.ReadAllTextAsync(path));
        AssertNoArtifacts();
    }

    [Fact]
    public async Task Save_CancellationDuringStreamingLeavesOriginalAndNoArtifact()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "stream-cancel.txt");
        await File.WriteAllTextAsync(path, "original");
        using var cancellation = new CancellationTokenSource();
        var chunks = 0;
        var service = new SafeTextEditorService(
            8 * 1024 * 1024,
            afterPublish: null,
            afterWriteChunk: (_, _) =>
            {
                if (Interlocked.Increment(ref chunks) == 2) cancellation.Cancel();
                return Task.CompletedTask;
            })
        {
            AccessMode = FileAccessMode.ManageFiles
        };
        var opened = await service.OpenAsync(path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SaveAsync(Request(opened, new string('x', 500_000)), cancellation.Token));

        Assert.True(chunks >= 2);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        AssertNoArtifacts();
    }

    private static TextEditorSaveRequest Request(TextEditorDocument document, string content) => new()
    {
        Path = document.Path,
        Content = content,
        EncodingName = document.EncodingName,
        HasByteOrderMark = document.HasByteOrderMark,
        ExpectedVersion = document.Version
    };

    private void AssertNoArtifacts() => Assert.DoesNotContain(
        Directory.EnumerateFiles(_root),
        path => Path.GetFileName(path).Contains(".odyssey-edit-", StringComparison.Ordinal));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
