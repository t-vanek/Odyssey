using System.Text.Json;
using Odyssey.Agent;
using Odyssey.Core;

namespace Odyssey.Mcp;

public sealed class OdysseyMcpFacade(
    IOdysseyStore store,
    ISearchService search,
    AgentAccessPolicyService policy,
    AgentOperationService operations)
{
    private Guid? _sessionId;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await store.InitializeAsync(cancellationToken);
        await search.InitializeAsync(cancellationToken);
        await operations.InitializeAsync(cancellationToken);
        _sessionId = (await store.GetSessionsAsync(cancellationToken)).FirstOrDefault()?.Id;
        await search.WarmupAsync(_sessionId, cancellationToken);
    }

    public string GetCapabilities() => Json(new
    {
        server = "Odyssey",
        role = "executor",
        accessLevel = policy.AccessLevel.ToString(),
        approvalAuthority = "Odyssey desktop user",
        aiCanApproveOwnOperations = false,
        tools = new
        {
            read = new[] { "search_files", "get_operation_status" },
            write = Enum.GetNames<AgentOperationKind>(),
            control = new[] { "execute_approved_operation", "cancel_operation" }
        },
        guarantees = new[]
        {
            "immutable-plan-sha256", "guard-pipeline", "human-approval", "expiry", "precondition-recheck", "audit"
        }
    });

    public async Task<string> SearchFilesAsync(string query, int limit, int offset, CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(0, offset);
        var response = await search.SearchAsync(new SearchRequest
        {
            Query = query?.Trim() ?? string.Empty,
            SessionId = _sessionId,
            Limit = limit,
            Offset = offset
        }, cancellationToken);
        return Json(new
        {
            response.TotalReturned,
            response.HasMore,
            elapsedMs = Math.Round(response.Elapsed.TotalMilliseconds, 2),
            results = response.Results.Select(item => new
            {
                fileId = item.FileId,
                item.Name,
                item.FullPath,
                type = item.Type.ToString(),
                category = item.Category.ToString(),
                item.Size,
                item.ModifiedAt,
                confidence = item.Confidence.ToString(),
                rankingScore = Math.Round(Math.Clamp(item.Score, 0, 1), 4),
                evidence = item.MatchEvidence.ToString(),
                item.MatchSnippet,
                item.IsMissing
            })
        });
    }

    public Task<string> PlanAsync(AgentOperationRequest request, CancellationToken cancellationToken) =>
        SerializeAsync(operations.PlanAsync(request, cancellationToken));

    public async Task<string> GetOperationStatusAsync(Guid id, CancellationToken cancellationToken)
    {
        var snapshot = await operations.GetAsync(id, cancellationToken);
        return snapshot is null ? Json(new { error = "Unknown operation plan." }) : Json(snapshot);
    }

    public async Task<string> ExecuteAsync(Guid id, CancellationToken cancellationToken) =>
        Json(await operations.ExecuteApprovedAsync(id, cancellationToken));

    public async Task<string> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        Json(new { planId = id, cancelled = await operations.CancelAsync(id, cancellationToken) });

    private static async Task<string> SerializeAsync<T>(Task<T> value) => Json(await value);
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
