using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;
    private readonly int _cacheKiB;
    private readonly long _mmapBytes;

    public SqliteConnectionFactory(
        ApplicationStorage storage,
        SystemPerformanceProfile? performance = null,
        bool pooling = true)
    {
        performance ??= SystemPerformanceProfile.Current;
        DatabasePath = storage.DatabasePath;
        _cacheKiB = performance.SqliteCacheKiB;
        _mmapBytes = performance.SqliteMmapBytes;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = pooling
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-{_cacheKiB};
            PRAGMA mmap_size={_mmapBytes};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}

public sealed class SqliteOdysseyStore(
    SqliteConnectionFactory connections,
    ILogger<SqliteOdysseyStore> logger) : IOdysseyStore
{
    public string DatabasePath => connections.DatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;

            CREATE TABLE IF NOT EXISTS RescueSessions (
                Id TEXT PRIMARY KEY,
                Name TEXT NOT NULL,
                Mode INTEGER NOT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ScanTargets (
                Id TEXT PRIMARY KEY,
                SessionId TEXT NOT NULL REFERENCES RescueSessions(Id) ON DELETE CASCADE,
                RootPath TEXT NOT NULL,
                Recursive INTEGER NOT NULL,
                IncludedExtensions TEXT NOT NULL,
                ExcludedPatterns TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ScanSessions (
                Id TEXT PRIMARY KEY,
                TargetId TEXT NOT NULL REFERENCES ScanTargets(Id) ON DELETE CASCADE,
                StartedAt TEXT NOT NULL,
                CompletedAt TEXT NULL,
                Status INTEGER NOT NULL,
                ResumedFromScanId TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS ScanCheckpoints (
                ScanId TEXT PRIMARY KEY REFERENCES ScanSessions(Id) ON DELETE CASCADE,
                FilesDiscovered INTEGER NOT NULL,
                DirectoriesDiscovered INTEGER NOT NULL,
                EntriesIndexed INTEGER NOT NULL,
                BytesObserved INTEGER NOT NULL,
                Errors INTEGER NOT NULL,
                ElapsedTicks INTEGER NOT NULL,
                CurrentPath TEXT NULL,
                UpdatedAt TEXT NOT NULL,
                OwnerProcessId INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TargetId TEXT NOT NULL REFERENCES ScanTargets(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL,
                FullPath TEXT NOT NULL,
                ParentPath TEXT NOT NULL,
                Extension TEXT NULL,
                EntryType INTEGER NOT NULL,
                Category INTEGER NOT NULL,
                Size INTEGER NULL,
                CreatedAt TEXT NULL,
                ModifiedAt TEXT NULL,
                Fingerprint INTEGER NULL,
                ContentText TEXT NULL,
                ContentIndexedModifiedAt TEXT NULL,
                ContentError TEXT NULL,
                ContentStatus INTEGER NOT NULL DEFAULT 0,
                ContentAttemptedAt TEXT NULL,
                LastSeenScanId TEXT NOT NULL REFERENCES ScanSessions(Id),
                IsMissing INTEGER NOT NULL DEFAULT 0,
                UNIQUE(TargetId, FullPath)
            );

            CREATE TABLE IF NOT EXISTS ScanErrors (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ScanId TEXT NOT NULL REFERENCES ScanSessions(Id) ON DELETE CASCADE,
                Path TEXT NULL,
                Message TEXT NOT NULL,
                OccurredAt TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_ScanTargets_SessionId ON ScanTargets(SessionId);
            CREATE INDEX IF NOT EXISTS IX_ScanSessions_TargetId ON ScanSessions(TargetId);
            CREATE INDEX IF NOT EXISTS IX_ScanSessions_Status ON ScanSessions(Status, TargetId);
            CREATE INDEX IF NOT EXISTS IX_Files_TargetId ON Files(TargetId);
            CREATE INDEX IF NOT EXISTS IX_Files_Target_Parent_Name ON Files(TargetId, ParentPath, Name);
            CREATE INDEX IF NOT EXISTS IX_Files_Target_Modified ON Files(TargetId, ModifiedAt, Id);
            CREATE INDEX IF NOT EXISTS IX_Files_Target_LastSeen ON Files(TargetId, LastSeenScanId);
            CREATE INDEX IF NOT EXISTS IX_Files_Category ON Files(Category);
            CREATE INDEX IF NOT EXISTS IX_Files_Extension ON Files(Extension);
            CREATE INDEX IF NOT EXISTS IX_Files_Size ON Files(Size) WHERE EntryType = 0 AND IsMissing = 0;
            CREATE INDEX IF NOT EXISTS IX_Files_ModifiedAt ON Files(ModifiedAt);
            CREATE INDEX IF NOT EXISTS IX_ScanErrors_ScanId ON ScanErrors(ScanId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "Files", "ContentText", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "Files", "ContentIndexedModifiedAt", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "Files", "ContentError", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "Files", "ContentStatus", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "Files", "ContentAttemptedAt", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ScanSessions", "ResumedFromScanId", "TEXT NULL", cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Odyssey database initialized at {DatabasePath}", DatabasePath);
    }

    public async Task<IReadOnlyList<RescueSession>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RescueSession>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Mode, CreatedAt FROM RescueSessions ORDER BY CreatedAt DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new RescueSession
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Mode = (SessionMode)reader.GetInt32(2),
                CreatedAt = ParseDate(reader.GetString(3))
            });
        return result;
    }

    public async Task<RescueSession> SaveSessionAsync(RescueSession session, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO RescueSessions(Id, Name, Mode, CreatedAt) VALUES($id, $name, $mode, $created)
            ON CONFLICT(Id) DO UPDATE SET Name=excluded.Name, Mode=excluded.Mode;
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString());
        command.Parameters.AddWithValue("$name", session.Name);
        command.Parameters.AddWithValue("$mode", (int)session.Mode);
        command.Parameters.AddWithValue("$created", FormatDate(session.CreatedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Rescue session {SessionId} saved", session.Id);
        return session;
    }

    public async Task RenameSessionAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Session name is required.", nameof(name));
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE RescueSessions SET Name=$name WHERE Id=$id;";
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScanTarget>> GetTargetsAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var result = new List<ScanTarget>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, SessionId, RootPath, Recursive, IncludedExtensions, ExcludedPatterns FROM ScanTargets WHERE SessionId=$session ORDER BY RootPath;";
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new ScanTarget
            {
                Id = Guid.Parse(reader.GetString(0)),
                SessionId = Guid.Parse(reader.GetString(1)),
                RootPath = reader.GetString(2),
                Recursive = reader.GetBoolean(3),
                IncludedExtensions = DeserializeArray(reader.GetString(4)),
                ExcludedPatterns = DeserializeArray(reader.GetString(5))
            });
        return result;
    }

    public async Task<ScanTarget> SaveTargetAsync(ScanTarget target, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(target.RootPath)) throw new ArgumentException("Target path is required.", nameof(target));
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScanTargets(Id, SessionId, RootPath, Recursive, IncludedExtensions, ExcludedPatterns)
            VALUES($id, $session, $root, $recursive, $included, $excluded)
            ON CONFLICT(Id) DO UPDATE SET RootPath=excluded.RootPath, Recursive=excluded.Recursive,
                IncludedExtensions=excluded.IncludedExtensions, ExcludedPatterns=excluded.ExcludedPatterns;
            """;
        command.Parameters.AddWithValue("$id", target.Id.ToString());
        command.Parameters.AddWithValue("$session", target.SessionId.ToString());
        command.Parameters.AddWithValue("$root", Path.GetFullPath(target.RootPath));
        command.Parameters.AddWithValue("$recursive", target.Recursive);
        command.Parameters.AddWithValue("$included", JsonSerializer.Serialize(target.IncludedExtensions));
        command.Parameters.AddWithValue("$excluded", JsonSerializer.Serialize(target.ExcludedPatterns));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return target with { RootPath = Path.GetFullPath(target.RootPath) };
    }

    public async Task RemoveTargetAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ScanTargets WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", targetId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StartScanAsync(ScanSession session, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ScanSessions(Id, TargetId, StartedAt, CompletedAt, Status, ResumedFromScanId) VALUES($id,$target,$started,NULL,$status,$resumed);";
        command.Parameters.AddWithValue("$id", session.Id.ToString());
        command.Parameters.AddWithValue("$target", session.TargetId.ToString());
        command.Parameters.AddWithValue("$started", FormatDate(session.StartedAt));
        command.Parameters.AddWithValue("$status", (int)ScanStatus.Running);
        command.Parameters.AddWithValue("$resumed", (object?)session.ResumedFromScanId?.ToString() ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteScanAsync(Guid scanId, ScanStatus status, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ScanSessions SET CompletedAt=$completed, Status=$status WHERE Id=$id;";
        command.Parameters.AddWithValue("$completed", FormatDate(completedAt));
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$id", scanId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveScanCheckpointAsync(
        Guid scanId,
        ScanProgress progress,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ScanCheckpoints(
                ScanId, FilesDiscovered, DirectoriesDiscovered, EntriesIndexed,
                BytesObserved, Errors, ElapsedTicks, CurrentPath, UpdatedAt, OwnerProcessId)
            VALUES($scan,$files,$directories,$indexed,$bytes,$errors,$elapsed,$path,$updated,$owner)
            ON CONFLICT(ScanId) DO UPDATE SET
                FilesDiscovered=excluded.FilesDiscovered,
                DirectoriesDiscovered=excluded.DirectoriesDiscovered,
                EntriesIndexed=excluded.EntriesIndexed,
                BytesObserved=excluded.BytesObserved,
                Errors=excluded.Errors,
                ElapsedTicks=excluded.ElapsedTicks,
                CurrentPath=excluded.CurrentPath,
                UpdatedAt=excluded.UpdatedAt,
                OwnerProcessId=excluded.OwnerProcessId;
            """;
        command.Parameters.AddWithValue("$scan", scanId.ToString());
        command.Parameters.AddWithValue("$files", progress.FilesDiscovered);
        command.Parameters.AddWithValue("$directories", progress.DirectoriesDiscovered);
        command.Parameters.AddWithValue("$indexed", progress.EntriesIndexed);
        command.Parameters.AddWithValue("$bytes", progress.BytesObserved);
        command.Parameters.AddWithValue("$errors", progress.Errors);
        command.Parameters.AddWithValue("$elapsed", progress.Elapsed.Ticks);
        command.Parameters.AddWithValue("$path", (object?)progress.CurrentPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$owner", Environment.ProcessId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InterruptedScanRecovery>> RecoverInterruptedScansAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var candidates = new List<(InterruptedScanRecovery Recovery, int? OwnerProcessId)>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT s.Id, s.TargetId,
                       COALESCE(c.FilesDiscovered,0), COALESCE(c.DirectoriesDiscovered,0),
                       COALESCE(c.EntriesIndexed,0), COALESCE(c.BytesObserved,0), COALESCE(c.Errors,0),
                       COALESCE(c.ElapsedTicks,0), c.CurrentPath,
                       COALESCE(c.UpdatedAt,s.StartedAt), c.OwnerProcessId
                FROM ScanSessions s
                JOIN ScanTargets t ON t.Id=s.TargetId
                LEFT JOIN ScanCheckpoints c ON c.ScanId=s.Id
                WHERE t.SessionId=$session AND s.Status=$running
                ORDER BY s.StartedAt;
                """;
            query.Parameters.AddWithValue("$session", sessionId.ToString());
            query.Parameters.AddWithValue("$running", (int)ScanStatus.Running);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var checkpoint = new ScanProgress(
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt64(6), TimeSpan.FromTicks(reader.GetInt64(7)),
                    reader.IsDBNull(8) ? null : reader.GetString(8));
                candidates.Add((new InterruptedScanRecovery(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), checkpoint,
                    ParseDate(reader.GetString(9))), reader.IsDBNull(10) ? null : reader.GetInt32(10)));
            }
        }

        var recoverable = candidates
            .Where(item => item.OwnerProcessId is null || item.OwnerProcessId == Environment.ProcessId || !IsProcessAlive(item.OwnerProcessId.Value))
            .Select(item => item.Recovery)
            .ToArray();
        if (recoverable.Length == 0) return recoverable;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE ScanSessions
            SET Status=$interrupted, CompletedAt=$completed
            WHERE Id=$id AND Status=$running;
            """;
        update.Parameters.AddWithValue("$interrupted", (int)ScanStatus.Interrupted);
        update.Parameters.AddWithValue("$completed", FormatDate(DateTimeOffset.UtcNow));
        update.Parameters.AddWithValue("$running", (int)ScanStatus.Running);
        var idParameter = update.Parameters.Add("$id", SqliteType.Text);
        foreach (var item in recoverable)
        {
            idParameter.Value = item.ScanId.ToString();
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recoverable;
    }

    public async Task UpsertEntriesAsync(Guid scanId, IReadOnlyList<FileEntry> entries, CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO Files(TargetId, Name, FullPath, ParentPath, Extension, EntryType, Category, Size, CreatedAt, ModifiedAt, Fingerprint, LastSeenScanId, IsMissing)
            VALUES($target,$name,$full,$parent,$extension,$type,$category,$size,$created,$modified,$fingerprint,$scan,0)
            ON CONFLICT(TargetId, FullPath) DO UPDATE SET
                Name=excluded.Name, ParentPath=excluded.ParentPath, Extension=excluded.Extension,
                EntryType=excluded.EntryType, Category=excluded.Category,
                Fingerprint=CASE WHEN Files.Size IS excluded.Size AND Files.ModifiedAt IS excluded.ModifiedAt THEN Files.Fingerprint ELSE NULL END,
                ContentText=CASE WHEN Files.Size IS excluded.Size AND Files.ModifiedAt IS excluded.ModifiedAt THEN Files.ContentText ELSE NULL END,
                ContentIndexedModifiedAt=CASE WHEN Files.Size IS excluded.Size AND Files.ModifiedAt IS excluded.ModifiedAt THEN Files.ContentIndexedModifiedAt ELSE NULL END,
                ContentError=CASE WHEN Files.Size IS excluded.Size AND Files.ModifiedAt IS excluded.ModifiedAt THEN Files.ContentError ELSE NULL END,
                ContentStatus=CASE WHEN Files.Size IS excluded.Size AND Files.ModifiedAt IS excluded.ModifiedAt THEN Files.ContentStatus ELSE 0 END,
                ContentAttemptedAt=CASE WHEN Files.Size IS excluded.Size AND Files.ModifiedAt IS excluded.ModifiedAt THEN Files.ContentAttemptedAt ELSE NULL END,
                Size=excluded.Size, CreatedAt=excluded.CreatedAt, ModifiedAt=excluded.ModifiedAt,
                LastSeenScanId=excluded.LastSeenScanId, IsMissing=0;
            """;
        AddEntryParameters(command);
        command.Prepare();
        foreach (var entry in entries)
        {
            SetEntryParameters(command, entry, scanId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkMissingAsync(Guid targetId, Guid successfulScanId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var collation = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
        command.CommandText = $"""
            UPDATE Files AS f SET IsMissing=1
            WHERE f.TargetId=$target AND f.LastSeenScanId<>$scan
              AND NOT EXISTS (
                  SELECT 1 FROM ScanErrors e
                  WHERE e.ScanId=$scan AND e.Path IS NOT NULL AND (
                      f.FullPath=e.Path{collation}
                      OR substr(f.FullPath,1,length(rtrim(e.Path,'/\'))+1)=rtrim(e.Path,'/\')||'/'{collation}
                      OR substr(f.FullPath,1,length(rtrim(e.Path,'/\'))+1)=rtrim(e.Path,'/\')||'\'{collation}
                  )
              );
            """;
        command.Parameters.AddWithValue("$target", targetId.ToString());
        command.Parameters.AddWithValue("$scan", successfulScanId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddScanErrorsAsync(IReadOnlyList<ScanError> errors, CancellationToken cancellationToken = default)
    {
        if (errors.Count == 0) return;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO ScanErrors(ScanId, Path, Message, OccurredAt) VALUES($scan,$path,$message,$occurred);";
        command.Parameters.Add("$scan", SqliteType.Text);
        command.Parameters.Add("$path", SqliteType.Text);
        command.Parameters.Add("$message", SqliteType.Text);
        command.Parameters.Add("$occurred", SqliteType.Text);
        command.Prepare();
        foreach (var error in errors)
        {
            command.Parameters["$scan"].Value = error.ScanId.ToString();
            command.Parameters["$path"].Value = (object?)error.Path ?? DBNull.Value;
            command.Parameters["$message"].Value = error.Message;
            command.Parameters["$occurred"].Value = FormatDate(error.OccurredAt);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> HasCompletedScanAsync(Guid targetId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM ScanSessions WHERE TargetId=$target AND Status=$status LIMIT 1);";
        command.Parameters.AddWithValue("$target", targetId.ToString());
        command.Parameters.AddWithValue("$status", (int)ScanStatus.Completed);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
    }

    public async Task ApplyFileOperationsAsync(
        IReadOnlyList<FileOperationRecord> operations,
        IReadOnlyList<ScanTarget> targets,
        CancellationToken cancellationToken = default)
    {
        if (operations.Count == 0 || targets.Count == 0) return;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var operation in operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operation.IsUndo)
            {
                await ApplyUndoAsync(connection, transaction, operation, targets, cancellationToken).ConfigureAwait(false);
                continue;
            }
            switch (operation.Kind)
            {
                case FileOperationKind.CreateDirectory:
                    await InsertDirectoryAsync(connection, transaction, operation.SourcePath, targets, cancellationToken).ConfigureAwait(false);
                    break;
                case FileOperationKind.Copy when operation.DestinationPath is not null:
                    await CopyIndexedTreeAsync(connection, transaction, operation.SourcePath, operation.DestinationPath, targets, cancellationToken).ConfigureAwait(false);
                    break;
                case FileOperationKind.Move or FileOperationKind.Rename when operation.DestinationPath is not null:
                    await MoveIndexedTreeAsync(connection, transaction, operation.SourcePath, operation.DestinationPath, targets, cancellationToken).ConfigureAwait(false);
                    break;
                case FileOperationKind.Trash:
                    await MarkPathMissingAsync(connection, transaction, operation.SourcePath, targets, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyUndoAsync(SqliteConnection connection, SqliteTransaction transaction,
        FileOperationRecord operation, IReadOnlyList<ScanTarget> targets, CancellationToken token)
    {
        switch (operation.Kind)
        {
            case FileOperationKind.Copy when operation.DestinationPath is not null:
                await MarkPathMissingAsync(connection, transaction, operation.DestinationPath, targets, token).ConfigureAwait(false);
                break;
            case FileOperationKind.Move or FileOperationKind.Rename when operation.DestinationPath is not null:
                await MoveIndexedTreeAsync(connection, transaction, operation.DestinationPath, operation.SourcePath, targets, token).ConfigureAwait(false);
                break;
            case FileOperationKind.CreateDirectory:
                await MarkPathMissingAsync(connection, transaction, operation.SourcePath, targets, token).ConfigureAwait(false);
                break;
        }
    }

    private static async Task CopyIndexedTreeAsync(SqliteConnection connection, SqliteTransaction transaction,
        string source, string destination, IReadOnlyList<ScanTarget> targets, CancellationToken token)
    {
        var sourceTarget = FindTarget(source, targets);
        var destinationTarget = FindTarget(destination, targets);
        if (sourceTarget is null || destinationTarget is null) return;
        var scanId = await GetLatestScanIdAsync(connection, transaction, destinationTarget.Id, token).ConfigureAwait(false);
        if (scanId is null) return;
        await DeleteDestinationRowsAsync(connection, transaction, destinationTarget.Id, destination, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Files(TargetId,Name,FullPath,ParentPath,Extension,EntryType,Category,Size,CreatedAt,ModifiedAt,Fingerprint,LastSeenScanId,IsMissing)
            SELECT $destinationTarget,
                   CASE WHEN FullPath=$source THEN $name ELSE Name END,
                   $destination || substr(FullPath,length($source)+1),
                   CASE WHEN FullPath=$source THEN $parent
                        WHEN ParentPath=$source THEN $destination
                        ELSE $destination || substr(ParentPath,length($source)+1) END,
                   CASE WHEN FullPath=$source AND EntryType=0 THEN $extension ELSE Extension END,
                   EntryType,Category,Size,CreatedAt,ModifiedAt,Fingerprint,$scan,0
            FROM Files
            WHERE TargetId=$sourceTarget AND IsMissing=0
              AND (FullPath=$source OR substr(FullPath,1,length($prefix))=$prefix);
            """;
        AddTreeParameters(command, sourceTarget.Id, destinationTarget.Id, scanId.Value, source, destination);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task MoveIndexedTreeAsync(SqliteConnection connection, SqliteTransaction transaction,
        string source, string destination, IReadOnlyList<ScanTarget> targets, CancellationToken token)
    {
        var sourceTarget = FindTarget(source, targets);
        if (sourceTarget is null) return;
        var destinationTarget = FindTarget(destination, targets);
        if (destinationTarget is null)
        {
            await MarkPathMissingAsync(connection, transaction, source, targets, token).ConfigureAwait(false);
            return;
        }
        var scanId = await GetLatestScanIdAsync(connection, transaction, destinationTarget.Id, token).ConfigureAwait(false);
        if (scanId is null) return;
        await DeleteDestinationRowsAsync(connection, transaction, destinationTarget.Id, destination, token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Files SET
                TargetId=$destinationTarget,
                Name=CASE WHEN FullPath=$source THEN $name ELSE Name END,
                ParentPath=CASE WHEN FullPath=$source THEN $parent
                                WHEN ParentPath=$source THEN $destination
                                ELSE $destination || substr(ParentPath,length($source)+1) END,
                FullPath=$destination || substr(FullPath,length($source)+1),
                Extension=CASE WHEN FullPath=$source AND EntryType=0 THEN $extension ELSE Extension END,
                LastSeenScanId=$scan, IsMissing=0
            WHERE TargetId=$sourceTarget
              AND (FullPath=$source OR substr(FullPath,1,length($prefix))=$prefix);
            """;
        AddTreeParameters(command, sourceTarget.Id, destinationTarget.Id, scanId.Value, source, destination);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task InsertDirectoryAsync(SqliteConnection connection, SqliteTransaction transaction,
        string path, IReadOnlyList<ScanTarget> targets, CancellationToken token)
    {
        var target = FindTarget(path, targets);
        if (target is null || !Directory.Exists(path)) return;
        var scanId = await GetLatestScanIdAsync(connection, transaction, target.Id, token).ConfigureAwait(false);
        if (scanId is null) return;
        var info = new DirectoryInfo(path);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Files(TargetId,Name,FullPath,ParentPath,Extension,EntryType,Category,Size,CreatedAt,ModifiedAt,Fingerprint,LastSeenScanId,IsMissing)
            VALUES($target,$name,$full,$parent,NULL,$type,$category,NULL,$created,$modified,NULL,$scan,0)
            ON CONFLICT(TargetId,FullPath) DO UPDATE SET Name=excluded.Name,ParentPath=excluded.ParentPath,
                ModifiedAt=excluded.ModifiedAt,LastSeenScanId=excluded.LastSeenScanId,IsMissing=0;
            """;
        command.Parameters.AddWithValue("$target", target.Id.ToString());
        command.Parameters.AddWithValue("$name", info.Name);
        command.Parameters.AddWithValue("$full", info.FullName);
        command.Parameters.AddWithValue("$parent", info.Parent?.FullName ?? string.Empty);
        command.Parameters.AddWithValue("$type", (int)FileEntryType.Directory);
        command.Parameters.AddWithValue("$category", (int)FileCategory.Other);
        command.Parameters.AddWithValue("$created", FormatDate(new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero)));
        command.Parameters.AddWithValue("$modified", FormatDate(new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
        command.Parameters.AddWithValue("$scan", scanId.Value.ToString());
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task MarkPathMissingAsync(SqliteConnection connection, SqliteTransaction transaction,
        string path, IReadOnlyList<ScanTarget> targets, CancellationToken token)
    {
        var target = FindTarget(path, targets);
        if (target is null) return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Files SET IsMissing=1 WHERE TargetId=$target AND (FullPath=$path OR substr(FullPath,1,length($prefix))=$prefix);";
        command.Parameters.AddWithValue("$target", target.Id.ToString());
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$prefix", Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task DeleteDestinationRowsAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid targetId, string path, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM Files WHERE TargetId=$target AND (FullPath=$path OR substr(FullPath,1,length($prefix))=$prefix);";
        command.Parameters.AddWithValue("$target", targetId.ToString());
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$prefix", Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void AddTreeParameters(SqliteCommand command, Guid sourceTarget, Guid destinationTarget,
        Guid scanId, string sourcePath, string destinationPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        command.Parameters.AddWithValue("$sourceTarget", sourceTarget.ToString());
        command.Parameters.AddWithValue("$destinationTarget", destinationTarget.ToString());
        command.Parameters.AddWithValue("$scan", scanId.ToString());
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$prefix", Path.TrimEndingDirectorySeparator(source) + Path.DirectorySeparatorChar);
        command.Parameters.AddWithValue("$destination", destination);
        command.Parameters.AddWithValue("$name", Path.GetFileName(destination));
        command.Parameters.AddWithValue("$parent", Path.GetDirectoryName(destination) ?? string.Empty);
        command.Parameters.AddWithValue("$extension", Path.GetExtension(destination).ToLowerInvariant());
    }

    private static async Task<Guid?> GetLatestScanIdAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid targetId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id FROM ScanSessions WHERE TargetId=$target ORDER BY StartedAt DESC LIMIT 1;";
        command.Parameters.AddWithValue("$target", targetId.ToString());
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is string text ? Guid.Parse(text) : null;
    }

    private static ScanTarget? FindTarget(string path, IReadOnlyList<ScanTarget> targets)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return targets.Where(target =>
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.RootPath));
                return string.Equals(fullPath, root, comparison) || fullPath.StartsWith(root + Path.DirectorySeparatorChar, comparison);
            })
            .OrderByDescending(target => target.RootPath.Length)
            .FirstOrDefault();
    }

    public async Task<SessionAnalysis> GetAnalysisAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        const string join = " FROM Files f JOIN ScanTargets t ON t.Id=f.TargetId WHERE t.SessionId=$session";
        var files = await ScalarAsync(connection, "SELECT COUNT(*)" + join + " AND f.EntryType=0", sessionId, cancellationToken);
        var directories = await ScalarAsync(connection, "SELECT COUNT(*)" + join + " AND f.EntryType=1", sessionId, cancellationToken);
        var size = await ScalarAsync(connection, "SELECT COALESCE(SUM(f.Size),0)" + join + " AND f.EntryType=0", sessionId, cancellationToken);
        var missing = await ScalarAsync(connection, "SELECT COUNT(*)" + join + " AND f.IsMissing=1", sessionId, cancellationToken);
        var errors = await ScalarAsync(connection, "SELECT COUNT(*) FROM ScanErrors e JOIN ScanSessions s ON s.Id=e.ScanId JOIN ScanTargets t ON t.Id=s.TargetId WHERE t.SessionId=$session", sessionId, cancellationToken);
        var duplicates = await ScalarAsync(connection, "SELECT COUNT(*) FROM (SELECT f.Size" + join + " AND f.EntryType=0 AND f.IsMissing=0 AND f.Size IS NOT NULL GROUP BY f.Size HAVING COUNT(*)>1)", sessionId, cancellationToken);

        var categories = new List<CategoryStatistic>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT f.Category, COUNT(*), COALESCE(SUM(f.Size),0)" + join + " AND f.EntryType=0 GROUP BY f.Category ORDER BY COUNT(*) DESC;";
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            categories.Add(new CategoryStatistic((FileCategory)reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)));

        return new SessionAnalysis(files, directories, size, missing, errors, duplicates, categories);
    }

    public async Task<IReadOnlyList<FileEntry>> GetDuplicateCandidatesAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var result = new List<FileEntry>();
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.Id,f.TargetId,f.Name,f.FullPath,f.ParentPath,f.Extension,f.EntryType,f.Size,f.CreatedAt,f.ModifiedAt,f.Category,f.Fingerprint,f.IsMissing
            FROM Files f JOIN ScanTargets t ON t.Id=f.TargetId
            WHERE t.SessionId=$session AND f.EntryType=0 AND f.IsMissing=0 AND f.Size IN (
                SELECT f2.Size FROM Files f2 JOIN ScanTargets t2 ON t2.Id=f2.TargetId
                WHERE t2.SessionId=$session AND f2.EntryType=0 AND f2.IsMissing=0 AND f2.Size IS NOT NULL
                GROUP BY f2.Size HAVING COUNT(*) > 1)
            ORDER BY f.Size, f.Id;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(ReadFile(reader));
        return result;
    }

    public async Task UpdateFingerprintsAsync(IReadOnlyDictionary<long, ulong> fingerprints, CancellationToken cancellationToken = default)
    {
        if (fingerprints.Count == 0) return;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE Files SET Fingerprint=$fingerprint WHERE Id=$id;";
        command.Parameters.Add("$fingerprint", SqliteType.Integer);
        command.Parameters.Add("$id", SqliteType.Integer);
        command.Prepare();
        foreach (var pair in fingerprints)
        {
            command.Parameters["$fingerprint"].Value = unchecked((long)pair.Value);
            command.Parameters["$id"].Value = pair.Key;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContentExtractionCandidate>> GetContentExtractionCandidatesAsync(
        Guid sessionId,
        IReadOnlyCollection<string> extensions,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (extensions.Count == 0 || limit <= 0) return [];
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var extensionParameters = extensions.Select((_, index) => $"$extension{index}").ToArray();
        command.CommandText = $"""
            SELECT f.Id,f.FullPath,COALESCE(f.Extension,''),f.Size,f.ModifiedAt
            FROM Files f JOIN ScanTargets t ON t.Id=f.TargetId
            WHERE t.SessionId=$session AND f.EntryType=0 AND f.IsMissing=0
              AND (f.ContentStatus=0 OR (f.ContentStatus=2 AND f.ContentAttemptedAt<$retryBefore))
              AND f.Extension IN ({string.Join(',', extensionParameters)})
            ORDER BY CASE WHEN f.Size IS NULL THEN 1 ELSE 0 END, f.Size, f.Id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        command.Parameters.AddWithValue("$retryBefore", FormatDate(DateTimeOffset.UtcNow.AddHours(-1)));
        var normalizedExtensions = extensions.Select(extension => extension.ToLowerInvariant()).ToArray();
        for (var index = 0; index < normalizedExtensions.Length; index++)
            command.Parameters.AddWithValue(extensionParameters[index], normalizedExtensions[index]);

        var result = new List<ContentExtractionCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new ContentExtractionCandidate(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : ParseDate(reader.GetString(4))));
        return result;
    }

    public async Task UpdateExtractedContentsAsync(
        IReadOnlyCollection<ContentIndexUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0) return;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Files SET ContentText=$text, ContentIndexedModifiedAt=$modified,
                ContentError=$error, ContentStatus=$status, ContentAttemptedAt=$attempted
            WHERE Id=$id AND ModifiedAt IS $modified;
            """;
        command.Parameters.Add("$text", SqliteType.Text);
        command.Parameters.Add("$modified", SqliteType.Text);
        command.Parameters.Add("$error", SqliteType.Text);
        command.Parameters.Add("$status", SqliteType.Integer);
        command.Parameters.Add("$id", SqliteType.Integer);
        command.Parameters.Add("$attempted", SqliteType.Text);
        command.Prepare();
        foreach (var update in updates)
        {
            command.Parameters["$text"].Value = (object?)update.Text ?? DBNull.Value;
            command.Parameters["$modified"].Value = update.SourceModifiedAt is null
                ? DBNull.Value : FormatDate(update.SourceModifiedAt.Value);
            command.Parameters["$error"].Value = (object?)update.Error ?? DBNull.Value;
            command.Parameters["$status"].Value = update.Error is null ? 1 : 2;
            command.Parameters["$id"].Value = update.FileId;
            command.Parameters["$attempted"].Value = FormatDate(DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MaintainIndexAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection, string table, string column, string definition, CancellationToken token)
    {
        await using var check = connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$column;";
        check.Parameters.AddWithValue("$column", column);
        var exists = Convert.ToInt64(await check.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (exists) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        await alter.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql, Guid sessionId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static FileEntry ReadFile(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        TargetId = Guid.Parse(reader.GetString(1)),
        Name = reader.GetString(2),
        FullPath = reader.GetString(3),
        ParentPath = reader.GetString(4),
        Extension = reader.IsDBNull(5) ? null : reader.GetString(5),
        Type = (FileEntryType)reader.GetInt32(6),
        Size = reader.IsDBNull(7) ? null : reader.GetInt64(7),
        CreatedAt = reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
        ModifiedAt = reader.IsDBNull(9) ? null : ParseDate(reader.GetString(9)),
        Category = (FileCategory)reader.GetInt32(10),
        Fingerprint = reader.IsDBNull(11) ? null : unchecked((ulong)reader.GetInt64(11)),
        IsMissing = reader.GetBoolean(12)
    };

    private static void AddEntryParameters(SqliteCommand command)
    {
        foreach (var name in new[] { "$target", "$name", "$full", "$parent", "$extension", "$created", "$modified", "$fingerprint", "$scan" })
            command.Parameters.Add(name, SqliteType.Text);
        foreach (var name in new[] { "$type", "$category", "$size" }) command.Parameters.Add(name, SqliteType.Integer);
    }

    private static void SetEntryParameters(SqliteCommand command, FileEntry entry, Guid scanId)
    {
        command.Parameters["$target"].Value = entry.TargetId.ToString();
        command.Parameters["$name"].Value = entry.Name;
        command.Parameters["$full"].Value = entry.FullPath;
        command.Parameters["$parent"].Value = entry.ParentPath;
        command.Parameters["$extension"].Value = (object?)entry.Extension ?? DBNull.Value;
        command.Parameters["$type"].Value = (int)entry.Type;
        command.Parameters["$category"].Value = (int)entry.Category;
        command.Parameters["$size"].Value = (object?)entry.Size ?? DBNull.Value;
        command.Parameters["$created"].Value = entry.CreatedAt is null ? DBNull.Value : FormatDate(entry.CreatedAt.Value);
        command.Parameters["$modified"].Value = entry.ModifiedAt is null ? DBNull.Value : FormatDate(entry.ModifiedAt.Value);
        command.Parameters["$fingerprint"].Value = entry.Fingerprint is null ? DBNull.Value : unchecked((long)entry.Fingerprint.Value);
        command.Parameters["$scan"].Value = scanId.ToString();
    }

    private static string[] DeserializeArray(string value) => JsonSerializer.Deserialize<string[]>(value) ?? [];
    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
