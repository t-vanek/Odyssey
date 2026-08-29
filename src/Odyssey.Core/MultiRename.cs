namespace Odyssey.Core;

public enum MultiRenameCaseMode { Unchanged, Lower, Upper, Title }
public enum MultiRenameSortMode { Name, Extension, Created, Modified, Size }
public enum FileSystemCaseMode { Auto, CaseSensitive, CaseInsensitive }

public sealed record MultiRenameRequest
{
    public required IReadOnlyList<string> SourcePaths { get; init; }
    public string Prefix { get; init; } = string.Empty;
    public string Suffix { get; init; } = string.Empty;
    public string SearchText { get; init; } = string.Empty;
    public string ReplacementText { get; init; } = string.Empty;
    public bool UseRegex { get; init; }
    public bool MatchCase { get; init; }
    public MultiRenameCaseMode CaseMode { get; init; }
    public bool IncludeCounter { get; init; }
    public int CounterStart { get; init; } = 1;
    public int CounterStep { get; init; } = 1;
    public int CounterPadding { get; init; } = 3;
    public string CounterSeparator { get; init; } = "_";
    public string DateFormat { get; init; } = "yyyyMMdd";
    public bool PreserveExtension { get; init; } = true;
    public string ExtensionReplacement { get; init; } = string.Empty;
    public MultiRenameSortMode SortMode { get; init; }
    public bool SortDescending { get; init; }
    public FileSystemCaseMode CaseSensitivity { get; init; }
    public IReadOnlyDictionary<string, string> ManualNames { get; init; } =
        new Dictionary<string, string>();
}

public sealed record MultiRenamePlanItem
{
    public required string SourcePath { get; init; }
    public required string DestinationPath { get; init; }
    public required string OriginalName { get; init; }
    public required string ProposedName { get; init; }
    public required FileEntryType Type { get; init; }
    public required long Length { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ModifiedAt { get; init; }
    public required bool IsManual { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool HasChange => !string.Equals(SourcePath, DestinationPath, StringComparison.Ordinal);
    public bool IsValid => Errors.Count == 0;
}

public sealed record MultiRenamePlan
{
    public const int CurrentSchemaVersion = 1;
    public required int SchemaVersion { get; init; }
    public required Guid Id { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required MultiRenameRequest Request { get; init; }
    public required bool IsCaseSensitive { get; init; }
    public required IReadOnlyList<MultiRenamePlanItem> Items { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public required string PlanHashSha256 { get; init; }
    public bool IsValid => Errors.Count == 0 && Items.Count > 0 && Items.All(item => item.IsValid)
                           && Items.Any(item => item.HasChange);
}

public sealed record MultiRenameBatchResult(
    Guid BatchId,
    int RenamedItems,
    DateTimeOffset CompletedAt,
    bool CanUndo);

public interface IMultiRenameService
{
    FileAccessMode AccessMode { get; set; }
    bool CanUndoLastBatch { get; }
    MultiRenamePlan CreatePlan(MultiRenameRequest request);
    string ExportPlan(MultiRenamePlan plan);
    MultiRenamePlan ImportPlan(string json);
    Task<MultiRenameBatchResult> ExecuteAsync(
        MultiRenamePlan plan,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<MultiRenameBatchResult?> UndoLastBatchAsync(
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
