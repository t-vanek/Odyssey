using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class DuplicateAnalyzer : IDuplicateAnalyzer
{
    private readonly IOdysseyStore _store;
    private readonly ILogger<DuplicateAnalyzer> _logger;
    private readonly SystemPerformanceProfile _performance;

    public DuplicateAnalyzer(IOdysseyStore store, ILogger<DuplicateAnalyzer> logger)
        : this(store, logger, SystemPerformanceProfile.Current) { }

    public DuplicateAnalyzer(
        IOdysseyStore store,
        ILogger<DuplicateAnalyzer> logger,
        SystemPerformanceProfile performance)
    {
        _store = store;
        _logger = logger;
        _performance = performance;
    }

    public async Task<IReadOnlyCollection<DuplicateGroup>> FindAsync(Guid rescueSessionId, CancellationToken cancellationToken)
    {
        var candidates = await _store.GetDuplicateCandidatesAsync(rescueSessionId, cancellationToken).ConfigureAwait(false);
        var work = candidates.Where(x => x.Size is not null)
            .GroupBy(x => x.Size!.Value)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToArray();
        var fingerprints = new ConcurrentDictionary<long, ulong>();
        var hashed = new ConcurrentBag<DuplicateFile>();

        await Parallel.ForEachAsync(work, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = _performance.DuplicateHashWorkerCount
        }, async (file, token) =>
        {
            try
            {
                // Always hash current bytes. A cached fingerprint is useful for persistence,
                // but size and timestamp alone cannot prove that a file did not change.
                var fingerprint = await HashAsync(file.FullPath, file.Size!.Value, token).ConfigureAwait(false);
                if (file.Fingerprint != fingerprint) fingerprints[file.Id] = fingerprint;
                hashed.Add(new DuplicateFile(file.Id, file.FullPath, file.Size!.Value, fingerprint));
            }
            catch (Exception ex) when (PortableFileSystemScanner.IsRecoverable(ex))
            {
                _logger.LogDebug(ex, "A duplicate candidate could not be hashed");
            }
        }).ConfigureAwait(false);

        await _store.UpdateFingerprintsAsync(fingerprints, cancellationToken).ConfigureAwait(false);
        return await VerifyByteForByteAsync(hashed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyCollection<DuplicateGroup>> VerifyByteForByteAsync(
        IEnumerable<DuplicateFile> files,
        CancellationToken cancellationToken)
    {
        var result = new List<DuplicateGroup>();
        foreach (var hashGroup in files.GroupBy(x => (x.Size, x.Fingerprint)).Where(group => group.Count() > 1))
        {
            var contentGroups = new List<List<DuplicateFile>>();
            foreach (var file in hashGroup)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var placed = false;
                foreach (var contentGroup in contentGroups)
                {
                    try
                    {
                        if (!await FilesEqualAsync(contentGroup[0].FullPath, file.FullPath, cancellationToken).ConfigureAwait(false))
                            continue;
                        contentGroup.Add(file);
                        placed = true;
                        break;
                    }
                    catch (Exception ex) when (PortableFileSystemScanner.IsRecoverable(ex))
                    {
                        _logger.LogDebug(ex, "Duplicate candidates could not be compared exactly");
                    }
                }
                if (!placed) contentGroups.Add([file]);
            }

            result.AddRange(contentGroups.Where(group => group.Count > 1)
                .Select(group => new DuplicateGroup(hashGroup.Key.Size, hashGroup.Key.Fingerprint, group.ToArray())));
        }
        return result;
    }

    private static async Task<ulong> HashAsync(string path, long expectedSize, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != expectedSize)
            throw new IOException("The file size changed after it was indexed.");
        var hasher = new XxHash64();
        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                hasher.Append(buffer.AsSpan(0, read));
            return BitConverter.ToUInt64(hasher.GetCurrentHash());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<bool> FilesEqualAsync(
        string leftPath, string rightPath, CancellationToken cancellationToken)
    {
        await using var left = new FileStream(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var right = new FileStream(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (left.Length != right.Length) return false;

        var leftBuffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        var rightBuffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            while (true)
            {
                var leftRead = await left.ReadAsync(leftBuffer.AsMemory(0, leftBuffer.Length), cancellationToken).ConfigureAwait(false);
                var rightRead = await right.ReadAsync(rightBuffer.AsMemory(0, rightBuffer.Length), cancellationToken).ConfigureAwait(false);
                if (leftRead != rightRead) return false;
                if (leftRead == 0) return true;
                if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }
}
