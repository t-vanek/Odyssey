using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed partial class SftpConnectionService
{
    private const int PreviewHeaderBytes = 4 * 1024;
    public int MaximumBlockBytes => 256 * 1024;

    public async Task<RemoteFilePreviewBlock> ReadBlockAsync(
        string connectionKey,
        string path,
        long offset,
        int maximumBytes,
        RemoteFilePreviewVersion? expectedVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionKey))
            throw new ArgumentException("An SFTP connection key is required.", nameof(connectionKey));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (maximumBytes is < 1 || maximumBytes > MaximumBlockBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var remotePath = NormalizePreviewRemotePath(path);
        var session = GetSession(connectionKey);
        await session.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await GetPreviewSnapshotAsync(session, remotePath, cancellationToken)
                .ConfigureAwait(false);
            if (expectedVersion is not null && expectedVersion != before)
                throw new IOException("The remote preview source changed after it was opened.");
            var actualOffset = Math.Min(offset, before.Length);
            var headerLength = (int)Math.Min(PreviewHeaderBytes, before.Length);
            var contentLength = (int)Math.Min(maximumBytes, before.Length - actualOffset);
            var header = new byte[headerLength];
            var content = new byte[contentLength];

            await using (var stream = await session.Client.OpenAsync(
                             remotePath, FileMode.Open, FileAccess.Read, cancellationToken).ConfigureAwait(false))
            {
                if (!stream.CanRead || !stream.CanSeek)
                    throw new IOException("The SFTP server did not provide a seekable read stream.");
                if (stream.Length != before.Length)
                    throw new IOException("The remote preview source changed while it was being opened.");
                await ReadPreviewExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
                stream.Position = actualOffset;
                await ReadPreviewExactlyAsync(stream, content, cancellationToken).ConfigureAwait(false);
                if (actualOffset + contentLength == before.Length)
                {
                    var extra = new byte[1];
                    if (await stream.ReadAsync(extra, cancellationToken).ConfigureAwait(false) != 0)
                        throw new IOException("The remote preview source exceeds its declared size.");
                }
            }

            var after = await GetPreviewSnapshotAsync(session, remotePath, cancellationToken)
                .ConfigureAwait(false);
            if (after != before)
                throw new IOException("The remote preview source changed while it was being read.");
            return new RemoteFilePreviewBlock(
                connectionKey, remotePath, actualOffset, header, content, before);
        }
        finally { session.Gate.Release(); }
    }

    private static async Task<RemoteFilePreviewVersion> GetPreviewSnapshotAsync(
        Session session,
        string path,
        CancellationToken cancellationToken)
    {
        var item = await session.Client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (item.IsDirectory) throw new InvalidDataException("An SFTP directory cannot be previewed as a file.");
        if (item.IsSymbolicLink) throw new IOException("SFTP links cannot be previewed.");
        return new RemoteFilePreviewVersion(
            checked((long)item.Length),
            new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero));
    }

    private static async Task ReadPreviewExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var completed = 0;
        while (completed < destination.Length)
        {
            var read = await stream.ReadAsync(destination[completed..], cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new IOException("The remote preview source is shorter than its declared size.");
            completed += read;
        }
    }

    private static string NormalizePreviewRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal)
            || path.Contains('\0') || path.Contains('\\'))
            throw new ArgumentException("An absolute canonical SFTP path is required.", nameof(path));
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("An absolute canonical SFTP file path is required.", nameof(path));
        return "/" + string.Join('/', segments);
    }
}
