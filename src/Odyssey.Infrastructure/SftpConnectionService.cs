using System.Security.Cryptography;
using System.Text;
using Odyssey.Core;
using Renci.SshNet;

namespace Odyssey.Infrastructure;

public sealed partial class SftpConnectionService : ISftpConnectionService, IRemoteFilePreviewReader
{
    private const int BufferSize = 1024 * 1024;
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public IReadOnlyList<SftpConnectionInfo> Connections
    {
        get { lock (_sync) return _sessions.Values.Select(item => item.Info).ToArray(); }
    }

    public async Task<SftpConnectionInfo> ConnectAsync(
        SftpConnectionRequest request, CancellationToken cancellationToken = default)
    {
        ValidateConnectionRequest(request);
        var expectedFingerprint = NormalizeFingerprint(request.ExpectedHostKeySha256);
        string? presentedFingerprint = null;
        var authentication = new PasswordAuthenticationMethod(request.Username, request.Password);
        var client = new SftpClient(new ConnectionInfo(request.Host.Trim(), request.Port, request.Username.Trim(), authentication));
        client.HostKeyReceived += (_, eventArgs) =>
        {
            presentedFingerprint = "SHA256:" + eventArgs.FingerPrintSHA256;
            eventArgs.CanTrust = expectedFingerprint is not null
                && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(expectedFingerprint),
                    Encoding.UTF8.GetBytes(presentedFingerprint));
        };

        try { await client.ConnectAsync(cancellationToken); }
        catch
        {
            client.Dispose();
            if (presentedFingerprint is not null && expectedFingerprint is null)
                throw new IOException($"Host key approval is required. Presented fingerprint: {presentedFingerprint}");
            if (presentedFingerprint is not null && expectedFingerprint is not null
                && !string.Equals(expectedFingerprint, presentedFingerprint, StringComparison.Ordinal))
                throw new IOException($"SFTP host key changed. Expected {expectedFingerprint}, presented {presentedFingerprint}.");
            throw;
        }

