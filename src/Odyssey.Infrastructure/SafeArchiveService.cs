using System.IO.Compression;
using System.Formats.Tar;
using Odyssey.Core;
using SharpSevenZip;

namespace Odyssey.Infrastructure;

public sealed partial class SafeArchiveService : IArchiveService, IArchiveMutationService, IArchiveRecoveryService
{
    private const int BufferSize = 128 * 1024;
    private readonly ArchiveSafetyLimits _limits;
    private readonly bool _nativeAvailable;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string? _recoveryRoot;

    public SafeArchiveService(ArchiveSafetyLimits? limits = null, ApplicationStorage? storage = null)
    {
        _limits = limits ?? new ArchiveSafetyLimits();
        ValidateLimits(_limits);
        _nativeAvailable = SevenZipArchiveReader.Default is not null;
        _recoveryRoot = storage is null ? null : Path.Combine(storage.DirectoryPath, "archive-recovery");
        Formats =
        [
            new ArchiveFormatSupport("ZIP", [".zip"], ArchiveCapabilities.Browse | ArchiveCapabilities.Extract
                | ArchiveCapabilities.Create | ArchiveCapabilities.Update | ArchiveCapabilities.DeleteEntries, true),
            NativeFormat("7z", [".7z"]),
            new ArchiveFormatSupport("TAR", [".tar"], ArchiveCapabilities.Browse | ArchiveCapabilities.Extract
                | ArchiveCapabilities.Create, true),
            NativeFormat("TAR.GZ", [".tar.gz", ".tgz"]),
            NativeFormat("GZip", [".gz"]),
            NativeFormat("BZip2", [".bz2"]),
            NativeFormat("XZ", [".xz"]),
            NativeFormat("RAR", [".rar"])
        ];
    }

    public FileAccessMode AccessMode { get; set; } = FileAccessMode.ReadOnly;
    public IReadOnlyList<ArchiveFormatSupport> Formats { get; }

    public bool CanOpen(string archivePath) => ResolveFormat(archivePath) is { IsAvailable: true };

    public ArchiveCapabilities GetCapabilities(string archivePath) =>
        ResolveFormat(archivePath)?.Capabilities ?? ArchiveCapabilities.None;

