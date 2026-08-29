using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class DirectorySynchronizationPlanner : IDirectorySynchronizationPlanner
{
    public IReadOnlyList<FileTransferRequest> CreatePlan(
        DirectoryComparisonResult comparison,
        IEnumerable<DirectoryComparisonEntry> selectedEntries,
        DirectorySyncDirection direction,
        bool verifyAfterCopy)
    {
        var leftToRight = direction == DirectorySyncDirection.LeftToRight;
        var destinationRoot = Path.GetFullPath(leftToRight ? comparison.RightPath : comparison.LeftPath);
        var requests = new List<FileTransferRequest>();
        foreach (var entry in selectedEntries.DistinctBy(item => item.RelativePath, PathComparer))
        {
            var source = leftToRight ? entry.Left : entry.Right;
            var destination = leftToRight ? entry.Right : entry.Left;
            if (source is null || entry.Difference is DirectoryDifferenceKind.Identical or DirectoryDifferenceKind.Error)
                continue;

            var desiredDestination = Path.GetFullPath(Path.Combine(destinationRoot, entry.RelativePath));
            if (!IsSameOrInside(destinationRoot, desiredDestination))
                throw new IOException("A synchronization entry points outside the destination directory.");
            var destinationDirectory = Path.GetDirectoryName(desiredDestination);
            if (destinationDirectory is null || !Directory.Exists(destinationDirectory))
                throw new DirectoryNotFoundException($"Synchronization destination is unavailable: {destinationDirectory}");

            requests.Add(new FileTransferRequest
            {
                Kind = FileOperationKind.Copy,
                SourcePath = source.FullPath,
                DestinationDirectory = destinationDirectory,
                ConflictPolicy = destination is null ? FileConflictPolicy.Fail : FileConflictPolicy.Replace,
                VerifyAfterCopy = verifyAfterCopy
            });
        }
        return requests;
    }

    private static bool IsSameOrInside(string parent, string candidate)
    {
        if (PathComparer.Equals(Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(candidate)))
            return true;
        var prefix = Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, PathComparison);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
