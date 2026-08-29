using System.Text.Json;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

internal enum ArchiveRecoveryState
{
    Prepared,
    OriginalBackedUp,
    Published
}

internal enum ArchivePublicationCheckpoint
{
    OriginalBackedUp,
    ReplacementMoved
}

internal sealed record ArchiveRecoveryManifest
{
    public const int CurrentSchemaVersion = 1;
    public required int SchemaVersion { get; init; }
    public required Guid TransactionId { get; init; }
    public required string DestinationPath { get; init; }
    public required string TemporaryPath { get; init; }
    public string? BackupPath { get; init; }
    public required bool OriginalExisted { get; init; }
    public required ArchiveRecoveryState State { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed partial class SafeArchiveService
{
    private const string RecoveryFilePrefix = ".odyssey-archive-recovery-";
    private const long MaximumRecoveryManifestBytes = 64 * 1024;
    private const int MaximumRecoveryManifests = 1_000;
    private static readonly JsonSerializerOptions RecoveryJsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal Action<ArchivePublicationCheckpoint>? PublicationCheckpoint { get; init; }

    public async Task<ArchiveRecoveryReport> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_recoveryRoot is null) return new ArchiveRecoveryReport(0, []);
            return await RecoverLocationAsync(_recoveryRoot, create: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<ArchiveRecoveryReport> RecoverBeforeWriteAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        var location = _recoveryRoot ?? Path.GetDirectoryName(archivePath)!;
        var report = await RecoverLocationAsync(location, create: _recoveryRoot is not null, cancellationToken)
            .ConfigureAwait(false);
        if (!report.IsSuccessful)
            throw new IOException("An archive recovery transaction requires manual inspection: "
                                  + string.Join("; ", report.Errors));
        return report;
    }

    private async Task<ArchiveRecoveryReport> RecoverLocationAsync(
        string location,
        bool create,
        CancellationToken cancellationToken)
    {
        if (create) Directory.CreateDirectory(location);
        if (!Directory.Exists(location)) return new ArchiveRecoveryReport(0, []);
        ValidateDestinationDirectory(location);
        var manifests = Directory.EnumerateFiles(location, $"{RecoveryFilePrefix}*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, PathComparer)
            .Take(MaximumRecoveryManifests + 1)
            .ToArray();
        if (manifests.Length > MaximumRecoveryManifests)
            return new ArchiveRecoveryReport(0, ["The archive recovery manifest limit was exceeded."]);

        var recovered = 0;
        var errors = new List<string>();
        foreach (var manifestPath in manifests)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var info = ValidateRegularFile(manifestPath, "Recovery manifest");
                if (info.Length > MaximumRecoveryManifestBytes)
                    throw new InvalidDataException("Recovery manifest is too large.");
                await using var stream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var manifest = await JsonSerializer.DeserializeAsync<ArchiveRecoveryManifest>(
                                   stream, RecoveryJsonOptions, cancellationToken).ConfigureAwait(false)
                               ?? throw new InvalidDataException("Recovery manifest is empty.");
                ValidateRecoveryManifest(manifestPath, manifest);
                RecoverManifest(manifestPath, manifest, cancellationToken);
                recovered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                errors.Add($"{Path.GetFileName(manifestPath)}: {ex.Message}");
            }
        }
        return new ArchiveRecoveryReport(recovered, errors);
    }

    private void RecoverManifest(
        string manifestPath,
        ArchiveRecoveryManifest manifest,
        CancellationToken cancellationToken)
    {
        var destinationExists = File.Exists(manifest.DestinationPath);
        var temporaryExists = File.Exists(manifest.TemporaryPath);
        var backupExists = manifest.BackupPath is not null && File.Exists(manifest.BackupPath);
        if (destinationExists) ValidateRecoverableZip(manifest.DestinationPath, cancellationToken);
        if (temporaryExists) ValidateRecoverableZip(manifest.TemporaryPath, cancellationToken);
        if (backupExists) ValidateRecoverableZip(manifest.BackupPath!, cancellationToken);

        if (manifest.State == ArchiveRecoveryState.Published)
        {
            if (!destinationExists)
            {
                if (!backupExists)
                    throw new IOException("Published archive and its recovery backup are both missing.");
                File.Move(manifest.BackupPath!, manifest.DestinationPath);
                backupExists = false;
            }
            DeleteRegularFileIfPresent(manifest.TemporaryPath);
            if (backupExists) DeleteRegularFileIfPresent(manifest.BackupPath!);
            DeleteRegularFileIfPresent(manifestPath);
            return;
        }

        if (!manifest.OriginalExisted)
        {
            if (destinationExists && temporaryExists)
                throw new IOException("Both a new archive and its unpublished temporary file exist.");
            if (temporaryExists) DeleteRegularFileIfPresent(manifest.TemporaryPath);
            // If the temporary file was already moved, publication completed. Keep the valid destination.
            DeleteRegularFileIfPresent(manifestPath);
            return;
        }

        if (!backupExists)
        {
            if (!destinationExists)
                throw new IOException("The original archive and its recovery backup are both missing.");
            if (!temporaryExists)
                throw new IOException("The recovery state is incomplete and has no temporary archive.");
            // The original is still at its destination, so the mutation never entered publication.
            DeleteRegularFileIfPresent(manifest.TemporaryPath);
            DeleteRegularFileIfPresent(manifestPath);
            return;
        }

        if (destinationExists && temporaryExists)
            throw new IOException("Archive recovery found an ambiguous destination and temporary archive.");
        if (!destinationExists && temporaryExists)
        {
            // The original was backed up but publication was interrupted. Complete the already validated move.
            File.Move(manifest.TemporaryPath, manifest.DestinationPath);
            destinationExists = true;
        }
        if (!destinationExists)
        {
            // No publishable replacement remains, so restore the byte-for-byte backup.
            File.Move(manifest.BackupPath!, manifest.DestinationPath);
            DeleteRegularFileIfPresent(manifestPath);
            return;
        }
        DeleteRegularFileIfPresent(manifest.BackupPath!);
        DeleteRegularFileIfPresent(manifestPath);
    }

