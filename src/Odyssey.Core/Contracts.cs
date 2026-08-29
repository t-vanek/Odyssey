namespace Odyssey.Core;

public interface IFileSystemScanner
{
    IAsyncEnumerable<FileSystemItem> EnumerateAsync(
        ScanTarget target,
        CancellationToken cancellationToken);
}

public interface IStorageVolumeDiscovery
{
    Task<IReadOnlyList<StorageVolume>> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface IDirectoryBrowserService
{
    Task<DirectoryPage> GetPageAsync(
        string path, int offset, int pageSize, CancellationToken cancellationToken = default);
    void Invalidate(string path);
}

public interface IDiskManagementService
{
    Task UnmountAsync(StorageVolume volume, CancellationToken cancellationToken = default);
    Task EjectAsync(StorageVolume volume, CancellationToken cancellationToken = default);
}

public interface IFileOperationService
{
    FileAccessMode AccessMode { get; set; }
    IReadOnlyList<FileOperationRecord> History { get; }

    Task<FileOperationRecord> CreateDirectoryAsync(string parentPath, string name, CancellationToken cancellationToken = default);
    Task<FileOperationRecord> CopyAsync(string sourcePath, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<FileOperationRecord> MoveAsync(string sourcePath, string destinationDirectory,
        IProgress<FileOperationProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<FileOperationRecord> RenameAsync(string sourcePath, string newName, CancellationToken cancellationToken = default);
    Task<FileOperationRecord> TrashAsync(string path, CancellationToken cancellationToken = default);
    Task<FileOperationRecord?> UndoLastAsync(CancellationToken cancellationToken = default);
}

public interface IFileClassifier
{
    FileCategory Classify(string? extension, FileEntryType type);
}

public interface IExclusionPolicy
{
    bool IsExcluded(string rootPath, string candidatePath, IReadOnlyCollection<string> configuredPatterns);
}

public interface IContentExtractor
{
    IReadOnlySet<string> SupportedExtensions { get; }
    bool CanHandle(string extension);
    Task<ExtractedContent?> ExtractAsync(string path, CancellationToken cancellationToken = default);
}

public interface IOcrCapability
{
    bool IsAvailable { get; }
    IReadOnlyCollection<string> Languages { get; }
    string? UnavailableReason { get; }
}

public interface ISearchService
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task WarmupAsync(Guid? sessionId, CancellationToken cancellationToken = default);
    Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SearchSuggestion>> SuggestAsync(SearchSuggestionRequest request, CancellationToken cancellationToken);
    Task RememberSearchAsync(string query, Guid? sessionId, CancellationToken cancellationToken = default);
}

public interface ISystemSearchHistoryService
{
    Task WarmupAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<string> Suggest(string query, int limit);
}

public interface IDuplicateAnalyzer
{
    Task<IReadOnlyCollection<DuplicateGroup>> FindAsync(
        Guid rescueSessionId,
        CancellationToken cancellationToken);
}

public interface IBackgroundAutomationService : IAsyncDisposable
{
    event EventHandler<BackgroundAutomationStatus>? StatusChanged;
    Task StartAsync(Guid sessionId, IReadOnlyCollection<ScanTarget> targets, CancellationToken cancellationToken = default);
    void UpdateTargets(IReadOnlyCollection<ScanTarget> targets);
    void NotifyTargetScanned(ScanTarget target);
    void NotifyUserActivity();
    Task StopAsync();
}

public interface IScanCoordinator
{
    Task<ScanSession> ScanAsync(
        ScanTarget target,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken);
}

public interface IOdysseyStore
{
    string DatabasePath { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RescueSession>> GetSessionsAsync(CancellationToken cancellationToken = default);
    Task<RescueSession> SaveSessionAsync(RescueSession session, CancellationToken cancellationToken = default);
    Task RenameSessionAsync(Guid id, string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanTarget>> GetTargetsAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<ScanTarget> SaveTargetAsync(ScanTarget target, CancellationToken cancellationToken = default);
    Task RemoveTargetAsync(Guid targetId, CancellationToken cancellationToken = default);

    Task StartScanAsync(ScanSession session, CancellationToken cancellationToken = default);
    Task CompleteScanAsync(Guid scanId, ScanStatus status, DateTimeOffset completedAt, CancellationToken cancellationToken = default);
    Task UpsertEntriesAsync(Guid scanId, IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default);
    Task MarkMissingAsync(Guid targetId, Guid successfulScanId, CancellationToken cancellationToken = default);
    Task AddScanErrorsAsync(IReadOnlyList<ScanError> errors, CancellationToken cancellationToken = default);
    Task<bool> HasCompletedScanAsync(Guid targetId, CancellationToken cancellationToken = default);
    Task ApplyFileOperationsAsync(
        IReadOnlyList<FileOperationRecord> operations,
        IReadOnlyList<ScanTarget> targets,
        CancellationToken cancellationToken = default);

    Task<SessionAnalysis> GetAnalysisAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileEntry>> GetDuplicateCandidatesAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task UpdateFingerprintsAsync(IReadOnlyDictionary<long, ulong> fingerprints, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ContentExtractionCandidate>> GetContentExtractionCandidatesAsync(
        Guid sessionId,
        IReadOnlyCollection<string> extensions,
        int limit,
        CancellationToken cancellationToken = default);
    Task UpdateExtractedContentsAsync(
        IReadOnlyCollection<ContentIndexUpdate> updates,
        CancellationToken cancellationToken = default);
    Task MaintainIndexAsync(CancellationToken cancellationToken = default);
}
