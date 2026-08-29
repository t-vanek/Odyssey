using System.Security.Cryptography;
using System.Text;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class SafeTextEditorService : ITextEditorService
{
    private const int StreamBufferSize = 64 * 1024;
    private const int CharacterChunkSize = 32 * 1024;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Func<string, CancellationToken, Task>? _afterPublish;
    private readonly Func<int, CancellationToken, Task>? _afterWriteChunk;

    public SafeTextEditorService(int maximumFileBytes = 8 * 1024 * 1024)
        : this(maximumFileBytes, null, null) { }

    internal SafeTextEditorService(
        int maximumFileBytes,
        Func<string, CancellationToken, Task>? afterPublish,
        Func<int, CancellationToken, Task>? afterWriteChunk = null)
    {
        MaximumFileBytes = maximumFileBytes is >= 1024 and <= 64 * 1024 * 1024
            ? maximumFileBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        _afterPublish = afterPublish;
        _afterWriteChunk = afterWriteChunk;
    }

    public int MaximumFileBytes { get; }

    public FileAccessMode AccessMode { get; set; } = FileAccessMode.ReadOnly;

    public async Task<TextEditorDocument> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var canonicalPath = ValidateRegularPath(path);
        var before = SnapshotMetadata(canonicalPath);
        EnsureBounded(before.Length);
        var bytes = await File.ReadAllBytesAsync(canonicalPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var after = SnapshotMetadata(canonicalPath);
        if (before != after)
            throw new TextDocumentChangedException("The file changed while it was being opened.");

        var decoded = Decode(bytes);
        EnsureText(decoded.Content);
        return new TextEditorDocument
        {
            Path = canonicalPath,
            Name = Path.GetFileName(canonicalPath),
            Content = decoded.Content,
            LanguageId = ResolveLanguage(canonicalPath),
            EncodingName = decoded.EncodingName,
            HasByteOrderMark = decoded.HasByteOrderMark,
            Version = new TextDocumentVersion(
                after.Length,
                after.ModifiedAt,
                Convert.ToHexString(SHA256.HashData(bytes)))
        };
    }

    public async Task<TextDocumentVersion> SaveAsync(
        TextEditorSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureWritable();
        var canonicalPath = ValidateRegularPath(request.Path);
        if (!PathEquals(canonicalPath, request.Path))
            throw new UnauthorizedAccessException("The editor path is not canonical.");
        var encoding = ResolveEncoding(request.EncodingName);
        var preamble = request.HasByteOrderMark ? Preamble(request.EncodingName) : [];
        var encodedLength = checked((long)preamble.Length + encoding.GetByteCount(request.Content));
        EnsureBounded(encodedLength);
        EnsureText(request.Content);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureExpectedVersionAsync(canonicalPath, request.ExpectedVersion, cancellationToken)
                .ConfigureAwait(false);
            var attributes = File.GetAttributes(canonicalPath);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                throw new UnauthorizedAccessException("The file is marked read-only.");

            var transactionId = Guid.NewGuid();
            var temporaryPath = canonicalPath + $".odyssey-edit-{transactionId:N}.tmp";
            var backupPath = canonicalPath + $".odyssey-edit-{transactionId:N}.bak";
            var published = false;
            try
            {
                await WriteTemporaryAsync(
                    temporaryPath, request.Content, encoding, preamble, cancellationToken).ConfigureAwait(false);
                PreservePermissions(canonicalPath, temporaryPath);
                var expectedPublishedHash = await HashAsync(temporaryPath, cancellationToken).ConfigureAwait(false);
                await EnsureExpectedVersionAsync(canonicalPath, request.ExpectedVersion, cancellationToken)
                    .ConfigureAwait(false);
                ValidateRegularPath(canonicalPath);
                cancellationToken.ThrowIfCancellationRequested();

                File.Replace(temporaryPath, canonicalPath, backupPath, ignoreMetadataErrors: false);
                published = true;
                if (_afterPublish is not null)
                    await _afterPublish(canonicalPath, CancellationToken.None).ConfigureAwait(false);
                var result = await SnapshotVersionAsync(canonicalPath, CancellationToken.None).ConfigureAwait(false);
                if (!string.Equals(result.Sha256, expectedPublishedHash, StringComparison.Ordinal))
                    throw new IOException("The published editor file failed verification.");
                DeleteRegularFile(backupPath);
                return result;
            }
            catch (Exception original)
            {
                if (published && File.Exists(backupPath))
                {
                    try
                    {
                        File.Replace(backupPath, canonicalPath, null, ignoreMetadataErrors: false);
                        published = false;
                    }
                    catch (Exception rollback)
                    {
                        throw new IOException(
                            $"The editor save failed and automatic rollback could not complete. The recovery backup is '{backupPath}'.",
                            new AggregateException(original, rollback));
                    }
                }
                else if (!File.Exists(canonicalPath) && File.Exists(backupPath))
                {
                    try { File.Move(backupPath, canonicalPath); }
                    catch (Exception rollback)
                    {
                        throw new IOException(
                            $"The editor save failed and automatic rollback could not complete. The recovery backup is '{backupPath}'.",
                            new AggregateException(original, rollback));
                    }
                }
                throw;
            }
            finally
            {
                TryDeleteRegularFile(temporaryPath);
                if (!published && File.Exists(canonicalPath)) TryDeleteRegularFile(backupPath);
            }
        }
        finally { _saveGate.Release(); }
    }

    private async Task WriteTemporaryAsync(
        string path,
        string content,
        Encoding encoding,
        byte[] preamble,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (preamble.Length > 0)
            await stream.WriteAsync(preamble, cancellationToken).ConfigureAwait(false);
        await using (var writer = new StreamWriter(stream, encoding, StreamBufferSize, leaveOpen: true))
        {
            for (var offset = 0; offset < content.Length; offset += CharacterChunkSize)
            {
                var count = Math.Min(CharacterChunkSize, content.Length - offset);
                await writer.WriteAsync(content.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
                if (_afterWriteChunk is not null)
                    await _afterWriteChunk(offset + count, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task EnsureExpectedVersionAsync(
        string path,
        TextDocumentVersion expected,
        CancellationToken cancellationToken)
    {
        var current = await SnapshotVersionAsync(path, cancellationToken).ConfigureAwait(false);
        if (current != expected)
            throw new TextDocumentChangedException(
                "The file changed after it was opened. Reload it before saving.");
    }

    private static async Task<TextDocumentVersion> SnapshotVersionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var before = SnapshotMetadata(path);
        var hash = await HashAsync(path, cancellationToken).ConfigureAwait(false);
        var after = SnapshotMetadata(path);
        if (before != after)
            throw new TextDocumentChangedException("The file changed while its version was being verified.");
        return new TextDocumentVersion(after.Length, after.ModifiedAt, hash);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            StreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private string ValidateRegularPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
        var canonical = Path.GetFullPath(path);
        if (!File.Exists(canonical)) throw new FileNotFoundException("The editor file is unavailable.", canonical);
        for (var current = canonical; current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new UnauthorizedAccessException("The editor does not follow symbolic links or reparse points.");
            var parent = Path.GetDirectoryName(current);
            if (parent is null || PathEquals(parent, current)) break;
        }
        var file = new FileInfo(canonical);
        file.Refresh();
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.Directory) || file.LinkTarget is not null)
            throw new TextEditorUnsupportedException("Only regular local text files can be edited.");
        EnsureBounded(file.Length);
        return canonical;
    }

    private void EnsureBounded(long length)
    {
        if (length > MaximumFileBytes)
            throw new TextEditorUnsupportedException(
                $"The built-in editor accepts files up to {MaximumFileBytes / (1024 * 1024)} MiB.");
    }

    private static void EnsureText(string content)
    {
        if (content.IndexOf('\0') >= 0)
            throw new TextEditorUnsupportedException("Binary files cannot be opened in the text editor.");
        var controls = 0;
        foreach (var value in content)
            if (char.IsControl(value) && value is not ('\r' or '\n' or '\t' or '\f')) controls++;
        if (controls > Math.Max(4, content.Length / 100))
            throw new TextEditorUnsupportedException("The file appears to contain binary data.");
    }

    private static DecodedText Decode(byte[] bytes)
    {
        try
        {
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return new DecodedText(
                    new UTF8Encoding(false, true).GetString(bytes.AsSpan(3)), "utf-8", true);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new DecodedText(
                    new UnicodeEncoding(false, false, true).GetString(bytes.AsSpan(2)), "utf-16", true);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new DecodedText(
                    new UnicodeEncoding(true, false, true).GetString(bytes.AsSpan(2)), "utf-16BE", true);
            return new DecodedText(new UTF8Encoding(false, true).GetString(bytes), "utf-8", false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new TextEditorUnsupportedException(
                $"The file encoding is not supported by the built-in editor: {exception.Message}");
        }
    }

    private static Encoding ResolveEncoding(string name) => name switch
    {
        "utf-8" => new UTF8Encoding(false, true),
        "utf-16" => new UnicodeEncoding(false, false, true),
        "utf-16BE" => new UnicodeEncoding(true, false, true),
        _ => throw new TextEditorUnsupportedException("The document encoding is not supported for safe saving.")
    };

    private static byte[] Preamble(string name) => name switch
    {
        "utf-8" => [0xEF, 0xBB, 0xBF],
        "utf-16" => [0xFF, 0xFE],
        "utf-16BE" => [0xFE, 0xFF],
        _ => []
    };

    private static string ResolveLanguage(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
        ".ts" or ".tsx" => "typescript",
        ".json" or ".jsonc" => "json",
        ".html" or ".htm" or ".xhtml" => "html",
        ".xml" or ".xaml" or ".axaml" or ".svg" => "xml",
        ".css" or ".scss" or ".sass" or ".less" => "css",
        ".md" or ".markdown" => "markdown",
        ".py" or ".pyw" => "python",
        ".sh" or ".bash" or ".zsh" or ".fish" => "shell",
        ".ps1" or ".psm1" or ".psd1" => "powershell",
        ".sql" => "sql",
        ".yaml" or ".yml" => "yaml",
        ".toml" => "ini",
        ".java" => "java",
        ".kt" or ".kts" => "kotlin",
        ".swift" => "swift",
        ".c" or ".h" => "c",
        ".cc" or ".cpp" or ".cxx" or ".hpp" => "cpp",
        ".rs" => "rust",
        ".go" => "go",
        _ => "plaintext"
    };

    private static MetadataSnapshot SnapshotMetadata(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists || file.LinkTarget is not null)
            throw new TextDocumentChangedException("The editor file is no longer a regular file.");
        return new MetadataSnapshot(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static void PreservePermissions(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
    }

    private static void TryDeleteRegularFile(string path)
    {
        try
        {
            if (!File.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return;
            File.Delete(path);
        }
        catch { }
    }

    private static void DeleteRegularFile(string path)
    {
        if (!File.Exists(path)) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("An editor recovery path became a link.");
        File.Delete(path);
    }

    private void EnsureWritable()
    {
        if (AccessMode != FileAccessMode.ManageFiles)
            throw new UnauthorizedAccessException("The built-in editor is read-only until file management is enabled.");
    }

    private static bool PathEquals(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record DecodedText(string Content, string EncodingName, bool HasByteOrderMark);
    private sealed record MetadataSnapshot(long Length, DateTimeOffset ModifiedAt);
}
