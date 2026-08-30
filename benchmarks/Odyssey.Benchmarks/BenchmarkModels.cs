namespace Odyssey.Benchmarks;

internal sealed record BenchmarkReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    BenchmarkEnvironment Environment,
    BenchmarkConfiguration Configuration,
    CorpusMetric Corpus,
    PhaseMetric Scan,
    ContentMetric ContentIndex,
    IReadOnlyList<SearchMetric> Searches,
    SearchSummary SearchSummary,
    TransferMetric Transfer,
    bool ValidationPassed,
    string? PreservedWorkspacePath,
    string? CorpusManifestPath);

internal sealed record BenchmarkEnvironment(
    string OperatingSystem,
    string Framework,
    string ProcessArchitecture,
    int LogicalProcessors,
    long AvailableMemoryBytes,
    string? Commit);

internal sealed record BenchmarkConfiguration(
    int PlainTextFileCount,
    int RichDocumentCount,
    int TotalFileCount,
    int SearchIterations,
    int TransferMiB);

internal sealed record CorpusMetric(
    double DurationMilliseconds,
    long BytesWritten,
    double FilesPerSecond,
    IReadOnlyDictionary<string, long> FilesByExtension);

internal sealed record PhaseMetric(
    double DurationMilliseconds,
    long ItemsProcessed,
    double ItemsPerSecond,
    long ProcessPeakWorkingSetBytes,
    long ManagedHeapBytes);

internal sealed record ContentMetric(
    double DurationMilliseconds,
    long FilesProcessed,
    long Errors,
    double FilesPerSecond,
    long ProcessPeakWorkingSetBytes,
    long ManagedHeapBytes,
    IReadOnlyDictionary<string, long> FilesByExtension,
    IReadOnlyDictionary<string, long> ErrorsByExtension);

internal sealed record SearchMetric(
    string Name,
    string Query,
    int ExpectedCount,
    int ReturnedCount,
    int TruePositiveCount,
    int FalsePositiveCount,
    int FalseNegativeCount,
    double Precision,
    double Recall,
    double MedianLatencyMilliseconds,
    double P95LatencyMilliseconds,
    IReadOnlyList<double> LatencySamplesMilliseconds);

internal sealed record SearchSummary(
    double MinimumPrecision,
    double MinimumRecall,
    double MedianLatencyMilliseconds,
    double P95LatencyMilliseconds);

internal sealed record TransferMetric(
    long Bytes,
    double DurationMilliseconds,
    double MiBPerSecond,
    bool Sha256Verified);

internal sealed record GeneratedCorpus(
    long BytesWritten,
    IReadOnlyList<BenchmarkQuery> Queries,
    IReadOnlyDictionary<string, long> FilesByExtension);

internal sealed record ContentIndexResult(
    long Files,
    long Errors,
    IReadOnlyDictionary<string, long> FilesByExtension,
    IReadOnlyDictionary<string, long> ErrorsByExtension);

internal sealed record BenchmarkQuery(
    string Name,
    string Query,
    IReadOnlySet<string> ExpectedPaths);

internal sealed record BenchmarkCorpusManifest(
    int SchemaVersion,
    int PlainTextFileCount,
    int RichDocumentCount,
    int TotalFileCount,
    IReadOnlyDictionary<string, long> FilesByExtension,
    IReadOnlyList<BenchmarkManifestQuery> Queries);

internal sealed record BenchmarkManifestQuery(
    string Name,
    string Query,
    IReadOnlyList<string> ExpectedRelativePaths);
