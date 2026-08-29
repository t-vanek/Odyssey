using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Odyssey.Infrastructure;

namespace Odyssey.Agent;

public sealed class AgentOperationStore
{
    private readonly string _connectionString;

    public AgentOperationStore(ApplicationStorage storage, bool pooling = true)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(storage.DirectoryPath, "agent-operations.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = pooling
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS AgentOperations(
                Id TEXT PRIMARY KEY,
                PlanJson TEXT NOT NULL,
                PlanSha256 TEXT NOT NULL,
                State INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL,
                Error TEXT NULL,
                Result TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AgentOperations_State_Updated
                ON AgentOperations(State, UpdatedAt DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(AgentOperationPlan plan, AgentOperationState state, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AgentOperations(Id, PlanJson, PlanSha256, State, UpdatedAt)
            VALUES($id, $plan, $hash, $state, $updated);
            """;
        command.Parameters.AddWithValue("$id", plan.Id.ToString());
        command.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(plan, JsonOptions));
        command.Parameters.AddWithValue("$hash", plan.PlanSha256);
        command.Parameters.AddWithValue("$state", (int)state);
        command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AgentOperationSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await ExpireOldAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PlanJson, State, UpdatedAt, Error, Result FROM AgentOperations WHERE Id=$id;";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<AgentOperationSnapshot>> GetPendingAsync(CancellationToken cancellationToken = default)
    {
        await ExpireOldAsync(cancellationToken);
        var result = new List<AgentOperationSnapshot>();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT PlanJson, State, UpdatedAt, Error, Result
            FROM AgentOperations
            WHERE State=$pending
            ORDER BY UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$pending", (int)AgentOperationState.PendingApproval);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(Read(reader));
        return result;
    }

    public async Task<bool> ApproveAsync(Guid id, bool strongApproval, CancellationToken cancellationToken = default)
    {
        var snapshot = await GetAsync(id, cancellationToken);
        if (snapshot is null || snapshot.State != AgentOperationState.PendingApproval) return false;
        if (snapshot.Plan.RequiresStrongApproval && !strongApproval) return false;
        return await TransitionAsync(id, AgentOperationState.PendingApproval, AgentOperationState.Approved, null, null, cancellationToken);
    }

    public Task<bool> DenyAsync(Guid id, string? reason = null, CancellationToken cancellationToken = default) =>
        TransitionAsync(id, AgentOperationState.PendingApproval, AgentOperationState.Denied,
            string.IsNullOrWhiteSpace(reason) ? "Denied by the user." : reason.Trim(), null, cancellationToken);

    public Task<bool> TryStartAsync(Guid id, CancellationToken cancellationToken = default) =>
        TransitionAsync(id, AgentOperationState.Approved, AgentOperationState.Executing, null, null, cancellationToken);

    public Task<bool> CompleteAsync(Guid id, string result, CancellationToken cancellationToken = default) =>
        TransitionAsync(id, AgentOperationState.Executing, AgentOperationState.Completed, null, result, cancellationToken);

    public Task<bool> FailAsync(Guid id, string error, CancellationToken cancellationToken = default) =>
        TransitionAsync(id, AgentOperationState.Executing, AgentOperationState.Failed, error, null, cancellationToken);

    public async Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (await TransitionAsync(id, AgentOperationState.PendingApproval, AgentOperationState.Cancelled, "Cancelled.", null, cancellationToken)) return true;
        return await TransitionAsync(id, AgentOperationState.Approved, AgentOperationState.Cancelled, "Cancelled.", null, cancellationToken);
    }

    private async Task<bool> TransitionAsync(
        Guid id,
        AgentOperationState expected,
        AgentOperationState next,
        string? error,
        string? result,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AgentOperations
            SET State=$next, UpdatedAt=$updated, Error=$error, Result=$result
            WHERE Id=$id AND State=$expected;
            """;
        command.Parameters.AddWithValue("$next", (int)next);
        command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)result ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$expected", (int)expected);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task ExpireOldAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE AgentOperations
            SET State=$expired, UpdatedAt=$updated, Error='Approval expired.'
            WHERE State IN ($pending, $approved)
              AND json_extract(PlanJson, '$.expiresAt') < $now;
            """;
        command.Parameters.AddWithValue("$expired", (int)AgentOperationState.Expired);
        command.Parameters.AddWithValue("$pending", (int)AgentOperationState.PendingApproval);
        command.Parameters.AddWithValue("$approved", (int)AgentOperationState.Approved);
        command.Parameters.AddWithValue("$updated", Format(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static AgentOperationSnapshot Read(SqliteDataReader reader)
    {
        var plan = JsonSerializer.Deserialize<AgentOperationPlan>(reader.GetString(0), JsonOptions)
                   ?? throw new InvalidDataException("Stored agent operation is invalid.");
        return new AgentOperationSnapshot(
            plan,
            (AgentOperationState)reader.GetInt32(1),
            Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