    public async Task<IReadOnlyList<ArchiveEntry>> ListAsync(
        string archivePath,
        string directoryPath = "",
        CancellationToken cancellationToken = default)
    {
        var source = ValidateArchivePath(archivePath);
        var directory = NormalizeDirectoryPath(directoryPath);
        var descriptors = await ScanAsync(source, cancellationToken).ConfigureAwait(false);
        var prefix = directory.Length == 0 ? string.Empty : directory + "/";
        var children = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!descriptor.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var remainder = descriptor.Path[prefix.Length..];
            if (remainder.Length == 0) continue;
            var separator = remainder.IndexOf('/');
            if (separator >= 0)
            {
                var name = remainder[..separator];
                var path = prefix + name;
                children.TryAdd(name, new ArchiveEntry(path, name, FileEntryType.Directory,
                    null, null, null));
                continue;
            }
            children.TryAdd(descriptor.Name, new ArchiveEntry(
                descriptor.Path,
                descriptor.Name,
                descriptor.IsDirectory ? FileEntryType.Directory : FileEntryType.File,
                descriptor.IsDirectory ? null : descriptor.Size,
                descriptor.CompressedSize,
                descriptor.ModifiedAt,
                descriptor.IsEncrypted,
                descriptor.IsSymbolicLink));
        }
        return children.Values.OrderByDescending(item => item.Type)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<FileTransferOutcome> ExtractAsync(
        string archivePath,
        string entryPath,
        string destinationDirectory,
        FileConflictPolicy conflictPolicy = FileConflictPolicy.Fail,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (AccessMode != FileAccessMode.ManageFiles)
            throw new InvalidOperationException("Odyssey is in read-only mode.");
        var source = ValidateArchivePath(archivePath);
        var destinationRoot = ValidateDestinationDirectory(destinationDirectory);
        var selectedPath = NormalizeEntryPath(entryPath, isDirectory: false);
        var descriptors = await ScanAsync(source, cancellationToken).ConfigureAwait(false);
        var exact = descriptors.FirstOrDefault(item => PathComparer.Equals(item.Path, selectedPath));
        var directoryPrefix = selectedPath + "/";
        var isDirectory = exact?.IsDirectory == true
                          || descriptors.Any(item => item.Path.StartsWith(directoryPrefix, StringComparison.Ordinal));
        var selected = isDirectory
            ? descriptors.Where(item => item.Path.StartsWith(directoryPrefix, StringComparison.Ordinal)
                                        || PathComparer.Equals(item.Path, selectedPath)).ToArray()
            : exact is null ? [] : [exact];
        if (selected.Length == 0) throw new FileNotFoundException("Archive entry not found.", selectedPath);
        if (selected.Any(item => item.IsSymbolicLink))
            throw new InvalidDataException("Symbolic links in archives are not extracted.");
        if (selected.Any(item => item.IsEncrypted))
            throw new InvalidDataException("Encrypted archive entries require an explicit password workflow.");

        var expandedBytes = selected.Where(item => !item.IsDirectory).Sum(item => item.Size);
        EnsureFreeSpace(destinationRoot, expandedBytes);
        var topName = selectedPath.Split('/')[^1];
        var stage = Path.Combine(destinationRoot, $".odyssey-archive-stage-{Guid.NewGuid():N}");
        var stagedItem = Path.Combine(stage, topName);
        Directory.CreateDirectory(stage);
        var completed = 0L;
        try
        {
            if (isDirectory) Directory.CreateDirectory(stagedItem);
            await ExtractEntriesAsync(source, selectedPath, isDirectory, selected, stagedItem,
                value =>
                {
                    completed += value;
                    progress?.Report(new FileOperationProgress(completed, expandedBytes, selectedPath));
                }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var requestedDestination = Path.Combine(destinationRoot, topName);
            var resolution = ResolveConflict(requestedDestination, conflictPolicy);
            if (resolution.Skip)
            {
                DeleteEntry(stage);
                return new FileTransferOutcome(null, resolution.Destination, true, false);
            }
            string? backup = null;
            try
            {
                if (resolution.ReplaceExisting)
                {
                    backup = resolution.Destination + $".odyssey-archive-backup-{Guid.NewGuid():N}";
                    MoveEntry(resolution.Destination, backup);
                }
                MoveEntry(stagedItem, resolution.Destination);
                DeleteEntry(stage);
                if (backup is not null) DeleteEntry(backup);
                return new FileTransferOutcome(null, resolution.Destination, false, false);
            }
            catch
            {
                if (backup is not null && Exists(backup) && !Exists(resolution.Destination))
                    MoveEntry(backup, resolution.Destination);
                throw;
            }
        }
        catch
        {
            DeleteEntry(stage);
            throw;
        }
    }

    private async Task ExtractEntriesAsync(
        string archivePath,
        string selectedPath,
        bool selectedIsDirectory,
        IReadOnlyList<EntryDescriptor> selected,
        string stagedItem,
        Action<int> report,
        CancellationToken cancellationToken)
    {
        if (IsZip(archivePath))
        {
            await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
            foreach (var descriptor in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveStagedPath(selectedPath, selectedIsDirectory, descriptor, stagedItem);
                if (descriptor.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = CreateOutput(destination);
                await using var guarded = new GuardedWriteStream(output, descriptor.Size, report, cancellationToken);
                await using var entryStream = archive.Entries[descriptor.Index].Open();
                await entryStream.CopyToAsync(guarded, BufferSize, cancellationToken).ConfigureAwait(false);
                await guarded.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (guarded.BytesWritten != descriptor.Size)
                    throw new InvalidDataException("The extracted size does not match archive metadata.");
                ApplyModifiedTime(destination, descriptor.ModifiedAt);
            }
            return;
        }

        if (IsTar(archivePath))
        {
            await ExtractTarEntriesAsync(archivePath, selectedPath, selectedIsDirectory, selected, stagedItem,
                report, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Task.Run(async () =>
        {
            using var archive = new SharpSevenZipExtractor(archivePath);
            foreach (var descriptor in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveStagedPath(selectedPath, selectedIsDirectory, descriptor, stagedItem);
                if (descriptor.IsDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var output = CreateOutput(destination);
                await using var guarded = new GuardedWriteStream(output, descriptor.Size, report, cancellationToken);
                await archive.ExtractFileAsync(descriptor.Index, guarded).ConfigureAwait(false);
                await guarded.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (guarded.BytesWritten != descriptor.Size)
                    throw new InvalidDataException("The extracted size does not match archive metadata.");
                ApplyModifiedTime(destination, descriptor.ModifiedAt);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task ExtractTarEntriesAsync(
        string archivePath,
        string selectedPath,
        bool selectedIsDirectory,
        IReadOnlyList<EntryDescriptor> selected,
        string stagedItem,
        Action<int> report,
        CancellationToken cancellationToken)
    {
        var selectedByIndex = selected.ToDictionary(item => item.Index);
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var archive = new TarReader(input, leaveOpen: false);
        var index = 0;
        while (await archive.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!selectedByIndex.TryGetValue(index++, out var descriptor)) continue;
            var destination = ResolveStagedPath(selectedPath, selectedIsDirectory, descriptor, stagedItem);
            if (descriptor.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            if (!IsTarRegularFile(entry.EntryType) || entry.DataStream is null)
                throw new InvalidDataException("Special TAR entries are not extracted.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = CreateOutput(destination);
            await CopyExactlyAsync(entry.DataStream, output, descriptor.Size, descriptor.Path, report,
                cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            ApplyModifiedTime(destination, descriptor.ModifiedAt);
        }
    }

    private Task<IReadOnlyList<EntryDescriptor>> ScanAsync(string archivePath, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<EntryDescriptor>>(() => IsZip(archivePath)
            ? ScanZip(archivePath, cancellationToken)
            : IsTar(archivePath)
                ? ScanTar(archivePath, cancellationToken)
                : ScanNative(archivePath, cancellationToken), cancellationToken);

    private IReadOnlyList<EntryDescriptor> ScanZip(string archivePath, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.SequentialScan);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var entries = new List<EntryDescriptor>(Math.Min(archive.Entries.Count, _limits.MaximumEntries));
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var total = 0L;
            for (var index = 0; index < archive.Entries.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (index >= _limits.MaximumEntries) throw new InvalidDataException("Archive entry-count limit exceeded.");
                var entry = archive.Entries[index];
                var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                var path = NormalizeEntryPath(entry.FullName, isDirectory);
                if (!paths.Add(path)) throw new InvalidDataException("Archive contains duplicate or case-colliding paths.");
                var size = isDirectory ? 0 : entry.Length;
                var compressed = isDirectory ? 0 : entry.CompressedLength;
                ValidateEntryQuota(size, compressed, ref total);
                entries.Add(new EntryDescriptor(index, path, path.Split('/')[^1], isDirectory,
                    size, compressed, ToDateTimeOffset(entry.LastWriteTime), false,
                    IsZipLink(entry.ExternalAttributes)));
            }
            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not InvalidDataException)
        {
            throw new InvalidDataException("The archive is corrupt or unsupported.", ex);
        }
    }

    private IReadOnlyList<EntryDescriptor> ScanNative(string archivePath, CancellationToken token)
    {
        if (!_nativeAvailable) throw new NotSupportedException("The native 7-Zip engine is unavailable.");
        try
        {
            using var archive = new SharpSevenZipExtractor(archivePath);
            var entries = new List<EntryDescriptor>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var total = 0L;
            foreach (var entry in archive.ArchiveFileData)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= _limits.MaximumEntries) throw new InvalidDataException("Archive entry-count limit exceeded.");
                var path = NormalizeEntryPath(entry.FileName, entry.IsDirectory);
                if (!paths.Add(path)) throw new InvalidDataException("Archive contains duplicate or case-colliding paths.");
                var size = entry.IsDirectory ? 0 : checked((long)entry.Size);
                ValidateEntryQuota(size, null, ref total);
                entries.Add(new EntryDescriptor((int)entry.Index, path, path.Split('/')[^1], entry.IsDirectory,
                    size, null, ToDateTimeOffset(entry.LastWriteTime), entry.Encrypted,
                    IsNativeLink(entry.Attributes)));
            }
            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not InvalidDataException)
        {
            throw new InvalidDataException("The archive is corrupt or unsupported.", ex);
        }
    }

    private IReadOnlyList<EntryDescriptor> ScanTar(string archivePath, CancellationToken token)
    {
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferSize, FileOptions.SequentialScan);
            using var archive = new TarReader(stream, leaveOpen: false);
            var entries = new List<EntryDescriptor>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var total = 0L;
            TarEntry? entry;
            while ((entry = archive.GetNextEntry(copyData: false)) is not null)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= _limits.MaximumEntries)
                    throw new InvalidDataException("Archive entry-count limit exceeded.");
                var isDirectory = entry.EntryType == TarEntryType.Directory;
                var isRegular = IsTarRegularFile(entry.EntryType);
                var path = NormalizeEntryPath(entry.Name, isDirectory);
                if (!paths.Add(path))
                    throw new InvalidDataException("Archive contains duplicate or case-colliding paths.");
                var size = isRegular ? entry.Length : 0;
                ValidateEntryQuota(size, null, ref total);
                entries.Add(new EntryDescriptor(entries.Count, path, path.Split('/')[^1], isDirectory,
                    size, null, entry.ModificationTime, false, !isDirectory && !isRegular));
            }
            return entries;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not InvalidDataException)
        {
            throw new InvalidDataException("The archive is corrupt or unsupported.", ex);
        }
    }

    private string ValidateArchivePath(string archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) throw new ArgumentException("Archive path is required.", nameof(archivePath));
        var path = Path.GetFullPath(archivePath);
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Archive not found.", path);
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Archive source cannot be a symbolic link or reparse point.");
        var format = ResolveFormat(path) ?? throw new NotSupportedException("Archive format is not supported.");
        if (!format.IsAvailable) throw new NotSupportedException(format.UnavailableReason);
        return path;
    }

    private static string ValidateDestinationDirectory(string destinationDirectory)
    {
        var path = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Extraction destination cannot cross a symbolic link or reparse point.");
            current = current.Parent;
        }
        return Path.TrimEndingDirectorySeparator(path);
    }

    private string NormalizeEntryPath(string path, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("Archive contains an empty path.");
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains('\0') || normalized.Contains(':'))
            throw new InvalidDataException("Archive contains an absolute or invalid path.");
        var segments = normalized.Split('/');
        if (segments.Length > _limits.MaximumDepth || segments.Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException("Archive path traversal or depth limit violation detected.");
        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("Archive contains a filename unsupported by this platform.");
        }
        return string.Join('/', segments);
    }

    private string NormalizeDirectoryPath(string path) => string.IsNullOrWhiteSpace(path) || path == "/"
        ? string.Empty
        : NormalizeEntryPath(path.Trim('/'), true);

    private void ValidateEntryQuota(long size, long? compressedSize, ref long total)
    {
        if (size < 0 || size > _limits.MaximumEntryBytes)
            throw new InvalidDataException("Archive entry-size limit exceeded.");
        total = checked(total + size);
        if (total > _limits.MaximumExpandedBytes)
            throw new InvalidDataException("Archive expanded-size limit exceeded.");
        if (size > 0 && compressedSize is 0)
            throw new InvalidDataException("Archive has an unsafe compression ratio.");
        if (size > 0 && compressedSize is > 0 && (double)size / compressedSize.Value > _limits.MaximumCompressionRatio)
            throw new InvalidDataException("Archive compression-ratio limit exceeded.");
    }

    private static string ResolveStagedPath(
        string selectedPath, bool selectedIsDirectory, EntryDescriptor descriptor, string stagedItem)
    {
        if (!selectedIsDirectory) return stagedItem;
        var relative = descriptor.Path.Length == selectedPath.Length
            ? string.Empty
            : descriptor.Path[(selectedPath.Length + 1)..];
        var path = relative.Length == 0 ? stagedItem : Path.Combine(stagedItem, relative.Replace('/', Path.DirectorySeparatorChar));
        var root = Path.GetFullPath(stagedItem) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!PathComparer.Equals(full, Path.TrimEndingDirectorySeparator(stagedItem))
            && !full.StartsWith(root, PathComparison))
            throw new InvalidDataException("Archive extraction escaped its staging boundary.");
        return full;
    }

