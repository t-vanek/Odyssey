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

public interface IFilteredDirectoryBrowserService
{
    Task<DirectoryPage> GetFilteredPageAsync(
        string path,
        int offset,
        int pageSize,
        DirectoryNameFilter filter,
        CancellationToken cancellationToken = default);
}

public interface IDirectoryComparisonService
{
    Task<DirectoryComparisonResult> CompareAsync(
        DirectoryComparisonRequest request,
        IProgress<DirectoryComparisonProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IDirectorySynchronizationPlanner
{
    IReadOnlyList<FileTransferRequest> CreatePlan(
        DirectoryComparisonResult comparison,
        IEnumerable<DirectoryComparisonEntry> selectedEntries,
        DirectorySyncDirection direction,
        bool verifyAfterCopy);
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
    Task<FileTransferOutcome> TransferAsync(
        FileTransferRequest request,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IFileTransferQueueService : IAsyncDisposable
{
    event EventHandler? Changed;
    IReadOnlyList<FileTransferJob> Items { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileTransferJob>> EnqueueAsync(
        IEnumerable<FileTransferRequest> requests,
        CancellationToken cancellationToken = default);
    Task PauseAsync(Guid id, CancellationToken cancellationToken = default);
    Task ResumeAsync(Guid id, CancellationToken cancellationToken = default);
    Task RetryAsync(Guid id, CancellationToken cancellationToken = default);
    Task CancelAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISftpConnectionService : IAsyncDisposable
{
    IReadOnlyList<SftpConnectionInfo> Connections { get; }
    Task<SftpConnectionInfo> ConnectAsync(SftpConnectionRequest request, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string connectionKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FileLocationEntry>> ListAsync(
        string connectionKey, string path, CancellationToken cancellationToken = default);
    Task<FileTransferOutcome> TransferAsync(
        FileTransferRequest request,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IFileLocationProvider
{
    FileTransferEndpointKind Kind { get; }
    FileLocationCapabilities Capabilities { get; }
    Task<IReadOnlyList<FileLocationEntry>> ListAsync(
        string path,
        string? connectionKey = null,
        CancellationToken cancellationToken = default);
}

public interface IFileLocationProviderRegistry
{
    IFileLocationProvider Get(FileTransferEndpointKind kind);
}

public interface IArchiveService
{
    FileAccessMode AccessMode { get; set; }
    IReadOnlyList<ArchiveFormatSupport> Formats { get; }
    bool CanOpen(string archivePath);
    ArchiveCapabilities GetCapabilities(string archivePath);
    Task<IReadOnlyList<ArchiveEntry>> ListAsync(
        string archivePath,
        string directoryPath = "",
        CancellationToken cancellationToken = default);
    Task<FileTransferOutcome> ExtractAsync(
        string archivePath,
        string entryPath,
        string destinationDirectory,
        FileConflictPolicy conflictPolicy = FileConflictPolicy.Fail,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IArchiveMutationService
{
    FileAccessMode AccessMode { get; set; }
    Task<FileTransferOutcome> CreateAsync(
        string archivePath,
        IReadOnlyList<string> sourcePaths,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<FileTransferOutcome> AddAsync(
        string archivePath,
        string sourcePath,
        string destinationDirectory = "",
        FileConflictPolicy conflictPolicy = FileConflictPolicy.Fail,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(
        string archivePath,
        string entryPath,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task DeleteManyAsync(
        string archivePath,
        IReadOnlyList<string> entryPaths,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IArchiveRecoveryService
{
    Task<ArchiveRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default);
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
        CancellationToken cancellationToken,
        Guid? resumedFromScanId = null);
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
    Task SaveScanCheckpointAsync(Guid scanId, ScanProgress progress, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InterruptedScanRecovery>> RecoverInterruptedScansAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default);
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
