namespace Odyssey.Core;

public enum QuickViewDisplayMode { Auto, Text, Hex }

public sealed record QuickViewVersion(long Length, DateTimeOffset ModifiedAt)
{
    public long? ContainerLength { get; init; }
    public DateTimeOffset? ContainerModifiedAt { get; init; }
    public DateTimeOffset? EntryModifiedAt { get; init; }
}

public sealed record QuickViewReadRequest
{
    public required string Path { get; init; }
    public FileTransferEndpointKind Endpoint { get; init; } = FileTransferEndpointKind.Local;
    public string? ContainerPath { get; init; }
    public string? EntryPath { get; init; }
    public long Offset { get; init; }
    public int MaximumBytes { get; init; } = 64 * 1024;
    public QuickViewDisplayMode Mode { get; init; } = QuickViewDisplayMode.Auto;
    public string EncodingName { get; init; } = "auto";
    public QuickViewVersion? ExpectedVersion { get; init; }
}

public sealed record ArchiveEntryPreviewVersion(
    long ArchiveLength,
    DateTimeOffset ArchiveModifiedAt,
    long EntryLength,
    DateTimeOffset? EntryModifiedAt);

public sealed record ArchiveEntryPreviewBlock(
    string ArchivePath,
    string EntryPath,
    long Offset,
    byte[] Header,
    byte[] Content,
    ArchiveEntryPreviewVersion Version);

public interface IArchiveEntryPreviewReader
{
    int MaximumBlockBytes { get; }
    Task<ArchiveEntryPreviewBlock> ReadBlockAsync(
        string archivePath,
        string entryPath,
        long offset,
        int maximumBytes,
        ArchiveEntryPreviewVersion? expectedVersion = null,
        CancellationToken cancellationToken = default);
}

public sealed record QuickViewChunk
{
    public required string Path { get; init; }
    public required QuickViewVersion Version { get; init; }
    public required long Offset { get; init; }
    public required int BytesRead { get; init; }
    public required long NextOffset { get; init; }
    public required string Content { get; init; }
    public required QuickViewDisplayMode EffectiveMode { get; init; }
    public required string EncodingName { get; init; }
    public required bool IsBinary { get; init; }
    public bool HasPrevious => Offset > 0;
    public bool HasNext => NextOffset < Version.Length;
}

public interface IQuickViewService
{
    int MaximumChunkBytes { get; }
    IReadOnlyList<string> SupportedEncodings { get; }
    Task<QuickViewChunk> ReadAsync(
        QuickViewReadRequest request,
        CancellationToken cancellationToken = default);
}
