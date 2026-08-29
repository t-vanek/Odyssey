using System.Buffers;
using System.IO.Compression;
using System.Formats.Tar;
using Odyssey.Core;
using SharpSevenZip;

namespace Odyssey.Infrastructure;

public sealed partial class SafeArchiveService : IArchiveEntryPreviewReader
{
    private const int PreviewHeaderBytes = 4 * 1024;
    public int MaximumBlockBytes => 256 * 1024;
    internal Action<long>? PreviewReadCheckpoint { get; init; }

    public async Task<ArchiveEntryPreviewBlock> ReadBlockAsync(
        string archivePath,
        string entryPath,
        long offset,
        int maximumBytes,
        ArchiveEntryPreviewVersion? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (maximumBytes is < 1 || maximumBytes > MaximumBlockBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var source = ValidatePreviewArchivePath(archivePath);
        var normalizedEntry = NormalizeEntryPath(entryPath, isDirectory: false);
        var archiveBefore = GetArchiveSnapshot(source);
        var descriptors = await ScanAsync(source, cancellationToken).ConfigureAwait(false);
        var descriptor = descriptors.FirstOrDefault(item => PathComparer.Equals(item.Path, normalizedEntry))
                         ?? throw new FileNotFoundException("Archive entry not found.", normalizedEntry);
        if (descriptor.IsDirectory) throw new InvalidDataException("A directory cannot be previewed as a file.");
        if (descriptor.IsSymbolicLink) throw new InvalidDataException("Archive links cannot be previewed.");
        if (descriptor.IsEncrypted) throw new InvalidDataException("Encrypted entries require an explicit password workflow.");
        var version = new ArchiveEntryPreviewVersion(
            archiveBefore.Length, archiveBefore.ModifiedAt, descriptor.Size, descriptor.ModifiedAt);
        if (expectedVersion is not null && expectedVersion != version)
            throw new IOException("The archive or preview entry changed after it was opened.");
        var actualOffset = Math.Min(offset, descriptor.Size);
        var headerLength = (int)Math.Min(PreviewHeaderBytes, descriptor.Size);
        var contentLength = (int)Math.Min(maximumBytes, descriptor.Size - actualOffset);

        PreviewBytes bytes;
        if (IsZip(source))
            bytes = await ReadZipPreviewAsync(source, descriptor, actualOffset, headerLength, contentLength,
                cancellationToken).ConfigureAwait(false);
        else if (IsTar(source))
            bytes = await ReadTarPreviewAsync(source, descriptor, actualOffset, headerLength, contentLength,
                cancellationToken).ConfigureAwait(false);
        else
            bytes = await ReadNativePreviewAsync(source, descriptor, actualOffset, headerLength, contentLength,
                cancellationToken).ConfigureAwait(false);

        var archiveAfter = GetArchiveSnapshot(source);
        if (archiveAfter != archiveBefore)
            throw new IOException("The archive changed while its entry was being previewed.");
        return new ArchiveEntryPreviewBlock(source, normalizedEntry, actualOffset,
            bytes.Header, bytes.Content, version);
    }

    private async Task<PreviewBytes> ReadZipPreviewAsync(
        string archivePath,
        EntryDescriptor descriptor,
        long offset,
        int headerLength,
        int contentLength,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        await using var entry = archive.Entries[descriptor.Index].Open();
        return await CaptureFromReadableStreamAsync(entry, descriptor.Size, offset, headerLength, contentLength,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreviewBytes> ReadTarPreviewAsync(
        string archivePath,
        EntryDescriptor descriptor,
        long offset,
        int headerLength,
        int contentLength,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var archive = new TarReader(input, leaveOpen: false);
        var index = 0;
        while (await archive.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (index++ != descriptor.Index) continue;
            if (!IsTarRegularFile(entry.EntryType) || entry.DataStream is null)
                throw new InvalidDataException("Special TAR entries cannot be previewed.");
            return await CaptureFromReadableStreamAsync(entry.DataStream, descriptor.Size, offset,
                headerLength, contentLength, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidDataException("The TAR entry disappeared during preview.");
    }

    private async Task<PreviewBytes> ReadNativePreviewAsync(
        string archivePath,
        EntryDescriptor descriptor,
        long offset,
        int headerLength,
        int contentLength,
        CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            using var archive = new SharpSevenZipExtractor(archivePath);
            await using var capture = new NativePreviewCaptureStream(
                descriptor.Size, offset, headerLength, contentLength, cancellationToken,
                PreviewReadCheckpoint);
            try
            {
                await archive.ExtractFileAsync(descriptor.Index, capture).ConfigureAwait(false);
            }
            catch (Exception exception) when (capture.AbortedAfterComplete
                                              && exception is not OperationCanceledException)
            {
                // The sink intentionally stops a native decoder once the complete requested prefix/block exists.
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!capture.HasRequiredBytes)
                throw new InvalidDataException("The extracted preview is shorter than archive metadata declares.");
            return new PreviewBytes(capture.Header, capture.Content);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PreviewBytes> CaptureFromReadableStreamAsync(
        Stream stream,
        long declaredLength,
        long offset,
        int headerLength,
        int contentLength,
        CancellationToken cancellationToken)
    {
        var header = new byte[headerLength];
        var content = new byte[contentLength];
        var scratch = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
            if (offset < headerLength)
            {
                var copied = Math.Min(contentLength, headerLength - (int)offset);
                header.AsSpan((int)offset, copied).CopyTo(content);
                if (copied < contentLength)
                    await ReadExactlyAsync(stream, content.AsMemory(copied), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SkipExactlyAsync(stream, offset - headerLength, scratch, cancellationToken).ConfigureAwait(false);
                await ReadExactlyAsync(stream, content, cancellationToken).ConfigureAwait(false);
            }
            if (offset + contentLength == declaredLength)
            {
                var extra = await stream.ReadAsync(scratch.AsMemory(0, 1), cancellationToken).ConfigureAwait(false);
                if (extra != 0) throw new InvalidDataException("Archive entry exceeds its declared size.");
            }
            return new PreviewBytes(header, content);
        }
        finally { ArrayPool<byte>.Shared.Return(scratch); }
    }

    private async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var current = await stream.ReadAsync(destination[read..], cancellationToken).ConfigureAwait(false);
            if (current == 0) throw new InvalidDataException("Archive entry is shorter than its declared size.");
            read += current;
            PreviewReadCheckpoint?.Invoke(current);
        }
    }

    private async Task SkipExactlyAsync(
        Stream stream,
        long count,
        byte[] scratch,
        CancellationToken cancellationToken)
    {
        var remaining = count;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(scratch.Length, remaining);
            var read = await stream.ReadAsync(scratch.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new InvalidDataException("Archive entry is shorter than its declared size.");
            remaining -= read;
            PreviewReadCheckpoint?.Invoke(read);
        }
    }

    private string ValidatePreviewArchivePath(string archivePath)
    {
        var path = ValidateArchivePath(archivePath);
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
        {
            parent.Refresh();
            if (parent.LinkTarget is not null || parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("An archive ancestor is a symbolic link or reparse point.");
        }
        return path;
    }

    private static ArchiveFileSnapshot GetArchiveSnapshot(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists) throw new FileNotFoundException("Archive not found.", path);
        return new ArchiveFileSnapshot(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private sealed record PreviewBytes(byte[] Header, byte[] Content);
    private sealed record ArchiveFileSnapshot(long Length, DateTimeOffset ModifiedAt);

    private sealed class NativePreviewCaptureStream : Stream
    {
        private readonly long _declaredLength;
        private readonly long _offset;
        private readonly long _stopAfter;
        private readonly CancellationToken _cancellationToken;
        private readonly Action<long>? _checkpoint;
        private long _position;

        public NativePreviewCaptureStream(
            long declaredLength,
            long offset,
            int headerLength,
            int contentLength,
            CancellationToken cancellationToken,
            Action<long>? checkpoint)
        {
            _declaredLength = declaredLength;
            _offset = offset;
            _stopAfter = Math.Max(headerLength, offset + contentLength);
            _cancellationToken = cancellationToken;
            _checkpoint = checkpoint;
            Header = new byte[headerLength];
            Content = new byte[contentLength];
        }

        public byte[] Header { get; }
        public byte[] Content { get; }
        public bool AbortedAfterComplete { get; private set; }
        public bool HasRequiredBytes => _position >= _stopAfter;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            CopyOverlap(buffer, _position, 0, Header);
            CopyOverlap(buffer, _position, _offset, Content);
            _position = checked(_position + buffer.Length);
            _checkpoint?.Invoke(buffer.Length);
            _cancellationToken.ThrowIfCancellationRequested();
            if (_position > _declaredLength)
                throw new InvalidDataException("Archive entry exceeds its declared size.");
            if (_stopAfter < _declaredLength && _position >= _stopAfter)
            {
                AbortedAfterComplete = true;
                throw new PreviewCompleteException();
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private static void CopyOverlap(ReadOnlySpan<byte> source, long sourceStart, long targetStart, byte[] target)
        {
            var start = Math.Max(sourceStart, targetStart);
            var end = Math.Min(sourceStart + source.Length, targetStart + target.Length);
            if (end <= start) return;
            source.Slice((int)(start - sourceStart), (int)(end - start))
                .CopyTo(target.AsSpan((int)(start - targetStart)));
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class PreviewCompleteException : IOException { }
}
