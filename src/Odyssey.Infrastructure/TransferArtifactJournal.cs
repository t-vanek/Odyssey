using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Odyssey.Infrastructure;

internal sealed class TransferArtifactJournal
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessGates =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _path;
    private readonly string _lockPath;
    private readonly SemaphoreSlim _gate;

    public TransferArtifactJournal(ApplicationStorage storage)
    {
        _path = Path.GetFullPath(Path.Combine(storage.DirectoryPath, "transfer-artifacts.json"));
        _lockPath = _path + ".lock";
        _gate = ProcessGates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
        _gate.Wait();
        try
        {
            using var lease = AcquireFileLockAsync(CancellationToken.None).GetAwaiter().GetResult();
            LoadAndRecover();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally { _gate.Release(); }
    }

    public async Task RegisterAsync(
        Guid id,
        string artifactPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var record = CreateValidatedRecord(id, artifactPath, destinationPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireFileLockAsync(cancellationToken).ConfigureAwait(false);
            var records = ReadRecords();
            if (records.Any(item => item.Id == id))
                throw new IOException("A transfer artifact with the same identity is already registered.");
            records.Add(record);
            await SaveAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task TryCompleteAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var lease = await AcquireFileLockAsync(CancellationToken.None).ConfigureAwait(false);
            var records = ReadRecords();
            var index = records.FindIndex(item => item.Id == id);
            if (index < 0) return;
            records.RemoveAt(index);
            await SaveAsync(records, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        finally { _gate.Release(); }
    }

    private void LoadAndRecover()
    {
        TryDeleteFile(_path + ".tmp");
        List<TransferArtifactRecord> loaded;
        try
        {
            loaded = ReadRecords();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            QuarantineInvalidJournal();
            return;
        }

        var remaining = new List<TransferArtifactRecord>();
        foreach (var record in loaded)
        {
            if (IsProcessAlive(record.OwnerProcessId))
            {
                remaining.Add(record);
                continue;
            }
            if (!EntryExists(record.ArtifactPath)) continue;
            try { DeleteOwnedEntry(record.ArtifactPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(record);
            }
        }
        try { Save(remaining); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private List<TransferArtifactRecord> ReadRecords()
    {
        if (!File.Exists(_path)) return [];
        var loaded = JsonSerializer.Deserialize<List<TransferArtifactRecord>>(File.ReadAllText(_path), JsonOptions);
        if (loaded is null || loaded.Any(item => !IsValid(item))
                           || loaded.Select(item => item.Id).Distinct().Count() != loaded.Count)
            throw new JsonException("The transfer artifact journal is invalid.");
        return loaded;
    }

    private async Task SaveAsync(
        IReadOnlyCollection<TransferArtifactRecord> records,
        CancellationToken cancellationToken)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         16 * 1024, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private async Task<FileStream> AcquireFileLockAsync(CancellationToken cancellationToken)
    {
        const int maximumAttempts = 100;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    1, FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void Save(IReadOnlyCollection<TransferArtifactRecord> records)
    {
        var temporaryPath = _path + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, records, JsonOptions);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static TransferArtifactRecord CreateValidatedRecord(
        Guid id,
        string artifactPath,
        string destinationPath)
    {
        var destination = Path.GetFullPath(destinationPath);
        var artifact = Path.GetFullPath(artifactPath);
        var record = new TransferArtifactRecord(
            id, artifact, destination, DateTimeOffset.UtcNow, Environment.ProcessId);
        if (!IsValid(record)) throw new IOException("The transfer artifact path is invalid.");
        return record;
    }

    private static bool IsValid(TransferArtifactRecord record)
    {
        if (record.Id == Guid.Empty || record.OwnerProcessId <= 0 || string.IsNullOrWhiteSpace(record.ArtifactPath)
                                    || string.IsNullOrWhiteSpace(record.DestinationPath)) return false;
        try
        {
            var destination = Path.GetFullPath(record.DestinationPath);
            var artifact = Path.GetFullPath(record.ArtifactPath);
            var expected = destination + $".odyssey-part-{record.Id:N}";
            return PathComparer.Equals(artifact, expected)
                   && PathComparer.Equals(Path.GetDirectoryName(artifact), Path.GetDirectoryName(destination));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void QuarantineInvalidJournal()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var quarantine = Path.Combine(Path.GetDirectoryName(_path)!,
                $"transfer-artifacts.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json");
            File.Move(_path, quarantine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void DeleteOwnedEntry(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(path, recursive: false);
            else File.Delete(path);
            return;
        }
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            File.Delete(path);
            return;
        }

        foreach (var child in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            var childAttributes = child.Attributes;
            if (childAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                child.Delete();
            }
            else if (child is DirectoryInfo)
            {
                DeleteOwnedEntry(child.FullName);
            }
            else
            {
                child.Delete();
            }
        }
        Directory.Delete(path, recursive: false);
    }

    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record TransferArtifactRecord(
        Guid Id,
        string ArtifactPath,
        string DestinationPath,
        DateTimeOffset CreatedAt,
        int OwnerProcessId);
}
