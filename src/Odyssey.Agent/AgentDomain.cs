using System.Text.Json.Serialization;

namespace Odyssey.Agent;

public enum AgentAccessLevel { ReadOnly, FileManagement, FullAccess }
public enum AgentOperationKind { CreateDirectory, Copy, Move, Rename, Trash, WriteText }
public enum AgentOperationRisk { Low, Medium, High, Critical }
public enum AgentOperationState { PendingApproval, Approved, Denied, Executing, Completed, Failed, Cancelled, Expired }
public enum AgentGuardDecision { Allow, RequireApproval, RequireStrongApproval, Deny }

public sealed record AgentOperationRequest
{
    public required string ClientId { get; init; }
    public required AgentOperationKind Kind { get; init; }
    public string? SourcePath { get; init; }
    public string? DestinationPath { get; init; }
    public string? Name { get; init; }
    public string? Content { get; init; }
    public bool Overwrite { get; init; }
    public string? ExpectedSha256 { get; init; }
}

public sealed record AgentGuardFinding(
    string Guard,
    AgentGuardDecision Decision,
    string Message);

public sealed record AgentOperationPlan
{
    public required Guid Id { get; init; }
    public required AgentOperationRequest Request { get; init; }
    public required string Summary { get; init; }
    public required AgentOperationRisk Risk { get; init; }
    public required IReadOnlyList<AgentGuardFinding> Guards { get; init; }
    public required string PlanSha256 { get; init; }
    public required string PathTopologySha256 { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public long EstimatedBytes { get; init; }
    public int EstimatedItems { get; init; }
    public bool RequiresStrongApproval { get; init; }

    [JsonIgnore]
    public bool IsDenied => Guards.Any(item => item.Decision == AgentGuardDecision.Deny);
}

public sealed record AgentOperationSnapshot(
    AgentOperationPlan Plan,
    AgentOperationState State,
    DateTimeOffset UpdatedAt,
    string? Error = null,
    string? Result = null);

public sealed record AgentExecutionResult(
    Guid PlanId,
    AgentOperationState State,
    string Message,
    string? Result = null);