    private static ConflictResolution ResolveConflict(string destination, FileConflictPolicy policy)
    {
        if (!Exists(destination)) return new ConflictResolution(destination, false, false);
        if (new FileSystemInfoProxy(destination).IsLink)
            throw new IOException("Archive extraction never replaces a symbolic link or reparse point.");
        return policy switch
        {
            FileConflictPolicy.Fail => throw new IOException("Destination already exists."),
            FileConflictPolicy.Skip => new ConflictResolution(destination, true, false),
            FileConflictPolicy.Replace => new ConflictResolution(destination, false, true),
            FileConflictPolicy.KeepBoth => new ConflictResolution(FindKeepBothName(destination), false, false),
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
    }

    private static string FindKeepBothName(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(parent, $"{name} ({index}){extension}");
            if (!Exists(candidate)) return candidate;
        }
        throw new IOException("A unique destination name could not be allocated.");
    }

    private static void EnsureFreeSpace(string destination, long required)
    {
        if (required <= 0) return;
        try
        {
            var root = Path.GetPathRoot(destination);
            if (root is null) return;
            var drive = new DriveInfo(root);
            if (drive.IsReady && drive.AvailableFreeSpace < required)
                throw new IOException("The destination does not have enough free space.");
        }
        catch (ArgumentException) { }
    }

