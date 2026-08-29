using System.Diagnostics;
using System.Security.Cryptography;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class DirectoryComparisonService : IDirectoryComparisonService
{
    private const int BufferSize = 1024 * 1024;

    public async Task<DirectoryComparisonResult> CompareAsync(
        DirectoryComparisonRequest request,
        IProgress<DirectoryComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        var leftRoot = EnsureDirectory(request.LeftPath);
        var rightRoot = EnsureDirectory(request.RightPath);
        if (PathEquals(leftRoot, rightRoot))
            throw new IOException("Choose two different directories to compare.");

        long scanned = 0;
        var left = BuildSnapshot(leftRoot, request.Recursive, value =>
            progress?.Report(new DirectoryComparisonProgress(Interlocked.Increment(ref scanned), value)), cancellationToken);
        var right = BuildSnapshot(rightRoot, request.Recursive, value =>
            progress?.Report(new DirectoryComparisonProgress(Interlocked.Increment(ref scanned), value)), cancellationToken);

        var relativePaths = left.Keys.Concat(right.Keys).Distinct(PathComparer).OrderBy(path => path, PathComparer);
        var entries = new List<DirectoryComparisonEntry>();
        foreach (var relativePath in relativePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            left.TryGetValue(relativePath, out var leftEntry);
            right.TryGetValue(relativePath, out var rightEntry);
            var difference = await DetermineDifferenceAsync(leftEntry, rightEntry, request.Mode, cancellationToken);
            entries.Add(new DirectoryComparisonEntry
            {
                RelativePath = relativePath,
                Left = leftEntry?.Side,
                Right = rightEntry?.Side,
                Difference = difference,
                Error = leftEntry?.Error ?? rightEntry?.Error
            });
        }

        var collapsed = CollapseOneSidedTrees(entries);
        return new DirectoryComparisonResult(leftRoot, rightRoot, collapsed, clock.Elapsed);
    }

    private static Dictionary<string, SnapshotEntry> BuildSnapshot(
        string root, bool recursive, Action<string> report, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SnapshotEntry>(PathComparer);
        var pending = new Stack<(DirectoryInfo Directory, string RelativePath)>();
        pending.Push((new DirectoryInfo(root), string.Empty));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, parentRelativePath) = pending.Pop();
            FileSystemInfo[] children;
            try
            {
                children = directory.EnumerateFileSystemInfos()
                    .Where(item => !TransferArtifactNames.IsInternal(item.Name))
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var issuePath = string.IsNullOrEmpty(parentRelativePath) ? "." : parentRelativePath;
                result[issuePath] = new SnapshotEntry(null, ex.Message);
                continue;
            }

            foreach (var child in children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = string.IsNullOrEmpty(parentRelativePath)
                    ? child.Name
                    : Path.Combine(parentRelativePath, child.Name);
                try
                {
                    var isDirectory = child is DirectoryInfo;
                    var side = new DirectoryComparisonSide(
                        child.FullName,
                        isDirectory ? FileEntryType.Directory : FileEntryType.File,
                        child is FileInfo file ? file.Length : null,
                        new DateTimeOffset(child.LastWriteTimeUtc, TimeSpan.Zero),
                        child.LinkTarget is not null,
                        child.LinkTarget);
                    result[relativePath] = new SnapshotEntry(side, null);
                    report(child.FullName);
                    if (recursive && child is DirectoryInfo childDirectory && child.LinkTarget is null)
                        pending.Push((childDirectory, relativePath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result[relativePath] = new SnapshotEntry(null, ex.Message);
                }
            }
        }
        return result;
    }

    private static async Task<DirectoryDifferenceKind> DetermineDifferenceAsync(
        SnapshotEntry? left,
        SnapshotEntry? right,
        DirectoryComparisonMode mode,
        CancellationToken cancellationToken)
    {
        if (left?.Error is not null || right?.Error is not null) return DirectoryDifferenceKind.Error;
        if (left?.Side is null) return DirectoryDifferenceKind.OnlyRight;
        if (right?.Side is null) return DirectoryDifferenceKind.OnlyLeft;
        if (left.Side.Type != right.Side.Type) return DirectoryDifferenceKind.Different;
        if (left.Side.IsSymbolicLink || right.Side.IsSymbolicLink)
            return left.Side.IsSymbolicLink == right.Side.IsSymbolicLink
                   && string.Equals(left.Side.LinkTarget, right.Side.LinkTarget, StringComparison.Ordinal)
                ? DirectoryDifferenceKind.Identical
                : DirectoryDifferenceKind.Different;
        if (left.Side.Type == FileEntryType.Directory) return DirectoryDifferenceKind.Identical;
        if (left.Side.Size != right.Side.Size) return DirectoryDifferenceKind.Different;
        if (mode == DirectoryComparisonMode.SizeAndModifiedTime)
            return left.Side.ModifiedAt == right.Side.ModifiedAt
                ? DirectoryDifferenceKind.Identical
                : DirectoryDifferenceKind.Different;

        return await FilesEqualAsync(left.Side.FullPath, right.Side.FullPath, cancellationToken)
            ? DirectoryDifferenceKind.Identical
            : DirectoryDifferenceKind.Different;
    }

    private static IReadOnlyList<DirectoryComparisonEntry> CollapseOneSidedTrees(
        IReadOnlyList<DirectoryComparisonEntry> entries)
    {
        var collapsedRoots = new List<string>();
        var result = new List<DirectoryComparisonEntry>(entries.Count);
        foreach (var entry in entries.OrderBy(item => PathDepth(item.RelativePath)).ThenBy(item => item.RelativePath, PathComparer))
        {
            if (collapsedRoots.Any(root => IsBelow(root, entry.RelativePath))) continue;
            result.Add(entry);
            var containsDirectory = entry.Left?.Type == FileEntryType.Directory || entry.Right?.Type == FileEntryType.Directory;
            if (containsDirectory && entry.Difference is
                    DirectoryDifferenceKind.OnlyLeft or DirectoryDifferenceKind.OnlyRight or DirectoryDifferenceKind.Different)
                collapsedRoots.Add(entry.RelativePath);
        }
        return result.OrderBy(item => item.RelativePath, PathComparer).ToArray();
    }

    private static async Task<bool> FilesEqualAsync(string left, string right, CancellationToken cancellationToken)
    {
        await using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var leftHash = await SHA256.HashDataAsync(leftStream, cancellationToken);
        var rightHash = await SHA256.HashDataAsync(rightStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) throw new DirectoryNotFoundException($"Directory is unavailable: {fullPath}");
        return fullPath;
    }

    private static int PathDepth(string path) => path.Count(character =>
        character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar);

    private static bool IsBelow(string parent, string candidate)
    {
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static bool PathEquals(string left, string right) => PathComparer.Equals(
        Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right));

    private sealed record SnapshotEntry(DirectoryComparisonSide? Side, string? Error);
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
