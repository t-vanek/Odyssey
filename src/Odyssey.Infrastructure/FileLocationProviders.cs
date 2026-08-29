using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class LocalFileLocationProvider : IFileLocationProvider
{
    public FileTransferEndpointKind Kind => FileTransferEndpointKind.Local;
    public FileLocationCapabilities Capabilities => FileLocationCapabilities.Browse | FileLocationCapabilities.Read
                                                     | FileLocationCapabilities.Write | FileLocationCapabilities.Move
                                                     | FileLocationCapabilities.Delete;

    public Task<IReadOnlyList<FileLocationEntry>> ListAsync(
        string path, string? connectionKey = null, CancellationToken cancellationToken = default)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (!directory.Exists) throw new DirectoryNotFoundException(directory.FullName);
        var entries = new List<FileLocationEntry>();
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TransferArtifactNames.IsInternal(item.Name)) continue;
            try
            {
                entries.Add(new FileLocationEntry(
                    item.Name,
                    item.FullName,
                    item is DirectoryInfo ? FileEntryType.Directory : FileEntryType.File,
                    item is FileInfo file ? file.Length : null,
                    new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero),
                    item.LinkTarget is not null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return Task.FromResult<IReadOnlyList<FileLocationEntry>>(entries
            .OrderByDescending(item => item.Type).ThenBy(item => item.Name, PathComparer).ToArray());
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

public sealed class SftpFileLocationProvider(ISftpConnectionService sftp) : IFileLocationProvider
{
    public FileTransferEndpointKind Kind => FileTransferEndpointKind.Sftp;
    public FileLocationCapabilities Capabilities => FileLocationCapabilities.Browse | FileLocationCapabilities.Read
                                                     | FileLocationCapabilities.Write | FileLocationCapabilities.Move
                                                     | FileLocationCapabilities.Delete;
    public Task<IReadOnlyList<FileLocationEntry>> ListAsync(
        string path, string? connectionKey = null, CancellationToken cancellationToken = default) =>
        sftp.ListAsync(connectionKey ?? throw new InvalidOperationException("An SFTP connection is required."), path, cancellationToken);
}

public sealed class ArchiveFileLocationProvider(IArchiveService archives) : IFileLocationProvider
{
    public FileTransferEndpointKind Kind => FileTransferEndpointKind.Archive;
    public FileLocationCapabilities Capabilities => FileLocationCapabilities.Browse | FileLocationCapabilities.Read;

    public async Task<IReadOnlyList<FileLocationEntry>> ListAsync(
        string path, string? connectionKey = null, CancellationToken cancellationToken = default)
    {
        var archivePath = connectionKey
                          ?? throw new InvalidOperationException("An archive path is required.");
        var entries = await archives.ListAsync(archivePath, path, cancellationToken).ConfigureAwait(false);
        return entries.Select(entry => new FileLocationEntry(
            entry.Name, entry.Path, entry.Type, entry.Size, entry.ModifiedAt, entry.IsSymbolicLink)).ToArray();
    }
}

public sealed class FileLocationProviderRegistry(IEnumerable<IFileLocationProvider> providers) : IFileLocationProviderRegistry
{
    private readonly IReadOnlyDictionary<FileTransferEndpointKind, IFileLocationProvider> _providers =
        providers.ToDictionary(provider => provider.Kind);

    public IFileLocationProvider Get(FileTransferEndpointKind kind) =>
        _providers.GetValueOrDefault(kind)
        ?? throw new NotSupportedException($"No file-location provider is registered for {kind}.");
}
