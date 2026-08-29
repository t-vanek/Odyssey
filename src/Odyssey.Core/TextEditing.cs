namespace Odyssey.Core;

public sealed record TextDocumentVersion(
    long Length,
    DateTimeOffset ModifiedAt,
    string Sha256);

public sealed record TextEditorDocument
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Content { get; init; }
    public required string LanguageId { get; init; }
    public required string EncodingName { get; init; }
    public required bool HasByteOrderMark { get; init; }
    public required TextDocumentVersion Version { get; init; }
}

public sealed record TextEditorSaveRequest
{
    public required string Path { get; init; }
    public required string Content { get; init; }
    public required string EncodingName { get; init; }
    public required bool HasByteOrderMark { get; init; }
    public required TextDocumentVersion ExpectedVersion { get; init; }
}

public interface ITextEditorService
{
    int MaximumFileBytes { get; }
    FileAccessMode AccessMode { get; set; }

    Task<TextEditorDocument> OpenAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<TextDocumentVersion> SaveAsync(
        TextEditorSaveRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class TextEditorUnsupportedException(string message) : IOException(message);
public sealed class TextDocumentChangedException(string message) : IOException(message);