        if (presentedFingerprint is null)
        {
            client.Dispose();
            throw new IOException("The SFTP server did not present a host key.");
        }
        var key = CreateConnectionKey(request.Host, request.Port, request.Username, presentedFingerprint);
        var info = new SftpConnectionInfo(key, request.Host.Trim(), request.Port, request.Username.Trim(), presentedFingerprint);
        var session = new Session(info, client);
        Session? previous = null;
        lock (_sync)
        {
            if (_sessions.Remove(key, out previous)) { }
            _sessions[key] = session;
        }
        if (previous is not null) await previous.DisposeAsync();
        return info;
    }

    public async Task DisconnectAsync(string connectionKey, CancellationToken cancellationToken = default)
    {
        Session? session;
        lock (_sync) _sessions.Remove(connectionKey, out session);
        if (session is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        await session.DisposeAsync();
    }

    public async Task<IReadOnlyList<FileLocationEntry>> ListAsync(
        string connectionKey, string path, CancellationToken cancellationToken = default)
    {
        var session = GetSession(connectionKey);
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            var normalized = NormalizeRemotePath(path);
            var result = new List<FileLocationEntry>();
            await foreach (var item in session.Client.ListDirectoryAsync(normalized, cancellationToken))
            {
                if (item.Name is "." or ".." || TransferArtifactNames.IsInternal(item.Name)) continue;
                result.Add(new FileLocationEntry(
                    item.Name,
                    item.FullName,
                    item.IsDirectory ? FileEntryType.Directory : FileEntryType.File,
                    item.IsDirectory ? null : checked((long)item.Length),
                    new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero),
                    item.IsSymbolicLink));
            }
            return result.OrderByDescending(item => item.Type).ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        }
        finally { session.Gate.Release(); }
    }

    public async Task<FileTransferOutcome> TransferAsync(
        FileTransferRequest request,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request.Kind != FileOperationKind.Copy)
            throw new NotSupportedException("Remote transfers currently support copy operations only.");
        var upload = request.SourceEndpoint == FileTransferEndpointKind.Local
                     && request.DestinationEndpoint == FileTransferEndpointKind.Sftp;
        var download = request.SourceEndpoint == FileTransferEndpointKind.Sftp
                       && request.DestinationEndpoint == FileTransferEndpointKind.Local;
        if (!upload && !download)
            throw new NotSupportedException("A remote transfer must copy between a local path and one SFTP connection.");
        var connectionKey = upload ? request.DestinationConnectionKey : request.SourceConnectionKey;
        var session = GetSession(connectionKey ?? throw new InvalidOperationException("The SFTP connection is missing."));
        await session.Gate.WaitAsync(cancellationToken);
        try
        {
            return upload
                ? await UploadAsync(session.Client, request, progress, cancellationToken)
                : await DownloadAsync(session.Client, request, progress, cancellationToken);
        }
        finally { session.Gate.Release(); }
    }

    private static async Task<FileTransferOutcome> UploadAsync(
        SftpClient client, FileTransferRequest request, IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(request.SourcePath);
        if (!File.Exists(source) && !Directory.Exists(source)) throw new FileNotFoundException("Local source is unavailable.", source);
        var destinationDirectory = NormalizeRemotePath(request.DestinationDirectory);
        var destination = CombineRemote(destinationDirectory, Path.GetFileName(source));
        var resolution = await ResolveRemoteConflictAsync(client, destination, request.ConflictPolicy, cancellationToken);
        if (resolution.Skipped) return new FileTransferOutcome(null, destination, true, false);
        destination = resolution.Destination;
        var temporary = destination + $".odyssey-part-{Guid.NewGuid():N}";
        try
        {
            var total = GetLocalSize(source);
            long completed = 0;
            if (File.Exists(source))
                await UploadFileAsync(client, source, temporary, request.VerifyAfterCopy, value =>
                {
                    completed += value;
                    progress?.Report(new FileOperationProgress(completed, total, source));
                }, cancellationToken);
            else
                await UploadDirectoryAsync(client, source, temporary, request.VerifyAfterCopy, value =>
                {
                    completed += value;
                    progress?.Report(new FileOperationProgress(completed, total, source));
                }, cancellationToken);
            await client.RenameFileAsync(temporary, destination, cancellationToken);
            if (resolution.Backup is not null) await DeleteRemoteTreeAsync(client, resolution.Backup, cancellationToken);
            return new FileTransferOutcome(null, destination, false, request.VerifyAfterCopy);
        }
        catch
        {
            await TryDeleteRemoteAsync(client, temporary, CancellationToken.None);
            await RestoreRemoteBackupAsync(client, resolution.Backup, destination);
            throw;
        }
    }

    private static async Task<FileTransferOutcome> DownloadAsync(
        SftpClient client, FileTransferRequest request, IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var source = NormalizeRemotePath(request.SourcePath);
        var sourceInfo = await client.GetAsync(source, cancellationToken);
        var destinationDirectory = Path.GetFullPath(request.DestinationDirectory);
        if (!Directory.Exists(destinationDirectory)) throw new DirectoryNotFoundException(destinationDirectory);
        var destination = Path.Combine(destinationDirectory, RemoteName(source));
        var resolution = ResolveLocalConflict(destination, request.ConflictPolicy);
        if (resolution.Skipped) return new FileTransferOutcome(null, destination, true, false);
        destination = resolution.Destination;
        var temporary = destination + $".odyssey-part-{Guid.NewGuid():N}";
        try
        {
            var total = sourceInfo.IsDirectory ? await GetRemoteSizeAsync(client, source, cancellationToken) : checked((long)sourceInfo.Length);
            long completed = 0;
            if (sourceInfo.IsDirectory)
                await DownloadDirectoryAsync(client, source, temporary, request.VerifyAfterCopy, value =>
                {
                    completed += value;
                    progress?.Report(new FileOperationProgress(completed, total, source));
                }, cancellationToken);
            else
                await DownloadFileAsync(client, source, temporary, request.VerifyAfterCopy, value =>
                {
                    completed += value;
                    progress?.Report(new FileOperationProgress(completed, total, source));
                }, cancellationToken);
            MoveLocalEntry(temporary, destination);
            if (resolution.Backup is not null) DeleteLocalEntry(resolution.Backup);
            return new FileTransferOutcome(null, destination, false, request.VerifyAfterCopy);
        }
        catch
        {
            TryDeleteLocal(temporary);
            if (resolution.Backup is not null && ExistsLocal(resolution.Backup) && !ExistsLocal(destination))
                MoveLocalEntry(resolution.Backup, destination);
            throw;
        }
    }

    private static async Task UploadDirectoryAsync(SftpClient client, string source, string destination,
        bool verify, Action<int> report, CancellationToken cancellationToken)
    {
        await client.CreateDirectoryAsync(destination, cancellationToken);
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childDestination = CombineRemote(destination, entry.Name);
            if (entry is DirectoryInfo directory && directory.LinkTarget is null)
                await UploadDirectoryAsync(client, directory.FullName, childDestination, verify, report, cancellationToken);
            else if (entry is FileInfo file && file.LinkTarget is null)
                await UploadFileAsync(client, file.FullName, childDestination, verify, report, cancellationToken);
            else
                throw new IOException("SFTP transfer does not follow symbolic links.");
        }
    }

    private static async Task UploadFileAsync(SftpClient client, string source, string destination,
        bool verify, Action<int> report, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var counting = new ProgressReadStream(input, report);
        await client.UploadFileAsync(counting, destination, canOverride: false, uploadProgress: null, cancellationToken);
        if (verify && !await LocalAndRemoteEqualAsync(client, source, destination, cancellationToken))
            throw new IOException("Uploaded data failed SHA-256 verification.");
    }

    private static async Task DownloadDirectoryAsync(SftpClient client, string source, string destination,
        bool verify, Action<int> report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        await foreach (var entry in client.ListDirectoryAsync(source, cancellationToken))
        {
            if (entry.Name is "." or "..") continue;
            var childDestination = Path.Combine(destination, entry.Name);
            if (entry.IsDirectory && !entry.IsSymbolicLink)
                await DownloadDirectoryAsync(client, entry.FullName, childDestination, verify, report, cancellationToken);
            else if (!entry.IsSymbolicLink)
                await DownloadFileAsync(client, entry.FullName, childDestination, verify, report, cancellationToken);
            else
                throw new IOException("SFTP transfer does not follow symbolic links.");
        }
    }

    private static async Task DownloadFileAsync(SftpClient client, string source, string destination,
        bool verify, Action<int> report, CancellationToken cancellationToken)
    {
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var counting = new ProgressWriteStream(output, report))
        {
            await client.DownloadFileAsync(source, counting, cancellationToken);
            await counting.FlushAsync(cancellationToken);
        }
        if (verify && !await LocalAndRemoteEqualAsync(client, destination, source, cancellationToken))
            throw new IOException("Downloaded data failed SHA-256 verification.");
    }

    private static async Task<bool> LocalAndRemoteEqualAsync(
        SftpClient client, string local, string remote, CancellationToken cancellationToken)
    {
        await using var localStream = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var remoteStream = client.OpenRead(remote);
        var localHash = await SHA256.HashDataAsync(localStream, cancellationToken);
        var remoteHash = await SHA256.HashDataAsync(remoteStream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(localHash, remoteHash);
    }

    private static async Task<RemoteConflictResolution> ResolveRemoteConflictAsync(
        SftpClient client, string destination, FileConflictPolicy policy, CancellationToken cancellationToken)
    {
        if (!client.Exists(destination)) return new RemoteConflictResolution(destination, null, false);
        if (policy == FileConflictPolicy.Skip) return new RemoteConflictResolution(destination, null, true);
        if (policy == FileConflictPolicy.Fail) throw new IOException($"An item already exists at the SFTP destination: {destination}");
        if (policy == FileConflictPolicy.KeepBoth)
        {
            for (var index = 2; index < 10_000; index++)
            {
                var candidate = AppendRemoteCounter(destination, index);
                if (!client.Exists(candidate)) return new RemoteConflictResolution(candidate, null, false);
            }
            throw new IOException("No available SFTP destination name could be generated.");
        }
        var backup = destination + $".odyssey-backup-{Guid.NewGuid():N}";
        await client.RenameFileAsync(destination, backup, cancellationToken);
        return new RemoteConflictResolution(destination, backup, false);
    }

    private static LocalConflictResolution ResolveLocalConflict(string destination, FileConflictPolicy policy)
    {
        if (!ExistsLocal(destination)) return new LocalConflictResolution(destination, null, false);
        if (policy == FileConflictPolicy.Skip) return new LocalConflictResolution(destination, null, true);
        if (policy == FileConflictPolicy.Fail) throw new IOException($"An item already exists at the destination: {destination}");
        if (policy == FileConflictPolicy.KeepBoth)
        {
            for (var index = 2; index < 10_000; index++)
            {
                var extension = File.Exists(destination) ? Path.GetExtension(destination) : string.Empty;
                var name = extension.Length == 0 ? Path.GetFileName(destination) : Path.GetFileNameWithoutExtension(destination);
                var candidate = Path.Combine(Path.GetDirectoryName(destination)!, $"{name} ({index}){extension}");
                if (!ExistsLocal(candidate)) return new LocalConflictResolution(candidate, null, false);
            }
            throw new IOException("No available destination name could be generated.");
        }
        var backup = destination + $".odyssey-backup-{Guid.NewGuid():N}";
        MoveLocalEntry(destination, backup);
        return new LocalConflictResolution(destination, backup, false);
    }

    private static async Task<long> GetRemoteSizeAsync(SftpClient client, string root, CancellationToken cancellationToken)
    {
        long total = 0;
        await foreach (var entry in client.ListDirectoryAsync(root, cancellationToken))
        {
            if (entry.Name is "." or "..") continue;
            if (entry.IsDirectory && !entry.IsSymbolicLink)
                total += await GetRemoteSizeAsync(client, entry.FullName, cancellationToken);
            else if (!entry.IsSymbolicLink)
                total += checked((long)entry.Length);
        }
        return total;
    }

    private static long GetLocalSize(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        long total = 0;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(path));
        while (pending.Count > 0)
        {
            foreach (var entry in pending.Pop().EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null)
                    throw new IOException("SFTP transfer does not follow symbolic links.");
                if (entry is FileInfo file) total += file.Length;
                else if (entry is DirectoryInfo directory) pending.Push(directory);
            }
        }
        return total;
    }

    private static async Task DeleteRemoteTreeAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        var info = await client.GetAsync(path, cancellationToken);
        if (!info.IsDirectory || info.IsSymbolicLink)
        {
            await client.DeleteFileAsync(path, cancellationToken);
            return;
        }
        await foreach (var entry in client.ListDirectoryAsync(path, cancellationToken))
        {
            if (entry.Name is "." or "..") continue;
            await DeleteRemoteTreeAsync(client, entry.FullName, cancellationToken);
        }
        await client.DeleteDirectoryAsync(path, cancellationToken);
    }

    private static async Task TryDeleteRemoteAsync(SftpClient client, string path, CancellationToken cancellationToken)
    {
        try { if (client.Exists(path)) await DeleteRemoteTreeAsync(client, path, cancellationToken); }
        catch { }
    }

    private static async Task RestoreRemoteBackupAsync(SftpClient client, string? backup, string destination)
    {
        if (backup is null) return;
        try
        {
            if (client.Exists(backup) && !client.Exists(destination))
                await client.RenameFileAsync(backup, destination, CancellationToken.None);
        }
        catch { }
    }

    private Session GetSession(string connectionKey)
    {
        lock (_sync)
            return _sessions.GetValueOrDefault(connectionKey)
                   ?? throw new InvalidOperationException("The SFTP session is not connected. Reconnect and retry the transfer.");
    }

    private static void ValidateConnectionRequest(SftpConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Host) || string.IsNullOrWhiteSpace(request.Username))
            throw new ArgumentException("SFTP host and username are required.", nameof(request));
        if (request.Port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(request.Port));
        if (string.IsNullOrEmpty(request.Password)) throw new ArgumentException("SFTP password is required.", nameof(request));
    }

    private static string? NormalizeFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? "SHA256:" + trimmed[7..]
            : "SHA256:" + trimmed;
    }

    private static string CreateConnectionKey(string host, int port, string username, string fingerprint)
    {
        var identity = $"{host.Trim().ToLowerInvariant()}\n{port}\n{username.Trim()}\n{fingerprint}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var parts = new Stack<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (parts.Count > 0) parts.Pop(); continue; }
            parts.Push(part);
        }
        return "/" + string.Join('/', parts.Reverse());
    }

    private static string CombineRemote(string parent, string name) =>
        NormalizeRemotePath(parent).TrimEnd('/') + "/" + name.Replace("/", string.Empty, StringComparison.Ordinal);
    private static string RemoteName(string path) => path.TrimEnd('/').Split('/')[^1];
    private static string AppendRemoteCounter(string path, int index)
    {
        var slash = path.LastIndexOf('/');
        var parent = path[..(slash + 1)];
        var name = path[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? $"{parent}{name[..dot]} ({index}){name[dot..]}" : $"{parent}{name} ({index})";
    }

    private static bool ExistsLocal(string path) => File.Exists(path) || Directory.Exists(path);
    private static void MoveLocalEntry(string source, string destination)
    {
        if (File.Exists(source)) File.Move(source, destination);
        else Directory.Move(source, destination);
    }
    private static void DeleteLocalEntry(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, true);
    }
    private static void TryDeleteLocal(string path) { try { DeleteLocalEntry(path); } catch { } }

    public async ValueTask DisposeAsync()
    {
        Session[] sessions;
        lock (_sync) { sessions = _sessions.Values.ToArray(); _sessions.Clear(); }
        foreach (var session in sessions) await session.DisposeAsync();
    }

    private sealed record RemoteConflictResolution(string Destination, string? Backup, bool Skipped);
    private sealed record LocalConflictResolution(string Destination, string? Backup, bool Skipped);

    private sealed class Session(SftpConnectionInfo info, SftpClient client) : IAsyncDisposable
    {
        public SftpConnectionInfo Info { get; } = info;
        public SftpClient Client { get; } = client;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public async ValueTask DisposeAsync()
        {
            await Gate.WaitAsync();
            try { if (Client.IsConnected) Client.Disconnect(); Client.Dispose(); }
            finally { Gate.Release(); Gate.Dispose(); }
        }
    }

    private sealed class ProgressReadStream(Stream inner, Action<int> report) : Stream
    {
        public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => false;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); report(read); return read; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await inner.ReadAsync(buffer, cancellationToken); report(read); return read; }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }

    private sealed class ProgressWriteStream(Stream inner, Action<int> report) : Stream
    {
        public override bool CanRead => false; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush(); public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) { inner.Write(buffer, offset, count); report(count); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { await inner.WriteAsync(buffer, cancellationToken); report(buffer.Length); }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override async ValueTask DisposeAsync() { await inner.DisposeAsync(); GC.SuppressFinalize(this); }
    }
}
