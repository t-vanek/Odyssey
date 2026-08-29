namespace Odyssey.Core;

public enum SessionMode { StandardReadOnly, ForensicReadOnly }
public enum FileAccessMode { ReadOnly, ManageFiles }
public enum FileEntryType { File, Directory }
public enum FileCategory { Documents, Images, Videos, Audio, Archives, SourceCode, Executables, Other }
public enum ScanStatus { Running, Completed, Cancelled, Failed, Interrupted }

public sealed record RescueSession
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required SessionMode Mode { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record ScanTarget
{
    public required Guid Id { get; init; }
    public required Guid SessionId { get; init; }
    public required string RootPath { get; init; }
    public bool Recursive { get; init; } = true;
    public IReadOnlyCollection<string> IncludedExtensions { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> ExcludedPatterns { get; init; } = Array.Empty<string>();
}

public sealed record ScanSession
{
    public required Guid Id { get; init; }
    public required Guid TargetId { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public ScanStatus Status { get; init; }
    public Guid? ResumedFromScanId { get; init; }
}

public sealed record FileEntry
{
    public long Id { get; init; }
    public required Guid TargetId { get; init; }
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required string ParentPath { get; init; }
    public string? Extension { get; init; }
    public required FileEntryType Type { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public FileCategory Category { get; init; }
    public ulong? Fingerprint { get; init; }
    public bool IsMissing { get; init; }
}

public sealed record FileSystemItem(
    string FullPath,
    FileEntryType Type,
    string? Error = null);

public sealed record ScanError(
    Guid ScanId,
    string? Path,
    string Message,
    DateTimeOffset OccurredAt);

public sealed record ScanProgress(
    long FilesDiscovered,
    long DirectoriesDiscovered,
    long EntriesIndexed,
    long BytesObserved,
    long Errors,
    TimeSpan Elapsed,
    string? CurrentPath = null);

public sealed record InterruptedScanRecovery(
    Guid ScanId,
    Guid TargetId,
    ScanProgress Checkpoint,
    DateTimeOffset UpdatedAt);

public sealed record SearchRequest
{
    public string Query { get; init; } = string.Empty;
    public string? Extension { get; init; }
    public FileCategory? Category { get; init; }
    public long? MinimumSize { get; init; }
    public long? MaximumSize { get; init; }
    public DateTimeOffset? ModifiedFrom { get; init; }
    public DateTimeOffset? ModifiedTo { get; init; }
    public Guid? TargetId { get; init; }
    public Guid? SessionId { get; init; }
    public int Limit { get; init; } = 200;
    public int Offset { get; init; }
}

public enum SearchSuggestionKind { History, SystemHistory }

public sealed record SearchSuggestion(string Text, SearchSuggestionKind Kind);

public sealed record SearchSuggestionRequest
{
    public string Query { get; init; } = string.Empty;
    public Guid? SessionId { get; init; }
    public int Limit { get; init; } = 7;
}

[Flags]
public enum SearchMatchEvidence
{
    None = 0,
    ExactName = 1,
    Name = 2,
    Path = 4,
    Content = 8
}

public enum SearchConfidenceBand { Possible, Medium, High }

public sealed record SearchResult
{
    public required long FileId { get; init; }
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required FileEntryType Type { get; init; }
    public required FileCategory Category { get; init; }
    public long? Size { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public required double Score { get; init; }
    public SearchMatchEvidence MatchEvidence { get; init; }
    public required Guid TargetId { get; init; }
    public bool IsMissing { get; init; }
    public string? MatchSnippet { get; init; }
    public IReadOnlyCollection<string> MatchedTerms { get; init; } = Array.Empty<string>();
    public SearchConfidenceBand Confidence => Score >= 0.78
        ? SearchConfidenceBand.High
        : Score >= 0.52
            ? SearchConfidenceBand.Medium
            : SearchConfidenceBand.Possible;
}

public sealed record SearchResponse(
    IReadOnlyCollection<SearchResult> Results,
    TimeSpan Elapsed,
    int TotalReturned,
    bool HasMore = false);

public sealed record DuplicateFile(long FileId, string FullPath, long Size, ulong Fingerprint);
public sealed record DuplicateGroup(long Size, ulong Fingerprint, IReadOnlyCollection<DuplicateFile> Files);

public sealed record CategoryStatistic(FileCategory Category, long Count, long Size);

public sealed record SessionAnalysis(
    long TotalFiles,
    long TotalDirectories,
    long TotalIndexedSize,
    long MissingEntries,
    long ScanErrors,
    long DuplicateCandidateGroups,
    IReadOnlyCollection<CategoryStatistic> Categories);

public sealed record ExtractedContent(string Text, string? Title = null);

public sealed record ContentExtractionCandidate(
    long FileId,
    string FullPath,
    string Extension,
    long? Size,
    DateTimeOffset? ModifiedAt);

public sealed record ContentIndexUpdate(
    long FileId,
    DateTimeOffset? SourceModifiedAt,
    string? Text,
    string? Error);

public enum BackgroundActivity
{
    Idle,
    Watching,
    ScanningChanges,
    ResumingScan,
    IndexingContent,
    MaintainingIndex,
    WaitingForTarget,
    Failed
}

public sealed record BackgroundAutomationStatus(
    BackgroundActivity Activity,
    string? Path = null,
    int Completed = 0,
    string? Detail = null);

public sealed record StorageVolume(
    string RootPath,
    string Name,
    bool IsRemovable,
    string? DevicePath = null,
    bool CanUnmount = false);

public sealed record DirectoryItem(
    string Name,
    string FullPath,
    FileEntryType Type,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string Extension,
    string Attributes);

public sealed record DirectoryPage(
    IReadOnlyList<DirectoryItem> Items,
    int Offset,
    int TotalCount,
    bool HasMore);

public enum FileNameFilterMode { Contains, Glob, Regex }

public sealed record DirectoryNameFilter(
    string Pattern,
    FileNameFilterMode Mode = FileNameFilterMode.Contains,
    bool MatchCase = false);

public enum DirectoryComparisonMode { SizeAndModifiedTime, Content }
public enum DirectoryDifferenceKind { Identical, OnlyLeft, OnlyRight, Different, Error }
public enum DirectorySyncDirection { LeftToRight, RightToLeft }

public sealed record DirectoryComparisonSide(
    string FullPath,
    FileEntryType Type,
    long? Size,
    DateTimeOffset? ModifiedAt,
    bool IsSymbolicLink = false,
    string? LinkTarget = null);

public sealed record DirectoryComparisonEntry
{
    public required string RelativePath { get; init; }
    public DirectoryComparisonSide? Left { get; init; }
    public DirectoryComparisonSide? Right { get; init; }
    public required DirectoryDifferenceKind Difference { get; init; }
    public string? Error { get; init; }
}

public sealed record DirectoryComparisonRequest
{
    public required string LeftPath { get; init; }
    public required string RightPath { get; init; }
    public bool Recursive { get; init; } = true;
    public DirectoryComparisonMode Mode { get; init; } = DirectoryComparisonMode.SizeAndModifiedTime;
}

public sealed record DirectoryComparisonProgress(long EntriesScanned, string? CurrentPath);

public sealed record DirectoryComparisonResult(
    string LeftPath,
    string RightPath,
    IReadOnlyList<DirectoryComparisonEntry> Entries,
    TimeSpan Elapsed);

public enum FileOperationKind { CreateDirectory, Copy, Move, Rename, Trash }

public enum FileConflictPolicy { Fail, Skip, Replace, KeepBoth }
public enum FileTransferEndpointKind { Local, Sftp, Archive }

public enum FileTransferState
{
    Queued,
    Running,
    Paused,
    Completed,
    Skipped,
    Failed,
    Cancelled
}

public sealed record FileTransferRequest
{
    public required FileOperationKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public required string DestinationDirectory { get; init; }
    public FileConflictPolicy ConflictPolicy { get; init; } = FileConflictPolicy.Fail;
    public bool VerifyAfterCopy { get; init; } = true;
    public FileTransferEndpointKind SourceEndpoint { get; init; } = FileTransferEndpointKind.Local;
    public FileTransferEndpointKind DestinationEndpoint { get; init; } = FileTransferEndpointKind.Local;
    public string? SourceConnectionKey { get; init; }
    public string? DestinationConnectionKey { get; init; }
}

public sealed record SftpConnectionRequest
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string Username { get; init; }
    public required string Password { get; init; }
    public string? ExpectedHostKeySha256 { get; init; }
}

public sealed record SftpConnectionInfo(
    string ConnectionKey,
    string Host,
    int Port,
    string Username,
    string HostKeySha256);

public sealed record FileLocationEntry(
    string Name,
    string FullPath,
    FileEntryType Type,
    long? Size,
    DateTimeOffset? ModifiedAt,
    bool IsSymbolicLink = false);

[Flags]
public enum FileLocationCapabilities { None = 0, Browse = 1, Read = 2, Write = 4, Move = 8, Delete = 16 }

[Flags]
public enum ArchiveCapabilities
{
    None = 0,
    Browse = 1,
    Extract = 2,
    Create = 4,
    Update = 8,
    DeleteEntries = 16
}

public sealed record ArchiveFormatSupport(
    string Name,
    IReadOnlyList<string> Extensions,
    ArchiveCapabilities Capabilities,
    bool IsAvailable,
    string? UnavailableReason = null);

public sealed record ArchiveEntry(
    string Path,
    string Name,
    FileEntryType Type,
    long? Size,
    long? CompressedSize,
    DateTimeOffset? ModifiedAt,
    bool IsEncrypted = false,
    bool IsSymbolicLink = false);

public sealed record ArchiveSafetyLimits
{
    public int MaximumEntries { get; init; } = 50_000;
    public int MaximumDepth { get; init; } = 64;
    public long MaximumEntryBytes { get; init; } = 4L * 1024 * 1024 * 1024;
    public long MaximumExpandedBytes { get; init; } = 20L * 1024 * 1024 * 1024;
    public double MaximumCompressionRatio { get; init; } = 1_000;
}

public sealed record ArchiveRecoveryReport(
    int RecoveredTransactions,
    IReadOnlyList<string> Errors)
{
    public bool IsSuccessful => Errors.Count == 0;
}

public sealed record FileTransferOutcome(
    FileOperationRecord? Operation,
    string DestinationPath,
    bool Skipped,
    bool Verified);

public sealed record FileTransferJob
{
    public required Guid Id { get; init; }
    public required FileTransferRequest Request { get; init; }
    public required FileTransferState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public long BytesCompleted { get; init; }
    public long? TotalBytes { get; init; }
    public string? CurrentPath { get; init; }
    public string? DestinationPath { get; init; }
    public Guid? OperationId { get; init; }
    public string? Error { get; init; }
    public int Attempt { get; init; }
}

public sealed record FileOperationProgress(
    long BytesCompleted,
    long? TotalBytes,
    string CurrentPath);

public sealed record FileOperationRecord
{
    public required Guid Id { get; init; }
    public required FileOperationKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public string? ReplacedItemBackupPath { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
    public bool CanUndo { get; init; }
    public bool IsUndo { get; init; }
}