    private ArchiveFormatSupport? ResolveFormat(string path)
    {
        var name = Path.GetFileName(path);
        return Formats.SelectMany(format => format.Extensions.Select(extension => (format, extension)))
            .Where(item => name.EndsWith(item.extension, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.extension.Length)
            .Select(item => item.format).FirstOrDefault();
    }

    private ArchiveFormatSupport NativeFormat(string name, IReadOnlyList<string> extensions) => new(
        name, extensions, ArchiveCapabilities.Browse | ArchiveCapabilities.Extract, _nativeAvailable,
        _nativeAvailable ? null : "The native 7-Zip engine is unavailable.");

    private static bool IsZip(string path) => path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
    private static bool IsTar(string path) => path.EndsWith(".tar", StringComparison.OrdinalIgnoreCase);
    private static bool IsTarRegularFile(TarEntryType type) =>
        type is TarEntryType.RegularFile or TarEntryType.V7RegularFile;
    private static bool IsZipLink(int attributes) => IsUnixLink(unchecked((uint)attributes))
                                                      || ((FileAttributes)attributes).HasFlag(FileAttributes.ReparsePoint);
    private static bool IsNativeLink(uint attributes) => IsUnixLink(attributes)
                                                         || ((FileAttributes)attributes).HasFlag(FileAttributes.ReparsePoint);
    private static bool IsUnixLink(uint attributes) => ((attributes >> 16) & 0xF000) == 0xA000;
    private static DateTimeOffset? ToDateTimeOffset(DateTime value) => value == default
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc));
    private static DateTimeOffset? ToDateTimeOffset(DateTimeOffset value) => value == default ? null : value;
    private static FileStream CreateOutput(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
        BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static void ApplyModifiedTime(string path, DateTimeOffset? modified)
    {
        if (modified is not null) File.SetLastWriteTimeUtc(path, modified.Value.UtcDateTime);
    }
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }
    private static void DeleteEntry(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
    private static void ValidateLimits(ArchiveSafetyLimits limits)
    {
        if (limits.MaximumEntries <= 0 || limits.MaximumDepth <= 0 || limits.MaximumEntryBytes <= 0
            || limits.MaximumExpandedBytes <= 0 || limits.MaximumCompressionRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    private sealed record EntryDescriptor(
        int Index, string Path, string Name, bool IsDirectory, long Size, long? CompressedSize,
        DateTimeOffset? ModifiedAt, bool IsEncrypted, bool IsSymbolicLink);
    private sealed record ConflictResolution(string Destination, bool Skip, bool ReplaceExisting);

    private sealed class FileSystemInfoProxy
    {
        public FileSystemInfoProxy(string path)
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            IsLink = info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        public bool IsLink { get; }
    }

    private sealed class GuardedWriteStream(
        Stream inner,
        long expectedLength,
        Action<int> report,
        CancellationToken cancellationToken) : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureLength(buffer.Length);
            inner.Write(buffer);
            BytesWritten += buffer.Length;
            report(buffer.Length);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            EnsureLength(buffer.Length);
            await inner.WriteAsync(buffer, token).ConfigureAwait(false);
            BytesWritten += buffer.Length;
            report(buffer.Length);
        }
        private void EnsureLength(int count)
        {
            if (count < 0 || BytesWritten > expectedLength - count)
                throw new InvalidDataException("Archive entry expanded beyond its declared size.");
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
