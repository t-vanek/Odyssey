using Microsoft.Extensions.Logging.Abstractions;
using Odyssey.Agent;
using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Tests;

public sealed class AgentOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"odyssey-agent-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadOnlyPolicy_DeniesWritePlan()
    {
        var context = await CreateContextAsync(AgentAccessLevel.ReadOnly);

        var snapshot = await context.Service.PlanAsync(new AgentOperationRequest
        {
            ClientId = "test-client",
            Kind = AgentOperationKind.CreateDirectory,
            DestinationPath = context.ApprovedRoot,
            Name = "new-folder"
        });

        Assert.Equal(AgentOperationState.Denied, snapshot.State);
        Assert.Contains(snapshot.Plan.Guards, item => item.Guard == "AccessModeGuard" && item.Decision == AgentGuardDecision.Deny);
        Assert.False(Directory.Exists(Path.Combine(context.ApprovedRoot, "new-folder")));
    }

    [Fact]
    public async Task ApprovedPlan_CannotExecuteBeforeHumanApproval()
    {
        var context = await CreateContextAsync(AgentAccessLevel.FileManagement);
        var snapshot = await context.Service.PlanAsync(new AgentOperationRequest
        {
            ClientId = "claude-desktop",
            Kind = AgentOperationKind.CreateDirectory,
            DestinationPath = context.ApprovedRoot,
            Name = "approved-folder"
        });

        var beforeApproval = await context.Service.ExecuteApprovedAsync(snapshot.Plan.Id);
        Assert.Equal(AgentOperationState.PendingApproval, beforeApproval.State);
        Assert.False(Directory.Exists(Path.Combine(context.ApprovedRoot, "approved-folder")));

        Assert.True(await context.Service.ApproveAsync(snapshot.Plan.Id, strongApproval: false));
        var completed = await context.Service.ExecuteApprovedAsync(snapshot.Plan.Id);

        Assert.Equal(AgentOperationState.Completed, completed.State);
        Assert.True(Directory.Exists(Path.Combine(context.ApprovedRoot, "approved-folder")));
    }

    [Fact]
    public async Task FullAccessOutsideIndexedLocations_RequiresStrongApproval()
    {
        var context = await CreateContextAsync(AgentAccessLevel.FullAccess);
        var destination = Path.Combine(_root, "outside", "note.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var snapshot = await context.Service.PlanAsync(new AgentOperationRequest
        {
            ClientId = "gpt",
            Kind = AgentOperationKind.WriteText,
            DestinationPath = destination,
            Content = "created by an approved Odyssey operation"
        });

        Assert.Equal(AgentOperationState.PendingApproval, snapshot.State);
        Assert.True(snapshot.Plan.RequiresStrongApproval);
        Assert.False(await context.Service.ApproveAsync(snapshot.Plan.Id, strongApproval: false));
        Assert.True(await context.Service.ApproveAsync(snapshot.Plan.Id, strongApproval: true));

        var completed = await context.Service.ExecuteApprovedAsync(snapshot.Plan.Id);
        Assert.Equal(AgentOperationState.Completed, completed.State);
        Assert.Equal("created by an approved Odyssey operation", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task ChangedFile_InvalidatesApprovedWritePlan()
    {
        var context = await CreateContextAsync(AgentAccessLevel.FileManagement);
        var destination = Path.Combine(context.ApprovedRoot, "work.txt");
        await File.WriteAllTextAsync(destination, "version one");
        var snapshot = await context.Service.PlanAsync(new AgentOperationRequest
        {
            ClientId = "claude",
            Kind = AgentOperationKind.WriteText,
            DestinationPath = destination,
            Content = "AI replacement",
            Overwrite = true
        });
        Assert.True(await context.Service.ApproveAsync(snapshot.Plan.Id, strongApproval: true));
        await File.WriteAllTextAsync(destination, "newer user work");

        var result = await context.Service.ExecuteApprovedAsync(snapshot.Plan.Id);

        Assert.Equal(AgentOperationState.Failed, result.State);
        Assert.Contains("changed after approval", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("newer user work", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task AccessRevokedAfterApproval_PreventsExecutionAndAuditsFailure()
    {
        var context = await CreateContextAsync(AgentAccessLevel.FileManagement);
        var snapshot = await context.Service.PlanAsync(new AgentOperationRequest
        {
            ClientId = "gpt",
            Kind = AgentOperationKind.CreateDirectory,
            DestinationPath = context.ApprovedRoot,
            Name = "must-not-exist"
        });
        Assert.True(await context.Service.ApproveAsync(snapshot.Plan.Id, strongApproval: false));

        context.Policy.AccessLevel = AgentAccessLevel.ReadOnly;
        var result = await context.Service.ExecuteApprovedAsync(snapshot.Plan.Id);
        var audited = await context.Service.GetAsync(snapshot.Plan.Id);

        Assert.Equal(AgentOperationState.Failed, result.State);
        Assert.Contains("read-only", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AgentOperationState.Failed, audited?.State);
        Assert.False(Directory.Exists(Path.Combine(context.ApprovedRoot, "must-not-exist")));
    }

    [Fact]
    public async Task LinkInsertedAfterApproval_CannotRedirectExecution()
    {
        if (OperatingSystem.IsWindows()) return;
        var context = await CreateContextAsync(AgentAccessLevel.FileManagement);
        var approvedParent = Path.Combine(context.ApprovedRoot, "approved-parent");
        var movedParent = Path.Combine(context.ApprovedRoot, "approved-parent-original");
        var outside = Path.Combine(_root, "redirect-target");
        Directory.CreateDirectory(approvedParent);
        Directory.CreateDirectory(outside);
        var snapshot = await context.Service.PlanAsync(new AgentOperationRequest
        {
            ClientId = "claude",
            Kind = AgentOperationKind.CreateDirectory,
            DestinationPath = approvedParent,
            Name = "redirected-folder"
        });
        Assert.True(await context.Service.ApproveAsync(snapshot.Plan.Id, strongApproval: false));

        Directory.Move(approvedParent, movedParent);
        Directory.CreateSymbolicLink(approvedParent, outside);
        var result = await context.Service.ExecuteApprovedAsync(snapshot.Plan.Id);

        Assert.Equal(AgentOperationState.Failed, result.State);
        Assert.Contains("link", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(outside, "redirected-folder")));
    }

    [Fact]
    public async Task SourceFileReplacedAfterApproval_CannotBeTrashed()
    {
        var context = await CreateContextAsync(AgentAccessLevel.FileManagement);
        var source = Path.Combine(context.ApprovedRoot, "important.txt");
        await File.WriteAllTextAsync(source, "approved version");
        var snapshot = await context.Service.PlanAsync(new AgentOperationRequest
        {
            ClientId = "gpt",
            Kind = AgentOperationKind.Trash,
            SourcePath = source
        });
        Assert.True(await context.Service.ApproveAsync(snapshot.Plan.Id, strongApproval: true));
        await File.WriteAllTextAsync(source, "newer user version");

        var result = await context.Service.ExecuteApprovedAsync(snapshot.Plan.Id);

        Assert.Equal(AgentOperationState.Failed, result.State);
        Assert.Contains("changed after approval", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("newer user version", await File.ReadAllTextAsync(source));
    }

    private async Task<TestContext> CreateContextAsync(AgentAccessLevel accessLevel)
    {
        var storage = new ApplicationStorage(Path.Combine(_root, $"data-{Guid.NewGuid():N}"));
        var approvedRoot = Path.Combine(_root, $"approved-{Guid.NewGuid():N}");
        Directory.CreateDirectory(approvedRoot);
        var connections = new SqliteConnectionFactory(storage);
        var store = new SqliteOdysseyStore(connections, NullLogger<SqliteOdysseyStore>.Instance);
        await store.InitializeAsync();
        var session = await store.SaveSessionAsync(new RescueSession
        {
            Id = Guid.NewGuid(),
            Name = "Agent tests",
            Mode = SessionMode.ForensicReadOnly,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await store.SaveTargetAsync(new ScanTarget
        {
            Id = Guid.NewGuid(),
            SessionId = session.Id,
            RootPath = approvedRoot,
            Recursive = true
        });
        var policy = new AgentAccessPolicyService(storage) { AccessLevel = accessLevel };
        var operationStore = new AgentOperationStore(storage);
        var service = new AgentOperationService(
            store,
            operationStore,
            policy,
            storage);
        await service.InitializeAsync();
        return new TestContext(service, policy, approvedRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed record TestContext(
        AgentOperationService Service,
        AgentAccessPolicyService Policy,
        string ApprovedRoot);
}
