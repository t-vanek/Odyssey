using System.IO.Compression;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed partial class SafeArchiveService
{
    public Task<FileTransferOutcome> CreateAsync(
        string archivePath,
        IReadOnlyList<string> sourcePaths,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteWriteAsync(
        () => CreateCoreAsync(archivePath, sourcePaths, progress, cancellationToken), cancellationToken);

    private async Task<FileTransferOutcome> CreateCoreAsync(
        string archivePath,
        IReadOnlyList<string> sourcePaths,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureArchiveWriteEnabled();
        if (sourcePaths.Count == 0) throw new ArgumentException("At least one archive input is required.", nameof(sourcePaths));
        var archive = ValidateWritableZipPath(archivePath);
        await EnsureRecoveredAsync(archive, cancellationToken).ConfigureAwait(false);
        archive = ValidateWritableZipPath(archivePath);
        if (File.Exists(archive)) throw new IOException("The destination archive already exists.");
        var additions = sourcePaths.SelectMany(source =>
            BuildSourceItems(source, string.Empty, archive, cancellationToken)).ToList();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (additions.Any(item => !paths.Add(item.ArchivePath)))
            throw new InvalidDataException("Selected inputs produce duplicate or case-colliding archive paths.");
        await RewriteZipAsync(archive, [], _ => false, additions, progress, cancellationToken).ConfigureAwait(false);
        return new FileTransferOutcome(null, archive, false, false);
    }

    public Task<FileTransferOutcome> AddAsync(
        string archivePath,
        string sourcePath,
        string destinationDirectory = "",
        FileConflictPolicy conflictPolicy = FileConflictPolicy.Fail,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteWriteAsync(
        () => AddCoreAsync(archivePath, sourcePath, destinationDirectory, conflictPolicy, progress, cancellationToken),
        cancellationToken);

    private async Task<FileTransferOutcome> AddCoreAsync(
        string archivePath,
        string sourcePath,
        string destinationDirectory,
        FileConflictPolicy conflictPolicy,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureArchiveWriteEnabled();
        var archive = ValidateWritableZipPath(archivePath);
        await EnsureRecoveredAsync(archive, cancellationToken).ConfigureAwait(false);
        archive = ValidateWritableZipPath(archivePath);
        var destination = NormalizeDirectoryPath(destinationDirectory);
        var sources = BuildSourceItems(sourcePath, destination, archive, cancellationToken);
        var rootPath = sources[0].ArchivePath;
        var existing = File.Exists(archive) ? ScanZip(archive, cancellationToken) : [];
        var resolution = ResolveArchiveConflict(rootPath, existing, conflictPolicy);
        if (resolution.Skip)
            return new FileTransferOutcome(null, $"{archive}!/{rootPath}", true, false);
        if (!PathComparer.Equals(resolution.RootPath, rootPath))
            sources = RebaseSourceItems(sources, rootPath, resolution.RootPath);

        await RewriteZipAsync(archive, existing,
            descriptor => resolution.Replace && ConflictsWithDesired(descriptor, rootPath),
            sources, progress, cancellationToken).ConfigureAwait(false);
        return new FileTransferOutcome(null, $"{archive}!/{resolution.RootPath}", false, false);
    }

    public async Task DeleteAsync(
        string archivePath,
        string entryPath,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await DeleteManyAsync(archivePath, [entryPath], progress, cancellationToken).ConfigureAwait(false);

    public Task DeleteManyAsync(
        string archivePath,
        IReadOnlyList<string> entryPaths,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => ExecuteWriteAsync(
        () => DeleteManyCoreAsync(archivePath, entryPaths, progress, cancellationToken), cancellationToken);

    private async Task DeleteManyCoreAsync(
        string archivePath,
        IReadOnlyList<string> entryPaths,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        EnsureArchiveWriteEnabled();
        if (entryPaths.Count == 0) throw new ArgumentException("At least one archive entry is required.", nameof(entryPaths));
        var archive = ValidateWritableZipPath(archivePath);
        await EnsureRecoveredAsync(archive, cancellationToken).ConfigureAwait(false);
        archive = ValidateWritableZipPath(archivePath, mustExist: true);
        var selected = entryPaths.Select(path => NormalizeEntryPath(path, isDirectory: false))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var existing = ScanZip(archive, cancellationToken);
        var missing = selected.FirstOrDefault(path =>
            !existing.Any(descriptor => IsSameOrChild(descriptor.Path, path)));
        if (missing is not null) throw new FileNotFoundException("Archive entry not found.", missing);
        await RewriteZipAsync(archive, existing,
            descriptor => selected.Any(path => IsSameOrChild(descriptor.Path, path)), [], progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<T> ExecuteWriteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await operation().ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private async Task ExecuteWriteAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await operation().ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private void EnsureArchiveWriteEnabled()
    {
        if (AccessMode != FileAccessMode.ManageFiles)
            throw new InvalidOperationException("Odyssey is in read-only mode.");
    }

    private async Task EnsureRecoveredAsync(string archivePath, CancellationToken cancellationToken)
    {
        _ = await RecoverBeforeWriteAsync(archivePath, cancellationToken).ConfigureAwait(false);
    }

    private string ValidateWritableZipPath(string archivePath, bool mustExist = false)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
            throw new ArgumentException("Archive path is required.", nameof(archivePath));
        var path = Path.GetFullPath(archivePath);
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Transactional archive writing is currently supported only for ZIP.");
        var parent = Path.GetDirectoryName(path)
                     ?? throw new ArgumentException("Archive parent directory is required.", nameof(archivePath));
        ValidateDestinationDirectory(parent);
        if (File.Exists(path))
        {
            ValidateArchivePath(path);
        }
        else if (Directory.Exists(path))
        {
            throw new IOException("The archive path points to a directory.");
        }
        else if (mustExist)
        {
            throw new FileNotFoundException("Archive not found.", path);
        }
        return path;
    }

    private List<ArchiveSourceItem> BuildSourceItems(
        string sourcePath,
        string destinationDirectory,
        string archivePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new ArgumentException("Source path is required.", nameof(sourcePath));
        var source = Path.GetFullPath(sourcePath);
        if (PathComparer.Equals(source, archivePath))
            throw new IOException("An archive cannot be added to itself.");
        ValidateSourceAncestors(source);
        FileSystemInfo root = Directory.Exists(source) ? new DirectoryInfo(source) : new FileInfo(source);
        if (!root.Exists) throw new FileNotFoundException("Archive input was not found.", source);
        EnsureSourceIsNotLink(root);
        var rootName = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
        var archiveRoot = NormalizeEntryPath(
            destinationDirectory.Length == 0 ? rootName : $"{destinationDirectory}/{rootName}",
            root is DirectoryInfo);
        var items = new List<ArchiveSourceItem>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = 0L;

        void Add(FileSystemInfo item, string entryPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSourceIsNotLink(item);
            var isDirectory = item is DirectoryInfo;
            var normalized = NormalizeEntryPath(entryPath, isDirectory);
            if (!paths.Add(normalized))
                throw new InvalidDataException("Source contains duplicate or case-colliding archive paths.");
            if (items.Count >= _limits.MaximumEntries)
                throw new InvalidDataException("Archive entry-count limit exceeded.");
            var size = isDirectory ? 0 : ((FileInfo)item).Length;
            ValidateEntryQuota(size, null, ref total);
            items.Add(new ArchiveSourceItem(item.FullName, normalized, isDirectory, size,
                new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        Add(root, archiveRoot);
        if (root is not DirectoryInfo rootDirectory) return items;
        var pending = new Stack<(DirectoryInfo Directory, string ArchivePath)>();
        pending.Push((rootDirectory, archiveRoot));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            FileSystemInfo[] children;
            try { children = current.Directory.EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Archive input could not be enumerated: {current.Directory.FullName}", ex);
            }
            for (var index = children.Length - 1; index >= 0; index--)
            {
                var child = children[index];
                var childPath = $"{current.ArchivePath}/{child.Name}";
                Add(child, childPath);
                if (child is DirectoryInfo directory) pending.Push((directory, childPath));
            }
        }
        return items;
    }

    private static void EnsureSourceIsNotLink(FileSystemInfo item)
    {
        item.Refresh();
        if (item.LinkTarget is not null || item.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Symbolic links and reparse points are not added to archives.");
    }

    private static void ValidateSourceAncestors(string sourcePath)
    {
        var current = Directory.Exists(sourcePath)
            ? new DirectoryInfo(sourcePath)
            : new FileInfo(sourcePath).Directory;
        while (current is not null)
        {
            EnsureSourceIsNotLink(current);
            current = current.Parent;
        }
    }

    private ArchiveConflictResolution ResolveArchiveConflict(
        string desiredRoot,
        IReadOnlyList<EntryDescriptor> existing,
        FileConflictPolicy policy)
    {
        var ancestorFileCollision = existing.Any(descriptor => !descriptor.IsDirectory
            && desiredRoot.StartsWith(descriptor.Path + "/", StringComparison.OrdinalIgnoreCase));
        var collides = existing.Any(descriptor => ConflictsWithDesired(descriptor, desiredRoot));
        if (!collides) return new ArchiveConflictResolution(desiredRoot, false, false);
        return policy switch
        {
            FileConflictPolicy.Fail => throw new IOException("An archive entry with this name already exists."),
            FileConflictPolicy.Skip => new ArchiveConflictResolution(desiredRoot, true, false),
            FileConflictPolicy.Replace => new ArchiveConflictResolution(desiredRoot, false, true),
            FileConflictPolicy.KeepBoth when ancestorFileCollision =>
                throw new IOException("The archive destination has a file where a directory is required."),
            FileConflictPolicy.KeepBoth => new ArchiveConflictResolution(
                FindKeepBothArchiveName(desiredRoot, existing), false, false),
            _ => throw new ArgumentOutOfRangeException(nameof(policy))
        };
    }

    private static string FindKeepBothArchiveName(string desiredRoot, IReadOnlyList<EntryDescriptor> existing)
    {
        var separator = desiredRoot.LastIndexOf('/');
        var parent = separator < 0 ? string.Empty : desiredRoot[..separator];
        var leaf = separator < 0 ? desiredRoot : desiredRoot[(separator + 1)..];
        var extension = Path.GetExtension(leaf);
        var name = leaf[..^extension.Length];
        var paths = existing.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 2; index < 10_000; index++)
        {
            var candidateLeaf = $"{name} ({index}){extension}";
            var candidate = parent.Length == 0 ? candidateLeaf : $"{parent}/{candidateLeaf}";
            if (!paths.Any(path => PathComparer.Equals(path, candidate)
                                   || path.StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        throw new IOException("A unique archive entry name could not be allocated.");
    }

    private static List<ArchiveSourceItem> RebaseSourceItems(
        IReadOnlyList<ArchiveSourceItem> sources,
        string oldRoot,
        string newRoot) => sources.Select(item => item with
    {
        ArchivePath = PathComparer.Equals(item.ArchivePath, oldRoot)
            ? newRoot
            : newRoot + item.ArchivePath[oldRoot.Length..]
    }).ToList();

    private async Task RewriteZipAsync(
        string archivePath,
        IReadOnlyList<EntryDescriptor> existing,
        Func<EntryDescriptor, bool> omit,
        IReadOnlyList<ArchiveSourceItem> additions,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var retained = existing.Where(descriptor => !omit(descriptor)).ToArray();
        var expanded = retained.Where(item => !item.IsDirectory).Sum(item => item.Size)
                       + additions.Where(item => !item.IsDirectory).Sum(item => item.Size);
        if (retained.Length + additions.Count > _limits.MaximumEntries || expanded > _limits.MaximumExpandedBytes)
            throw new InvalidDataException("Rewritten archive would exceed configured safety limits.");
        EnsureFreeSpace(Path.GetDirectoryName(archivePath)!, expanded + 1024 * 1024);
        var original = File.Exists(archivePath) ? new FileInfo(archivePath) : null;
        var originalLength = original?.Length;
        var originalModified = original?.LastWriteTimeUtc;
        var parent = Path.GetDirectoryName(archivePath)!;
        var transactionId = Guid.NewGuid();
        var temporary = ExpectedTemporaryPath(archivePath, transactionId);
        var recoveryManifest = GetRecoveryManifestPath(archivePath, transactionId);
        var completed = 0L;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var target = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                if (original is not null)
                {
                    await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    using var sourceArchive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
                    foreach (var descriptor in retained)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sourceEntry = sourceArchive.Entries[descriptor.Index];
                        var targetEntry = target.CreateEntry(descriptor.IsDirectory
                            ? descriptor.Path + "/"
                            : descriptor.Path, CompressionLevel.NoCompression);
                        targetEntry.ExternalAttributes = sourceEntry.ExternalAttributes;
                        targetEntry.LastWriteTime = SafeZipTime(descriptor.ModifiedAt);
                        if (descriptor.IsDirectory) continue;
                        await using var sourceStream = sourceEntry.Open();
                        await using var targetStream = targetEntry.Open();
                        await CopyExactlyAsync(sourceStream, targetStream, descriptor.Size, descriptor.Path,
                            value => Report(value, descriptor.Path), cancellationToken).ConfigureAwait(false);
                    }
                }

                foreach (var addition in additions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var targetEntry = target.CreateEntry(addition.IsDirectory
                        ? addition.ArchivePath + "/"
                        : addition.ArchivePath, CompressionLevel.NoCompression);
                    targetEntry.LastWriteTime = SafeZipTime(addition.ModifiedAt);
                    if (addition.IsDirectory) continue;
                    var before = new FileInfo(addition.SourcePath);
                    var length = before.Length;
                    var modified = before.LastWriteTimeUtc;
                    await using var sourceStream = new FileStream(addition.SourcePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var targetStream = targetEntry.Open();
                    await CopyExactlyAsync(sourceStream, targetStream, addition.Size, addition.ArchivePath,
                        value => Report(value, addition.ArchivePath), cancellationToken).ConfigureAwait(false);
                    var after = new FileInfo(addition.SourcePath);
                    if (!after.Exists || after.Length != length || after.LastWriteTimeUtc != modified)
                        throw new IOException($"Archive input changed while it was being read: {addition.SourcePath}");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = ScanZip(temporary, cancellationToken);
            if (original is not null)
            {
                original.Refresh();
                if (!original.Exists || original.Length != originalLength || original.LastWriteTimeUtc != originalModified)
                    throw new IOException("The archive changed while its replacement was being prepared.");
            }
            PublishArchiveReplacement(temporary, archivePath, transactionId, recoveryManifest);
        }
        catch
        {
            // An unresolved journal owns its artifacts. Recovery must see them byte-for-byte after restart.
            if (!File.Exists(recoveryManifest)) DeleteEntry(temporary);
            throw;
        }

        void Report(int value, string path)
        {
            completed += value;
            progress?.Report(new FileOperationProgress(completed, expanded, path));
        }
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long expectedLength,
        string path,
        Action<int> report,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var remaining = expectedLength;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException($"Archive entry ended early: {path}");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
            report(read);
        }
        if (await source.ReadAsync(buffer.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidDataException($"Archive entry expanded beyond its declared size: {path}");
    }

    private static DateTimeOffset SafeZipTime(DateTimeOffset? value)
    {
        var timestamp = value ?? DateTimeOffset.UtcNow;
        var minimum = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var maximum = new DateTimeOffset(2107, 12, 31, 23, 59, 58, TimeSpan.Zero);
        return timestamp < minimum ? minimum : timestamp > maximum ? maximum : timestamp;
    }

    private void PublishArchiveReplacement(
        string temporary,
        string destination,
        Guid transactionId,
        string manifestPath)
    {
        var originalExisted = File.Exists(destination);
        var backup = originalExisted ? ExpectedBackupPath(destination, transactionId) : null;
        var manifest = new ArchiveRecoveryManifest
        {
            SchemaVersion = ArchiveRecoveryManifest.CurrentSchemaVersion,
            TransactionId = transactionId,
            DestinationPath = destination,
            TemporaryPath = temporary,
            BackupPath = backup,
            OriginalExisted = originalExisted,
            State = ArchiveRecoveryState.Prepared,
            CreatedAt = DateTimeOffset.UtcNow
        };
        WriteRecoveryManifest(manifestPath, manifest);
        try
        {
            if (originalExisted)
            {
                File.Move(destination, backup!);
                PublicationCheckpoint?.Invoke(ArchivePublicationCheckpoint.OriginalBackedUp);
                manifest = manifest with { State = ArchiveRecoveryState.OriginalBackedUp };
                WriteRecoveryManifest(manifestPath, manifest);
            }
            File.Move(temporary, destination);
            PublicationCheckpoint?.Invoke(ArchivePublicationCheckpoint.ReplacementMoved);
            manifest = manifest with { State = ArchiveRecoveryState.Published };
            WriteRecoveryManifest(manifestPath, manifest);
        }
        catch
        {
            try
            {
                if (originalExisted && File.Exists(backup))
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(backup!, destination);
                }
                else if (!originalExisted && File.Exists(destination) && !File.Exists(temporary))
                {
                    File.Move(destination, temporary);
                }
                File.Delete(manifestPath);
            }
            catch
            {
                // Keep the validated journal and all remaining artifacts for startup reconciliation.
            }
            throw;
        }
        try
        {
            if (backup is not null) File.Delete(backup);
            File.Delete(manifestPath);
        }
        catch
        {
            // Published state is durable; startup recovery only removes exact validated artifacts.
        }
    }

    private static bool ConflictsWithDesired(EntryDescriptor descriptor, string desiredRoot) =>
        IsSameOrChild(descriptor.Path, desiredRoot)
        || (!descriptor.IsDirectory
            && desiredRoot.StartsWith(descriptor.Path + "/", StringComparison.OrdinalIgnoreCase));

    private static bool IsSameOrChild(string candidate, string root) =>
        PathComparer.Equals(candidate, root)
        || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private sealed record ArchiveSourceItem(
        string SourcePath,
        string ArchivePath,
        bool IsDirectory,
        long Size,
        DateTimeOffset ModifiedAt);

    private sealed record ArchiveConflictResolution(string RootPath, bool Skip, bool Replace);
}
