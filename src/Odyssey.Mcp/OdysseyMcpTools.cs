using System.ComponentModel;
using ModelContextProtocol.Server;
using Odyssey.Agent;

namespace Odyssey.Mcp;

[McpServerToolType]
public sealed class OdysseyMcpTools(OdysseyMcpFacade facade)
{
    [McpServerTool(Name = "odyssey_get_capabilities")]
    [Description("Returns Odyssey executor capabilities, active access level, approval authority and safety guarantees.")]
    public string GetCapabilities() => facade.GetCapabilities();

    [McpServerTool(Name = "odyssey_search_files")]
    [Description("Searches Odyssey's local index. This is read-only, paginated, and returns explainable match evidence.")]
    public Task<string> SearchFiles(
        [Description("Words remembered from the file name, path, or indexed document content.")] string query,
        [Description("Maximum number of results, from 1 to 50.")] int limit = 20,
        [Description("Zero-based result offset for pagination.")] int offset = 0,
        CancellationToken cancellationToken = default) =>
        facade.SearchFilesAsync(query, limit, offset, cancellationToken);

    [McpServerTool(Name = "odyssey_plan_create_folder")]
    [Description("Creates an immutable guarded plan to create a folder. The operation cannot execute before Odyssey user approval.")]
    public Task<string> PlanCreateFolder(string clientId, string parentPath, string name, CancellationToken cancellationToken = default) =>
        facade.PlanAsync(new AgentOperationRequest
        {
            ClientId = clientId,
            Kind = AgentOperationKind.CreateDirectory,
            DestinationPath = parentPath,
            Name = name
        }, cancellationToken);

    [McpServerTool(Name = "odyssey_plan_copy")]
    [Description("Creates an immutable guarded copy plan. Existing destinations are never overwritten.")]
    public Task<string> PlanCopy(string clientId, string sourcePath, string destinationDirectory, CancellationToken cancellationToken = default) =>
        facade.PlanAsync(new AgentOperationRequest
        {
            ClientId = clientId,
            Kind = AgentOperationKind.Copy,
            SourcePath = sourcePath,
            DestinationPath = destinationDirectory
        }, cancellationToken);

    [McpServerTool(Name = "odyssey_plan_move")]
    [Description("Creates an immutable guarded move plan. Execution requires Odyssey user approval.")]
    public Task<string> PlanMove(string clientId, string sourcePath, string destinationDirectory, CancellationToken cancellationToken = default) =>
        facade.PlanAsync(new AgentOperationRequest
        {
            ClientId = clientId,
            Kind = AgentOperationKind.Move,
            SourcePath = sourcePath,
            DestinationPath = destinationDirectory
        }, cancellationToken);

    [McpServerTool(Name = "odyssey_plan_rename")]
    [Description("Creates an immutable guarded rename plan. The new value must be a file name, not a path.")]
    public Task<string> PlanRename(string clientId, string sourcePath, string newName, CancellationToken cancellationToken = default) =>
        facade.PlanAsync(new AgentOperationRequest
        {
            ClientId = clientId,
            Kind = AgentOperationKind.Rename,
            SourcePath = sourcePath,
            Name = newName
        }, cancellationToken);

    [McpServerTool(Name = "odyssey_plan_trash")]
    [Description("Creates a high-risk guarded plan to move an item to the operating-system trash. Strong user approval is required.")]
    public Task<string> PlanTrash(string clientId, string path, CancellationToken cancellationToken = default) =>
        facade.PlanAsync(new AgentOperationRequest
        {
            ClientId = clientId,
            Kind = AgentOperationKind.Trash,
            SourcePath = path
        }, cancellationToken);

    [McpServerTool(Name = "odyssey_plan_write_text")]
    [Description("Creates a guarded plan to create or replace a UTF-8 text file. Existing files are version-locked at planning time.")]
    public Task<string> PlanWriteText(
        string clientId,
        string destinationPath,
        string content,
        [Description("Must be true to replace an existing file.")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        facade.PlanAsync(new AgentOperationRequest
        {
            ClientId = clientId,
            Kind = AgentOperationKind.WriteText,
            DestinationPath = destinationPath,
            Content = content,
            Overwrite = overwrite
        }, cancellationToken);

    [McpServerTool(Name = "odyssey_get_operation_status")]
    [Description("Returns guard findings, approval state, execution result, or failure for an Odyssey operation plan.")]
    public Task<string> GetOperationStatus(Guid planId, CancellationToken cancellationToken = default) =>
        facade.GetOperationStatusAsync(planId, cancellationToken);

    [McpServerTool(Name = "odyssey_execute_approved_operation")]
    [Description("Executes a plan only after it was approved in Odyssey. Calling this tool cannot approve the plan.")]
    public Task<string> ExecuteApprovedOperation(Guid planId, CancellationToken cancellationToken = default) =>
        facade.ExecuteAsync(planId, cancellationToken);

    [McpServerTool(Name = "odyssey_cancel_operation")]
    [Description("Cancels a pending or approved operation before execution starts.")]
    public Task<string> CancelOperation(Guid planId, CancellationToken cancellationToken = default) =>
        facade.CancelAsync(planId, cancellationToken);
}
