using System.Runtime.CompilerServices;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class PortableFileSystemScanner(IExclusionPolicy exclusionPolicy) : IFileSystemScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0
    };

    public async IAsyncEnumerable<FileSystemItem> EnumerateAsync(
        ScanTarget target,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Directory.Exists(target.RootPath))
        {
            yield return new FileSystemItem(target.RootPath, FileEntryType.Directory, "Target directory is unavailable.");
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(target.RootPath));
        var itemsSinceYield = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            var iterator = TryOpen(directory, out var openError);
            if (iterator is null)
            {
                yield return new FileSystemItem(directory, FileEntryType.Directory, openError);
                continue;
            }

            using (iterator)
            {
                while (TryMoveNext(iterator, out var candidate, out var moveError))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (candidate is null)
                    {
                        if (moveError is not null)
                            yield return new FileSystemItem(directory, FileEntryType.Directory, moveError);
                        break;
                    }

                    if (exclusionPolicy.IsExcluded(target.RootPath, candidate, target.ExcludedPatterns))
                        continue;

                    if (!TryGetAttributes(candidate, out var attributes, out var attributeError))
                    {
                        yield return new FileSystemItem(candidate, FileEntryType.File, attributeError);
                        continue;
                    }

                    var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    var type = isDirectory ? FileEntryType.Directory : FileEntryType.File;
                    if (isDirectory || IncludesExtension(target, candidate))
                        yield return new FileSystemItem(candidate, type);

                    if (target.Recursive && isDirectory && !attributes.HasFlag(FileAttributes.ReparsePoint))
                        pending.Push(candidate);

                    // Channel backpressure normally yields naturally. On very fast
                    // sources, yield occasionally instead of scheduling every entry.
                    if ((++itemsSinceYield & 0xff) == 0)
                        await Task.Yield();
                }
            }
        }
    }

    private static IEnumerator<string>? TryOpen(string directory, out string? error)
    {
        try
        {
            error = null;
            return Directory.EnumerateFileSystemEntries(directory, "*", Options).GetEnumerator();
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            error = ex.Message;
            return null;
        }
    }

    private static bool TryMoveNext(IEnumerator<string> iterator, out string? candidate, out string? error)
    {
        try
        {
            error = null;
            if (iterator.MoveNext())
            {
                candidate = iterator.Current;
                return true;
            }

            candidate = null;
            return false;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            candidate = null;
            error = ex.Message;
            return true;
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes, out string? error)
    {
        try
        {
            attributes = File.GetAttributes(path);
            error = null;
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            attributes = default;
            error = ex.Message;
            return false;
        }
    }

    private static bool IncludesExtension(ScanTarget target, string path) =>
        target.IncludedExtensions.Count == 0 || target.IncludedExtensions.Any(extension =>
            string.Equals(NormalizeExtension(extension), Path.GetExtension(path), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeExtension(string value) => value.StartsWith('.') ? value : $".{value}";

    internal static bool IsRecoverable(Exception exception) => exception is
        UnauthorizedAccessException or IOException or PathTooLongException or NotSupportedException or ArgumentException;
}
