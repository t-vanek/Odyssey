using System.Buffers;
using System.Globalization;
using System.Text;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class QuickViewService : IQuickViewService
{
    private const int HeaderBytes = 4 * 1024;
    private const int HexBytesPerLine = 16;
    public const int DefaultMaximumChunkBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly byte[] Utf16LeBom = [0xFF, 0xFE];
    private static readonly byte[] Utf16BeBom = [0xFE, 0xFF];
    private static readonly IReadOnlyList<string> Encodings =
        ["auto", "utf-8", "utf-16", "utf-16BE", "iso-8859-1", "us-ascii"];
    private readonly IArchiveEntryPreviewReader? _archiveReader;

    public QuickViewService() : this(null, DefaultMaximumChunkBytes) { }
    public QuickViewService(int maximumChunkBytes) : this(null, maximumChunkBytes) { }
    public QuickViewService(IArchiveEntryPreviewReader archiveReader)
        : this(archiveReader, Math.Min(DefaultMaximumChunkBytes, archiveReader.MaximumBlockBytes)) { }

    private QuickViewService(IArchiveEntryPreviewReader? archiveReader, int maximumChunkBytes)
    {
        if (maximumChunkBytes is < 1024 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumChunkBytes));
        MaximumChunkBytes = maximumChunkBytes;
        _archiveReader = archiveReader;
    }

    public int MaximumChunkBytes { get; }
    public IReadOnlyList<string> SupportedEncodings => Encodings;
    internal Func<Stream, Memory<byte>, CancellationToken, ValueTask<int>> ReadOperation { get; init; } =
        static (stream, buffer, token) => stream.ReadAsync(buffer, token);

    public async Task<QuickViewChunk> ReadAsync(
        QuickViewReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Offset < 0) throw new ArgumentOutOfRangeException(nameof(request.Offset));
        if (request.MaximumBytes is < 1 || request.MaximumBytes > MaximumChunkBytes)
            throw new ArgumentOutOfRangeException(nameof(request.MaximumBytes));
        return request.Endpoint switch
        {
            FileTransferEndpointKind.Local => await ReadLocalAsync(request, cancellationToken).ConfigureAwait(false),
            FileTransferEndpointKind.Archive => await ReadArchiveAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Quick View does not support {request.Endpoint} sources yet.")
        };
    }

    private async Task<QuickViewChunk> ReadLocalAsync(
        QuickViewReadRequest request,
        CancellationToken cancellationToken)
    {
        var path = ValidateLocalFile(request.Path);
        var before = Snapshot(path);
        if (request.ExpectedVersion is not null && request.ExpectedVersion != before)
            throw new IOException("The preview source changed after it was opened.");
        var requestedOffset = Math.Min(request.Offset, before.Length);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);

        var headerLength = (int)Math.Min(HeaderBytes, before.Length);
        var header = ArrayPool<byte>.Shared.Rent(Math.Max(1, headerLength));
        var content = ArrayPool<byte>.Shared.Rent(request.MaximumBytes);
        try
        {
            stream.Position = 0;
            var headerRead = await ReadAtMostAsync(stream, header.AsMemory(0, headerLength), cancellationToken)
                .ConfigureAwait(false);
            var detection = DetectEncoding(header.AsSpan(0, headerRead), request.EncodingName);
            var effectiveMode = request.Mode == QuickViewDisplayMode.Auto
                ? detection.IsBinary ? QuickViewDisplayMode.Hex : QuickViewDisplayMode.Text
                : request.Mode;
            var offset = AlignOffset(requestedOffset, effectiveMode, detection.Encoding);
            stream.Position = offset;
            var wanted = (int)Math.Min(request.MaximumBytes, before.Length - offset);
            var read = await ReadAtMostAsync(stream, content.AsMemory(0, wanted), cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var consumed = effectiveMode == QuickViewDisplayMode.Text
                ? CompleteTextPrefixLength(content.AsSpan(0, read), detection.Encoding)
                : read;
            if (consumed == 0 && read > 0) consumed = read;
            var display = effectiveMode == QuickViewDisplayMode.Hex
                ? FormatHex(content.AsSpan(0, consumed), offset)
                : DecodeText(content.AsSpan(0, consumed), detection.Encoding,
                    offset == 0 ? detection.BomLength : 0);

            var after = Snapshot(path);
            if (after != before) throw new IOException("The preview source changed while it was being read.");
            return new QuickViewChunk
            {
                Path = path,
                Version = before,
                Offset = offset,
                BytesRead = consumed,
                NextOffset = offset + consumed,
                Content = display,
                EffectiveMode = effectiveMode,
                EncodingName = detection.Name,
                IsBinary = detection.IsBinary
            };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
            ArrayPool<byte>.Shared.Return(content);
        }
    }

    private async Task<QuickViewChunk> ReadArchiveAsync(
        QuickViewReadRequest request,
        CancellationToken cancellationToken)
    {
        if (_archiveReader is null) throw new NotSupportedException("Archive preview is unavailable.");
        if (string.IsNullOrWhiteSpace(request.ContainerPath) || string.IsNullOrWhiteSpace(request.EntryPath))
            throw new ArgumentException("Archive Quick View requires a container and entry path.");
        var expected = request.ExpectedVersion is null
            ? null
            : request.ExpectedVersion.ContainerLength is { } containerLength
              && request.ExpectedVersion.ContainerModifiedAt is { } containerModified
                ? new ArchiveEntryPreviewVersion(containerLength, containerModified,
                    request.ExpectedVersion.Length, request.ExpectedVersion.EntryModifiedAt)
                : throw new ArgumentException("The archive preview version is incomplete.");
        var block = await _archiveReader.ReadBlockAsync(
            request.ContainerPath, request.EntryPath, request.Offset, request.MaximumBytes,
            expected, cancellationToken).ConfigureAwait(false);
        var detection = DetectEncoding(block.Header, request.EncodingName);
        var effectiveMode = request.Mode == QuickViewDisplayMode.Auto
            ? detection.IsBinary ? QuickViewDisplayMode.Hex : QuickViewDisplayMode.Text
            : request.Mode;
        var alignedOffset = AlignOffset(block.Offset, effectiveMode, detection.Encoding);
        if (alignedOffset != block.Offset)
        {
            block = await _archiveReader.ReadBlockAsync(
                request.ContainerPath, request.EntryPath, alignedOffset, request.MaximumBytes,
                block.Version, cancellationToken).ConfigureAwait(false);
        }
        var consumed = effectiveMode == QuickViewDisplayMode.Text
            ? CompleteTextPrefixLength(block.Content, detection.Encoding)
            : block.Content.Length;
        if (consumed == 0 && block.Content.Length > 0) consumed = block.Content.Length;
        var display = effectiveMode == QuickViewDisplayMode.Hex
            ? FormatHex(block.Content.AsSpan(0, consumed), block.Offset)
            : DecodeText(block.Content.AsSpan(0, consumed), detection.Encoding,
                block.Offset == 0 ? detection.BomLength : 0);
        var version = new QuickViewVersion(
            block.Version.EntryLength,
            block.Version.EntryModifiedAt ?? block.Version.ArchiveModifiedAt)
        {
            ContainerLength = block.Version.ArchiveLength,
            ContainerModifiedAt = block.Version.ArchiveModifiedAt,
            EntryModifiedAt = block.Version.EntryModifiedAt
        };
        return new QuickViewChunk
        {
            Path = request.Path,
            Version = version,
            Offset = block.Offset,
            BytesRead = consumed,
            NextOffset = block.Offset + consumed,
            Content = display,
            EffectiveMode = effectiveMode,
            EncodingName = detection.Name,
            IsBinary = detection.IsBinary
        };
    }

    private async Task<int> ReadAtMostAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await ReadOperation(stream, buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static string ValidateLocalFile(string candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) throw new ArgumentException("A preview path is required.");
        var path = Path.GetFullPath(candidate);
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists) throw new FileNotFoundException("The preview source is unavailable.", path);
        if (file.LinkTarget is not null || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Links and reparse points cannot be previewed.");
        for (var parent = file.Directory; parent is not null; parent = parent.Parent)
        {
            parent.Refresh();
            if (parent.LinkTarget is not null || parent.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("A preview source ancestor is a link or reparse point.");
        }
        return path;
    }

    private static QuickViewVersion Snapshot(string path)
    {
        var file = new FileInfo(path);
        file.Refresh();
        if (!file.Exists) throw new FileNotFoundException("The preview source is unavailable.", path);
        return new QuickViewVersion(file.Length, new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static EncodingDetection DetectEncoding(ReadOnlySpan<byte> sample, string requested)
    {
        var normalized = string.IsNullOrWhiteSpace(requested) ? "auto" : requested.Trim();
        if (!normalized.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            var selected = ResolveEncoding(normalized);
            return new EncodingDetection(selected, CanonicalName(selected), BomLength(sample, selected), false);
        }
        if (sample.StartsWith(Utf8Bom))
            return new EncodingDetection(Encoding.UTF8, "utf-8", 3, false);
        if (sample.StartsWith(Utf16LeBom))
            return new EncodingDetection(Encoding.Unicode, "utf-16", 2, false);
        if (sample.StartsWith(Utf16BeBom))
            return new EncodingDetection(Encoding.BigEndianUnicode, "utf-16BE", 2, false);

        if (LooksLikeUtf16(sample, evenZeros: false))
            return new EncodingDetection(Encoding.Unicode, "utf-16", 0, false);
        if (LooksLikeUtf16(sample, evenZeros: true))
            return new EncodingDetection(Encoding.BigEndianUnicode, "utf-16BE", 0, false);
        var binary = LooksBinary(sample);
        if (!binary)
        {
            try
            {
                _ = StrictUtf8.GetString(sample);
                return new EncodingDetection(Encoding.UTF8, "utf-8", 0, false);
            }
            catch (DecoderFallbackException) { }
        }
        return new EncodingDetection(Encoding.Latin1, "iso-8859-1", 0, binary);
    }

    private static Encoding ResolveEncoding(string name) => name.ToLowerInvariant() switch
    {
        "utf-8" => Encoding.UTF8,
        "utf-16" => Encoding.Unicode,
        "utf-16be" => Encoding.BigEndianUnicode,
        "iso-8859-1" => Encoding.Latin1,
        "us-ascii" => Encoding.ASCII,
        _ => throw new ArgumentException($"Unsupported preview encoding: {name}")
    };

    private static string CanonicalName(Encoding encoding) => encoding.CodePage switch
    {
        65001 => "utf-8",
        1200 => "utf-16",
        1201 => "utf-16BE",
        28591 => "iso-8859-1",
        20127 => "us-ascii",
        _ => encoding.WebName
    };

    private static int BomLength(ReadOnlySpan<byte> sample, Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        return preamble.Length > 0 && sample.StartsWith(preamble) ? preamble.Length : 0;
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> sample, bool evenZeros)
    {
        if (sample.Length < 4) return false;
        var pairs = sample.Length / 2;
        var zeros = 0;
        for (var index = evenZeros ? 0 : 1; index < pairs * 2; index += 2)
            if (sample[index] == 0) zeros++;
        return zeros >= Math.Max(2, pairs * 3 / 5);
    }

    private static bool LooksBinary(ReadOnlySpan<byte> sample)
    {
        if (sample.Length == 0) return false;
        var controls = 0;
        foreach (var value in sample)
        {
            if (value == 0) return true;
            if (value < 0x20 && value is not (0x09 or 0x0A or 0x0D)) controls++;
        }
        return controls > sample.Length / 20;
    }

    private static long AlignOffset(long offset, QuickViewDisplayMode mode, Encoding encoding)
    {
        if (mode != QuickViewDisplayMode.Text) return offset;
        return encoding.CodePage is 1200 or 1201 ? offset & ~1L : offset;
    }

    private static int CompleteTextPrefixLength(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        if (bytes.Length == 0) return 0;
        if (encoding.CodePage is 1200 or 1201) return bytes.Length & ~1;
        if (encoding.CodePage != 65001) return bytes.Length;
        var continuation = 0;
        for (var index = bytes.Length - 1; index >= 0 && continuation < 3 && (bytes[index] & 0xC0) == 0x80;
             index--) continuation++;
        var leadIndex = bytes.Length - continuation - 1;
        if (leadIndex < 0) return bytes.Length;
        var lead = bytes[leadIndex];
        var expected = lead switch
        {
            < 0x80 => 1,
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 1
        };
        return bytes.Length - leadIndex < expected ? leadIndex : bytes.Length;
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes, Encoding encoding, int bomLength)
    {
        if (bomLength > bytes.Length) bomLength = 0;
        return encoding.GetString(bytes[bomLength..]);
    }

    private static string FormatHex(ReadOnlySpan<byte> bytes, long offset)
    {
        var builder = new StringBuilder(bytes.Length * 4);
        for (var lineStart = 0; lineStart < bytes.Length; lineStart += HexBytesPerLine)
        {
            var count = Math.Min(HexBytesPerLine, bytes.Length - lineStart);
            builder.Append((offset + lineStart).ToString("X16", CultureInfo.InvariantCulture)).Append("  ");
            for (var index = 0; index < HexBytesPerLine; index++)
            {
                if (index < count) builder.Append(bytes[lineStart + index].ToString("X2", CultureInfo.InvariantCulture));
                else builder.Append("  ");
                builder.Append(index == 7 ? "  " : " ");
            }
            builder.Append(" |");
            for (var index = 0; index < count; index++)
            {
                var value = bytes[lineStart + index];
                builder.Append(value is >= 0x20 and <= 0x7E ? (char)value : '.');
            }
            builder.Append('|');
            if (lineStart + count < bytes.Length) builder.AppendLine();
        }
        return builder.ToString();
    }

    private sealed record EncodingDetection(Encoding Encoding, string Name, int BomLength, bool IsBinary);
}
