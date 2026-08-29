using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class SafeFileOperationService(ApplicationStorage storage) : IFileOperationService
{
    private const int BufferSize = 1024 * 1024;
    private readonly List<FileOperationRecord> _history = [];
    private readonly SemaphoreSlim _operationGate = new(1, 1);

    public FileAccessMode AccessMode { get; set; } = FileAccessMode.ReadOnly;
    public IReadOnlyList<FileOperationRecord> History
    {
        get { lock (_history) return _history.ToArray(); }
    }

    public async Task<FileOperationRecord> CreateDirectoryAsync(
        string parentPath, string name, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ValidateSimpleName(name);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            EnsureDirectory(parentPath);
            var destination = Path.Combine(Path.GetFullPath(parentPath), name.Trim());
            EnsureAvailable(destination);
            Directory.CreateDirectory(destination);
            return AddRecord(FileOperationKind.CreateDirectory, destination, destination, canUndo: true);
        }
        finally { _operationGate.Release(); }
    }

    public Task<FileOperationRecord> CopyAsync(
        string sourcePath, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => TransferLegacyAsync(
        new FileTransferRequest
        {
            Kind = FileOperationKind.Copy,
            SourcePath = sourcePath,
            DestinationDirectory = destinationDirectory,
            VerifyAfterCopy = false
        }, progress, cancellationToken);

    public Task<FileOperationRecord> MoveAsync(
        string sourcePath, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default) => TransferLegacyAsync(
        new FileTransferRequest
        {
            Kind = FileOperationKind.Move,
            SourcePath = sourcePath,
            DestinationDirectory = destinationDirectory,
            VerifyAfterCopy = false
        }, progress, cancellationToken);

    private async Task<FileOperationRecord> TransferLegacyAsync(
        FileTransferRequest request,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken) =>
        (await TransferAsync(request, progress, cancellationToken)).Operation
        ?? throw new IOException("The operation was skipped.");

    public async Task<FileOperationRecord> RenameAsync(
        string sourcePath, string newName, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ValidateSimpleName(newName);
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var source = EnsureEntry(sourcePath);
            var parent = Path.GetDirectoryName(source)
                         ?? throw new IOException("The root of a drive cannot be renamed here.");
            var destination = Path.Combine(parent, newName.Trim());
            if (PathEquals(source, destination))
                throw new IOException("The new name is the same as the current name.");
            EnsureAvailable(destination);
            cancellationToken.ThrowIfCancellationRequested();
            MoveEntry(source, destination);
            return AddRecord(FileOperationKind.Rename, source, destination, canUndo: true);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<FileOperationRecord> TrashAsync(string path, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var source = EnsureEntry(path);
            await MoveToTrashCoreAsync(source, cancellationToken);
            return AddRecord(FileOperationKind.Trash, source, null, canUndo: false);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<FileOperationRecord?> UndoLastAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            FileOperationRecord? record;
            // AddRecord keeps the newest operation at index zero, therefore undo
            // must select the first reversible item rather than the oldest one.
            lock (_history) record = _history.FirstOrDefault(item => item.CanUndo);
            if (record is null) return null;

            cancellationToken.ThrowIfCancellationRequested();
            switch (record.Kind)
            {
                case FileOperationKind.CreateDirectory:
                    if (Directory.Exists(record.SourcePath) && !Directory.EnumerateFileSystemEntries(record.SourcePath).Any())
                        Directory.Delete(record.SourcePath);
                    else
                        throw new IOException("The created folder is no longer empty and cannot be undone safely.");
                    break;
                case FileOperationKind.Copy:
                    if (record.DestinationPath is null || !EntryExists(record.DestinationPath))
                        throw new IOException("The copied item is no longer available.");
                    await MoveToTrashCoreAsync(record.DestinationPath, cancellationToken);
                    RestoreReplacedItem(record);
                    break;
                case FileOperationKind.Move:
                case FileOperationKind.Rename:
                    if (record.DestinationPath is null || !EntryExists(record.DestinationPath))
                        throw new IOException("The moved item is no longer available.");
                    EnsureAvailable(record.SourcePath);
                    MoveEntry(record.DestinationPath, record.SourcePath);
                    RestoreReplacedItem(record);
                    break;
                default:
                    return null;
            }

            lock (_history)
            {
                var index = _history.FindIndex(item => item.Id == record.Id);
                if (index >= 0) _history[index] = record with { CanUndo = false };
            }
            return record with { CanUndo = false, IsUndo = true };
        }
        finally { _operationGate.Release(); }
    }

    public async Task<FileTransferOutcome> TransferAsync(
        FileTransferRequest request,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        if (request.Kind is not (FileOperationKind.Copy or FileOperationKind.Move))
            throw new ArgumentException("A transfer must be a copy or move operation.", nameof(request));
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var source = EnsureEntry(request.SourcePath);
            EnsureDirectory(request.DestinationDirectory);
            var destination = Path.Combine(Path.GetFullPath(request.DestinationDirectory), Path.GetFileName(source));
            if (PathEquals(source, destination))
                throw new IOException("Source and destination are the same.");
            if (Directory.Exists(source) && IsInside(source, destination))
                throw new IOException("A folder cannot be copied or moved inside itself.");
            if (EntryExists(destination))
            {
                switch (request.ConflictPolicy)
                {
                    case FileConflictPolicy.Skip:
                        return new FileTransferOutcome(null, destination, Skipped: true, Verified: false);
                    case FileConflictPolicy.KeepBoth:
                        destination = FindAvailableName(destination);
                        break;
                    case FileConflictPolicy.Replace:
                        break;
                    default:
                        EnsureAvailable(destination);
                        break;
                }
            }

            string? replacedItemBackup = null;
            if (request.ConflictPolicy == FileConflictPolicy.Replace && EntryExists(destination))
            {
                replacedItemBackup = MoveToReplacementBackup(destination);
            }

            var move = request.Kind == FileOperationKind.Move;
            var temporaryDestination = destination + $".odyssey-part-{Guid.NewGuid():N}";

            try
            {
                if (move)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        MoveEntry(source, destination);
                        var size = GetEntrySize(destination);
                        progress?.Report(new FileOperationProgress(size, size, destination));
                        var moved = AddRecord(FileOperationKind.Move, source, destination, canUndo: true, replacedItemBackup);
                        return new FileTransferOutcome(moved, destination, Skipped: false, Verified: true);
                    }
                    catch (IOException) when (!EntryExists(destination))
                    {
                        // A rename cannot cross some filesystem boundaries. Copy fully first,
                        // then remove the source only after the destination succeeded.
                    }
                }

                var total = GetEntrySize(source);
                long completed = 0;
                var progressClock = Stopwatch.StartNew();
                long lastProgressReport = -100;
                await CopyEntryAsync(source, temporaryDestination, value =>
                {
                    completed += value;
                    var now = progressClock.ElapsedMilliseconds;
                    if (completed >= total || now - lastProgressReport >= 100)
                    {
                        lastProgressReport = now;
                        progress?.Report(new FileOperationProgress(completed, total, source));
                    }
                }, cancellationToken);
                var verified = !request.VerifyAfterCopy || await VerifyEntriesAsync(source, temporaryDestination, cancellationToken);
                if (!verified) throw new IOException("The copied data failed SHA-256 verification.");
                MoveEntry(temporaryDestination, destination);
                if (move) DeleteEntry(source);

                var record = AddRecord(request.Kind, source, destination, canUndo: true, replacedItemBackup);
                return new FileTransferOutcome(record, destination, Skipped: false, Verified: request.VerifyAfterCopy);
            }
            catch
            {
                TryDeleteEntry(temporaryDestination);
                if (replacedItemBackup is not null && EntryExists(replacedItemBackup) && !EntryExists(destination))
                    MoveEntry(replacedItemBackup, destination);
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    private static async Task CopyEntryAsync(
        string source, string destination, Action<int> report, CancellationToken cancellationToken)
    {
        if (File.Exists(source))
        {
            await CopyFileAsync(source, destination, report, cancellationToken);
            return;
        }

        var sourceDirectory = new DirectoryInfo(source);
        if (sourceDirectory.LinkTarget is not null)
        {
            Directory.CreateSymbolicLink(destination, sourceDirectory.LinkTarget);
            return;
        }

        Directory.CreateDirectory(destination);
        foreach (var entry in sourceDirectory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childDestination = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
                await CopyEntryAsync(entry.FullName, childDestination, report, cancellationToken);
            else
                await CopyFileAsync(entry.FullName, childDestination, report, cancellationToken);
        }
        Directory.SetLastWriteTimeUtc(destination, sourceDirectory.LastWriteTimeUtc);
    }

    private static async Task CopyFileAsync(
        string source, string destination, Action<int> report, CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.LinkTarget is not null)
        {
            File.CreateSymbolicLink(destination, sourceInfo.LinkTarget);
            return;
        }

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                report(read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        await output.FlushAsync(cancellationToken);
        File.SetLastWriteTimeUtc(destination, sourceInfo.LastWriteTimeUtc);
    }

    private async Task MoveToTrashCoreAsync(string source, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(source))
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(source,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            else
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(source,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "gio",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };
                process.StartInfo.ArgumentList.Add("trash");
                process.StartInfo.ArgumentList.Add(source);
                process.Start();
                await process.WaitForExitAsync(cancellationToken);
                if (process.ExitCode == 0) return;
                // gio cannot trash from some mounts (temporary, network and
                // container filesystems). Fall through to Odyssey's private,
                // recoverable trash instead of turning a safe undo into a failure.
                await process.StandardError.ReadToEndAsync(cancellationToken);
            }
            catch (Win32Exception)
            {
                // Minimal desktop environments may not provide gio. Use Odyssey's
                // private recoverable trash as a safe fallback.
            }
        }

        var trashDirectory = Path.Combine(storage.DirectoryPath, "Trash");
        Directory.CreateDirectory(trashDirectory);
        var destination = Path.Combine(trashDirectory,
            $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{Path.GetFileName(source)}");
        MoveEntry(source, destination);
    }

    private FileOperationRecord AddRecord(
        FileOperationKind kind, string source, string? destination, bool canUndo,
        string? replacedItemBackup = null)
    {
        var record = new FileOperationRecord
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            SourcePath = source,
            DestinationPath = destination,
            ReplacedItemBackupPath = replacedItemBackup,
            CompletedAt = DateTimeOffset.UtcNow,
            CanUndo = canUndo
        };
        lock (_history)
        {
            _history.Insert(0, record);
            if (_history.Count > 100) _history.RemoveRange(100, _history.Count - 100);
        }
        return record;
    }

    private static void RestoreReplacedItem(FileOperationRecord record)
    {
        if (record.ReplacedItemBackupPath is null || !EntryExists(record.ReplacedItemBackupPath)) return;
        if (record.DestinationPath is null) throw new IOException("The replaced item has no destination to restore.");
        EnsureAvailable(record.DestinationPath);
        MoveEntry(record.ReplacedItemBackupPath, record.DestinationPath);
    }

    private static string FindAvailableName(string destination)
    {
        var directory = Path.GetDirectoryName(destination)!;
        var extension = File.Exists(destination) ? Path.GetExtension(destination) : string.Empty;
        var name = extension.Length > 0 ? Path.GetFileNameWithoutExtension(destination) : Path.GetFileName(destination);
        for (var index = 2; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{name} ({index}){extension}");
            if (!EntryExists(candidate)) return candidate;
        }
        throw new IOException("No available destination name could be generated.");
    }

    private string MoveToReplacementBackup(string destination)
    {
        var backupDirectory = Path.Combine(storage.DirectoryPath, "OperationBackups");
        var centralBackup = Path.Combine(backupDirectory,
            $"{Path.GetFileName(destination)}.odyssey-backup-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(backupDirectory);
            MoveEntry(destination, centralBackup);
            return centralBackup;
        }
        catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && EntryExists(destination))
        {
            TryDeleteEntry(centralBackup);
        }

        var directory = Path.GetDirectoryName(destination)!;
        var name = Path.GetFileName(destination);
        var adjacentBackup = Path.Combine(directory, $".{name}.odyssey-backup-{Guid.NewGuid():N}");
        MoveEntry(destination, adjacentBackup);
        return adjacentBackup;
    }

    private static async Task<bool> VerifyEntriesAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(source)) return await FilesEqualAsync(source, destination, cancellationToken);
        if (!Directory.Exists(source) || !Directory.Exists(destination)) return false;

        var sourceRoot = Path.GetFullPath(source);
        var destinationRoot = Path.GetFullPath(destination);
        var sourceEntries = EnumerateTreeWithoutFollowingLinks(sourceRoot)
            .Select(path => Path.GetRelativePath(sourceRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var destinationEntries = EnumerateTreeWithoutFollowingLinks(destinationRoot)
            .Select(path => Path.GetRelativePath(destinationRoot, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (!sourceEntries.SequenceEqual(destinationEntries, PathComparer)) return false;

        foreach (var relativePath in sourceEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(sourceRoot, relativePath);
            var destinationPath = Path.Combine(destinationRoot, relativePath);
            if (Directory.Exists(sourcePath) != Directory.Exists(destinationPath)) return false;
            if (File.Exists(sourcePath) && !await FilesEqualAsync(sourcePath, destinationPath, cancellationToken)) return false;
        }
        return true;
    }

    private static IEnumerable<string> EnumerateTreeWithoutFollowingLinks(string root)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                yield return entry.FullName;
                if (entry is DirectoryInfo directory && directory.LinkTarget is null)
                    pending.Push(directory);
            }
        }
    }

    private static async Task<bool> FilesEqualAsync(
        string source, string destination, CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        if (!destinationInfo.Exists || sourceInfo.Length != destinationInfo.Length) return false;
        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destinationStream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceHash = await SHA256.HashDataAsync(sourceStream, cancellationToken);
        var destinationHash = await SHA256.HashDataAsync(destinationStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash);
    }

    private void EnsureWritable()
    {
        if (AccessMode == FileAccessMode.ReadOnly)
            throw new InvalidOperationException("Odyssey is in read-only mode. Enable file management in Settings first.");
    }

    private static string EnsureEntry(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!EntryExists(fullPath)) throw new FileNotFoundException("The selected item is no longer available.", fullPath);
        return fullPath;
    }

    private static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Destination folder is unavailable: {path}");
    }

    private static void EnsureAvailable(string path)
    {
        if (EntryExists(path)) throw new IOException($"An item already exists at the destination: {path}");
    }

    private static void ValidateSimpleName(string name)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed is "." or ".." ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.Contains(Path.DirectorySeparatorChar) || trimmed.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Enter a valid name without a path.", nameof(name));
    }

    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static bool PathEquals(string left, string right) =>
        PathComparer.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)));

    private static bool IsInside(string parent, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(candidate).StartsWith(prefix, comparison);
    }

    private static long GetEntrySize(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry is FileInfo file) total += file.Length;
                else if (entry is DirectoryInfo directory && directory.LinkTarget is null) pending.Push(directory);
            }
        }
        return total;
    }

    private static void MoveEntry(string source, string destination)
    {
        if (File.Exists(source)) File.Move(source, destination, overwrite: false);
        else Directory.Move(source, destination);
    }

    private static void DeleteEntry(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void TryDeleteEntry(string path)
    {
        try { DeleteEntry(path); }
        catch { /* Best-effort cleanup of an incomplete destination. */ }
    }
}