    private void ValidateRecoverableZip(string path, CancellationToken cancellationToken)
    {
        ValidateRegularFile(path, "Archive recovery artifact");
        _ = ScanZip(path, cancellationToken);
    }

    private static void ValidateRecoveryManifest(string manifestPath, ArchiveRecoveryManifest manifest)
    {
        if (manifest.SchemaVersion != ArchiveRecoveryManifest.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported archive recovery schema.");
        if (manifest.TransactionId == Guid.Empty)
            throw new InvalidDataException("Recovery transaction identifier is missing.");
        var expectedName = RecoveryManifestFileName(manifest.TransactionId);
        if (!string.Equals(Path.GetFileName(manifestPath), expectedName, StringComparison.Ordinal))
            throw new InvalidDataException("Recovery manifest filename does not match its transaction.");
        if (!Path.IsPathFullyQualified(manifest.DestinationPath)
            || !manifest.DestinationPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recovery destination is not an absolute ZIP path.");
        var destination = Path.GetFullPath(manifest.DestinationPath);
        var parent = Path.GetDirectoryName(destination)
                     ?? throw new InvalidDataException("Recovery destination has no parent directory.");
        ValidateDestinationDirectory(parent);
        if (!PathComparer.Equals(destination, manifest.DestinationPath)
            || !PathComparer.Equals(ExpectedTemporaryPath(destination, manifest.TransactionId), manifest.TemporaryPath))
            throw new InvalidDataException("Recovery artifact paths do not match the transaction.");
        var expectedBackup = manifest.OriginalExisted
            ? ExpectedBackupPath(destination, manifest.TransactionId)
            : null;
        if ((expectedBackup is null) != (manifest.BackupPath is null)
            || expectedBackup is not null && !PathComparer.Equals(expectedBackup, manifest.BackupPath))
            throw new InvalidDataException("Recovery backup path does not match the transaction.");
        ValidateArtifactPath(manifest.TemporaryPath);
        if (manifest.BackupPath is not null) ValidateArtifactPath(manifest.BackupPath);
        ValidateArtifactPath(destination);
    }

    private static void ValidateArtifactPath(string path)
    {
        if (Directory.Exists(path)) throw new IOException("Archive recovery artifact points to a directory.");
        if (File.Exists(path)) _ = ValidateRegularFile(path, "Archive recovery artifact");
    }

    private static FileInfo ValidateRegularFile(string path, string description)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"{description} is missing.", path);
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{description} cannot be a symbolic link or reparse point.");
        return info;
    }

    private string GetRecoveryManifestPath(string destination, Guid transactionId)
    {
        var root = _recoveryRoot ?? Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(root);
        ValidateDestinationDirectory(root);
        return Path.Combine(root, RecoveryManifestFileName(transactionId));
    }

    private static string RecoveryManifestFileName(Guid transactionId) =>
        $"{RecoveryFilePrefix}{transactionId:N}.json";

    private static string ExpectedTemporaryPath(string destination, Guid transactionId) =>
        Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileNameWithoutExtension(destination)}.odyssey-new-{transactionId:N}.zip");

    private static string ExpectedBackupPath(string destination, Guid transactionId) =>
        destination + $".odyssey-backup-{transactionId:N}";

    private static void DeleteRegularFileIfPresent(string path)
    {
        if (!File.Exists(path)) return;
        _ = ValidateRegularFile(path, "Recovery cleanup artifact");
        File.Delete(path);
    }

    private static void WriteRecoveryManifest(string path, ArchiveRecoveryManifest manifest)
    {
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                       16 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, manifest, RecoveryJsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
