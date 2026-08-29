using System.Collections.Concurrent;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class CachedDirectoryBrowserService : IDirectoryBrowserService, IFilteredDirectoryBrowserService
{
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(PathComparer);
    private readonly LinkedList<string> _lru = new();
    private readonly SystemPerformanceProfile _performance;
    private long _cachedBytes;

    public CachedDirectoryBrowserService()
        : this(SystemPerformanceProfile.Current) { }

    public CachedDirectoryBrowserService(SystemPerformanceProfile performance) => _performance = performance;

    public async Task<DirectoryPage> GetPageAsync(
        string path, int offset, int pageSize, CancellationToken cancellationToken = default)
        => await GetPageCoreAsync(path, offset, pageSize, null, cancellationToken).ConfigureAwait(false);

    public async Task<DirectoryPage> GetFilteredPageAsync(
        string path,
        int offset,
        int pageSize,
        DirectoryNameFilter filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (!FileNamePatternMatcher.TryCreate(filter, out _, out var error))
            throw new ArgumentException(error, nameof(filter));
        return await GetPageCoreAsync(path, offset, pageSize, filter, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DirectoryPage> GetPageCoreAsync(
        string path,
        int offset,
        int pageSize,
        DirectoryNameFilter? filter,
        CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > 5000) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var fullPath = Path.GetFullPath(path);
        var snapshot = TryGetValid(fullPath);
        if (snapshot is null)
        {
            snapshot = await Task.Run(() => ReadDirectory(fullPath, cancellationToken), cancellationToken).ConfigureAwait(false);
            Put(fullPath, snapshot);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (filter is not null && !string.IsNullOrEmpty(filter.Pattern))
        {
            FileNamePatternMatcher.TryCreate(filter, out var matcher, out _);
            var filteredItems = new List<DirectoryItem>(pageSize);
            var totalMatches = 0;
            foreach (var item in snapshot.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!matcher(item.Name)) continue;
                if (totalMatches >= offset && filteredItems.Count < pageSize) filteredItems.Add(item);
                totalMatches++;
            }
            return new DirectoryPage(filteredItems, offset, totalMatches,
                offset + filteredItems.Count < totalMatches);
        }
        var items = snapshot.Items.Skip(offset).Take(pageSize).ToArray();
        return new DirectoryPage(items, offset, snapshot.Items.Count,
            offset + items.Length < snapshot.Items.Count);
    }

    public void Invalidate(string path)
    {
        var fullPath = Path.GetFullPath(path);
        lock (_sync)
        {
            RemoveLocked(fullPath);
        }
    }

    private Snapshot? TryGetValid(string path)
    {
        lock (_sync)
        {
            if (!_cache.TryGetValue(path, out var entry)) return null;
            DateTime lastWrite;
            try { lastWrite = Directory.GetLastWriteTimeUtc(path); }
            catch { RemoveLocked(path); return null; }
            if (lastWrite != entry.Snapshot.LastWriteUtc)
            {
                RemoveLocked(path);
                return null;
            }
            _lru.Remove(path);
            _lru.AddFirst(path);
            return entry.Snapshot;
        }
    }

    private void Put(string path, Snapshot snapshot)
    {
        lock (_sync)
        {
            RemoveLocked(path);
            _cache[path] = new CacheEntry(snapshot);
            _cachedBytes += snapshot.EstimatedBytes;
            _lru.Remove(path);
            _lru.AddFirst(path);
            while (_lru.Count > _performance.DirectoryCacheEntryLimit ||
                   _cachedBytes > _performance.DirectoryCacheBudgetBytes)
            {
                var oldest = _lru.Last!.Value;
                RemoveLocked(oldest);
            }
        }
    }

    private void RemoveLocked(string path)
    {
        if (_cache.Remove(path, out var entry))
            _cachedBytes -= entry.Snapshot.EstimatedBytes;
        _lru.Remove(path);
    }

    private Snapshot ReadDirectory(string path, CancellationToken token)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists) throw new DirectoryNotFoundException(path);
        var fileSystemItems = directory.EnumerateFileSystemInfos()
            .Where(item => !TransferArtifactNames.IsInternal(item.Name))
            .ToArray();
        var items = new ConcurrentBag<DirectoryItem>();
        Parallel.ForEach(fileSystemItems, new ParallelOptions
        {
            CancellationToken = token,
            MaxDegreeOfParallelism = _performance.DirectoryWorkerCount
        }, item =>
        {
            try
            {
                items.Add(item is DirectoryInfo
                    ? new DirectoryItem(item.Name, item.FullName, FileEntryType.Directory, null,
                        item.LastWriteTimeUtc, string.Empty, FormatAttributes(item.Attributes))
                    : new DirectoryItem(item.Name, item.FullName, FileEntryType.File, ((FileInfo)item).Length,
                        item.LastWriteTimeUtc, item.Extension.TrimStart('.'), FormatAttributes(item.Attributes)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // One inaccessible or disappearing item must not block the directory.
            }
        });
        var sortedItems = items.ToList();
        sortedItems.Sort((left, right) =>
        {
            var type = right.Type.CompareTo(left.Type);
            return type != 0 ? type : PathComparer.Compare(left.Name, right.Name);
        });
        var estimatedBytes = 256L + sortedItems.Sum(EstimateBytes);
        return new Snapshot(sortedItems, directory.LastWriteTimeUtc, estimatedBytes);
    }

    private static long EstimateBytes(DirectoryItem item) => 160L +
        (item.Name.Length + item.FullPath.Length + item.Extension.Length + item.Attributes.Length) * sizeof(char);

    private static string FormatAttributes(FileAttributes attributes)
    {
        Span<char> result = stackalloc char[4];
        result[0] = attributes.HasFlag(FileAttributes.ReadOnly) ? 'R' : '-';
        result[1] = attributes.HasFlag(FileAttributes.Hidden) ? 'H' : '-';
        result[2] = attributes.HasFlag(FileAttributes.System) ? 'S' : '-';
        result[3] = attributes.HasFlag(FileAttributes.Archive) ? 'A' : '-';
        return new string(result);
    }

    private sealed record Snapshot(IReadOnlyList<DirectoryItem> Items, DateTime LastWriteUtc, long EstimatedBytes);
    private sealed record CacheEntry(Snapshot Snapshot);
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
