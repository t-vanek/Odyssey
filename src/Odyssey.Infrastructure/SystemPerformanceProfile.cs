namespace Odyssey.Infrastructure;

/// <summary>
/// Bounded resource limits derived from the machine running Odyssey. The limits
/// intentionally leave most memory and CPU time available to the operating system.
/// </summary>
public sealed record SystemPerformanceProfile(
    int ProcessorCount,
    long AvailableMemoryBytes,
    int ScanWorkerCount,
    int DirectoryWorkerCount,
    int DuplicateHashWorkerCount,
    int ScanQueueCapacity,
    int ScanBatchSize,
    long DirectoryCacheBudgetBytes,
    int DirectoryCacheEntryLimit,
    int SqliteCacheKiB,
    long SqliteMmapBytes)
{
    private const long MiB = 1024L * 1024L;
    private const long GiB = 1024L * MiB;

    public static SystemPerformanceProfile Current { get; } = Detect();

    public static SystemPerformanceProfile Detect()
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (memory <= 0)
            memory = 4 * GiB;
        return ForHardware(Environment.ProcessorCount, memory);
    }

    public static SystemPerformanceProfile ForHardware(int processorCount, long availableMemoryBytes)
    {
        var processors = Math.Clamp(processorCount, 1, 256);
        var memory = Math.Max(availableMemoryBytes, 512 * MiB);

        // Metadata work benefits from modest I/O oversubscription; hashing is
        // deliberately capped because simultaneous reads can saturate one disk.
        var scanWorkers = Math.Clamp(processors * 2, 2, 32);
        var directoryWorkers = Math.Clamp(processors, 2, 16);
        var hashWorkers = Math.Clamp(processors, 2, 8);
        var queueCapacity = Math.Clamp(scanWorkers * 512, 1_024, 16_384);
        var batchSize = Math.Clamp((int)(memory / GiB) * 125, 500, 2_000);
        var directoryCache = Math.Clamp(memory / 64, 32 * MiB, 512 * MiB);
        var directoryEntries = Math.Clamp((int)(memory / GiB) * 8, 16, 128);
        var sqliteCacheKiB = (int)(Math.Clamp(memory / 128, 32 * MiB, 128 * MiB) / 1024);
        var sqliteMmap = Math.Clamp(memory / 32, 64 * MiB, 512 * MiB);

        return new SystemPerformanceProfile(processors, memory, scanWorkers, directoryWorkers,
            hashWorkers, queueCapacity, batchSize, directoryCache, directoryEntries,
            sqliteCacheKiB, sqliteMmap);
    }
}
