using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Agent;

public sealed class AgentOperationService(
    IOdysseyStore odysseyStore,
    AgentOperationStore operations,
    AgentAccessPolicyService policy,
    ApplicationStorage storage)
{
    private const int MaximumTextBytes = 4 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _clientPlanWindows = new(StringComparer.Ordinal);
    private readonly IFileOperationService fileOperations = new SafeFileOperationService(storage)
    {
        AccessMode = FileAccessMode.ManageFiles
    };
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public async Task InitializeAsync(CancellationToken cancellationToken = default) =>
        await operations.InitializeAsync(cancellationToken);

    public async Task<AgentOperationSnapshot> PlanAsync(
        AgentOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateClient(request.ClientId);
        var normalized = await NormalizeAsync(request, cancellationToken);
        var findings = new List<AgentGuardFinding>();
        var accessLevel = policy.AccessLevel;
        var affectedPaths = GetAffectedPaths(normalized).Distinct(PathComparer).ToArray();
        var roots = await GetApprovedRootsAsync(cancellationToken);

        findings.Add(Allow("ClientIdentityGuard", $"Declared MCP client label: {normalized.ClientId}."));
        findings.Add(IsWithinPlanRate(normalized.ClientId)
            ? Allow("RateLimitGuard", "Client planning rate is within the safety limit.")
            : Deny("RateLimitGuard", "The client created too many operation plans in one minute."));

        findings.Add(accessLevel == AgentAccessLevel.ReadOnly
            ? Deny("AccessModeGuard", "The Odyssey AI access level is read-only.")
            : Allow("AccessModeGuard", $"AI access level: {accessLevel}."));

        var outsideApprovedRoots = affectedPaths.Where(path => !IsWithinAny(path, roots)).ToArray();
        if (outsideApprovedRoots.Length > 0)
        {
            findings.Add(accessLevel == AgentAccessLevel.FullAccess
                ? Strong("TargetScopeGuard", "The operation reaches outside indexed search locations and requires strong approval.")
                : Deny("TargetScopeGuard", "File-management access is limited to indexed search locations."));
        }
        else
        {
            findings.Add(Allow("TargetScopeGuard", "All paths are inside approved search locations."));
        }

        var applicationData = Path.GetFullPath(storage.DirectoryPath);
        if (affectedPaths.Any(path => IsSameOrInside(applicationData, path)))
            findings.Add(Deny("ProtectedPathGuard", "Odyssey application data cannot be modified through AI tools."));
        else if (affectedPaths.Any(IsPseudoFileSystemPath))
            findings.Add(Deny("ProtectedPathGuard", "Virtual kernel and device paths cannot be modified through AI tools."));
        else if (affectedPaths.Any(IsSystemProtectedPath))
            findings.Add(Strong("ProtectedPathGuard", "The operation affects an operating-system or security-sensitive path."));
        else
            findings.Add(Allow("ProtectedPathGuard", "No protected Odyssey or system path was detected."));

        var hasReparsePoint = affectedPaths.Any(ContainsReparsePoint);
        if (hasReparsePoint)
            findings.Add(accessLevel == AgentAccessLevel.FullAccess
                ? Strong("CanonicalPathGuard", "A symbolic link or reparse point occurs in an affected path.")
                : Deny("CanonicalPathGuard", "Linked paths require full access and strong approval."));
        else
            findings.Add(Allow("CanonicalPathGuard", "Paths are canonical and contain no detected link boundary."));

        AddConflictFindings(normalized, findings);
        findings.Add(Approval("PromptInjectionGuard", "Tool arguments and document text are treated as untrusted input; human approval remains authoritative."));
        var (items, bytes) = MeasureImpact(normalized.SourcePath);
        if (items > 1_000 || bytes > 1024L * 1024 * 1024)
            findings.Add(Strong("BulkOperationGuard", $"Large operation: approximately {items:N0} items and {bytes:N0} bytes."));
        else
            findings.Add(Allow("BulkOperationGuard", $"Estimated impact: {items:N0} items and {bytes:N0} bytes."));

        var risk = DetermineRisk(normalized.Kind, findings, items, bytes);
        findings.Add(risk >= AgentOperationRisk.High
            ? Strong("ApprovalGuard", "Risk policy requires strong one-time user approval in Odyssey.")
            : Approval("ApprovalGuard", "A one-time user approval in Odyssey is required."));
        var now = DateTimeOffset.UtcNow;
        var planWithoutHash = new AgentOperationPlan
        {
            Id = Guid.NewGuid(),
            Request = normalized,
            Summary = CreateSummary(normalized),
            Risk = risk,
            Guards = findings,
            PlanSha256 = string.Empty,
            PathTopologySha256 = ComputePathTopologyHash(affectedPaths),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(10),
            EstimatedBytes = bytes,
            EstimatedItems = items,
            RequiresStrongApproval = risk >= AgentOperationRisk.High || findings.Any(item => item.Decision == AgentGuardDecision.RequireStrongApproval)
        };
        var plan = planWithoutHash with { PlanSha256 = ComputePlanHash(planWithoutHash) };
        var state = plan.IsDenied ? AgentOperationState.Denied : AgentOperationState.PendingApproval;
        await operations.SaveAsync(plan, state, cancellationToken);
        return new AgentOperationSnapshot(plan, state, now,
            plan.IsDenied ? string.Join(" ", findings.Where(item => item.Decision == AgentGuardDecision.Deny).Select(item => item.Message)) : null);
    }

    public Task<IReadOnlyList<AgentOperationSnapshot>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        operations.GetPendingAsync(cancellationToken);

    public Task<AgentOperationSnapshot?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        operations.GetAsync(id, cancellationToken);

    public Task<bool> ApproveAsync(Guid id, bool strongApproval, CancellationToken cancellationToken = default) =>
        operations.ApproveAsync(id, strongApproval, cancellationToken);

    public Task<bool> DenyAsync(Guid id, string? reason = null, CancellationToken cancellationToken = default) =>
        operations.DenyAsync(id, reason, cancellationToken);

    public Task<bool> CancelAsync(Guid id, CancellationToken cancellationToken = default) =>
        operations.CancelAsync(id, cancellationToken);

    public async Task<AgentExecutionResult> ExecuteApprovedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var snapshot = await operations.GetAsync(id, cancellationToken);
        if (snapshot is null) return new AgentExecutionResult(id, AgentOperationState.Failed, "Unknown operation plan.");
        if (snapshot.State != AgentOperationState.Approved)
            return new AgentExecutionResult(id, snapshot.State, $"Operation is {snapshot.State}; approval is required before execution.");
        if (!await operations.TryStartAsync(id, cancellationToken))
            return new AgentExecutionResult(id, AgentOperationState.Failed, "The operation was already claimed or its approval expired.");

        try
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(snapshot.Plan.PlanSha256),
                    Convert.FromHexString(ComputePlanHash(snapshot.Plan with { PlanSha256 = string.Empty }))))
                throw new InvalidDataException("The immutable operation plan failed integrity verification.");
            if (policy.AccessLevel == AgentAccessLevel.ReadOnly)
                throw new InvalidOperationException("AI access was switched to read-only after approval.");
            if (policy.AccessLevel == AgentAccessLevel.FileManagement)
            {
                var roots = await GetApprovedRootsAsync(cancellationToken);
                if (GetAffectedPaths(snapshot.Plan.Request).Any(path => !IsWithinAny(path, roots)))
                    throw new InvalidOperationException("AI access no longer permits paths outside indexed locations.");
            }
            var currentTopology = ComputePathTopologyHash(GetAffectedPaths(snapshot.Plan.Request));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(snapshot.Plan.PathTopologySha256),
                    Convert.FromHexString(currentTopology)))
                throw new IOException("A symbolic-link boundary changed after approval; create a new operation plan.");
            ValidatePreconditions(snapshot.Plan);
            var result = await ExecuteCoreAsync(snapshot.Plan, cancellationToken);
            await operations.CompleteAsync(id, result, cancellationToken);
            return new AgentExecutionResult(id, AgentOperationState.Completed, "Operation completed and verified.", result);
        }
        catch (Exception ex)
        {
            await operations.FailAsync(id, ex.Message, CancellationToken.None);
            return new AgentExecutionResult(id, AgentOperationState.Failed, ex.Message);
        }
    }

    private async Task<string> ExecuteCoreAsync(AgentOperationPlan plan, CancellationToken cancellationToken)
    {
        var request = plan.Request;
        FileOperationRecord? record = request.Kind switch
        {
            AgentOperationKind.CreateDirectory => await fileOperations.CreateDirectoryAsync(request.DestinationPath!, request.Name!, cancellationToken),
            AgentOperationKind.Copy => await fileOperations.CopyAsync(request.SourcePath!, request.DestinationPath!, cancellationToken: cancellationToken),
            AgentOperationKind.Move => await fileOperations.MoveAsync(request.SourcePath!, request.DestinationPath!, cancellationToken: cancellationToken),
            AgentOperationKind.Rename => await fileOperations.RenameAsync(request.SourcePath!, request.Name!, cancellationToken),
            AgentOperationKind.Trash => await fileOperations.TrashAsync(request.SourcePath!, cancellationToken),
            AgentOperationKind.WriteText => null,
            _ => throw new ArgumentOutOfRangeException()
        };

        if (request.Kind == AgentOperationKind.WriteText)
        {
            var written = await WriteTextAtomicallyAsync(plan, cancellationToken);
            return JsonSerializer.Serialize(new { path = written, verified = true });
        }

        if (record is null) throw new InvalidOperationException("The operation did not produce a result.");
        return JsonSerializer.Serialize(new
        {
            operationId = record.Id,
            kind = record.Kind.ToString(),
            source = record.SourcePath,
            destination = record.DestinationPath,
            canUndo = record.CanUndo
        });
    }

    private async Task<string> WriteTextAtomicallyAsync(AgentOperationPlan plan, CancellationToken cancellationToken)
    {
        var request = plan.Request;
        var destination = request.DestinationPath!;
        var parent = Path.GetDirectoryName(destination) ?? throw new IOException("A file-system root cannot be written as a file.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
        VerifyExpectedHash(destination, request.ExpectedSha256);

        var backupDirectory = Path.Combine(storage.DirectoryPath, "agent-backups", plan.Id.ToString("N"));
        Directory.CreateDirectory(backupDirectory);
        if (File.Exists(destination)) File.Copy(destination, Path.Combine(backupDirectory, Path.GetFileName(destination)), overwrite: false);

        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.odyssey-write");
        try
        {
            await File.WriteAllTextAsync(temporary, request.Content!, new UTF8Encoding(false), cancellationToken);
            await using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                _ = await SHA256.HashDataAsync(stream, cancellationToken);
            File.Move(temporary, destination, overwrite: request.Overwrite);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
        return destination;
    }

    private static async Task<AgentOperationRequest> NormalizeAsync(AgentOperationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = string.IsNullOrWhiteSpace(request.SourcePath) ? null : Path.GetFullPath(request.SourcePath);
        var destination = string.IsNullOrWhiteSpace(request.DestinationPath) ? null : Path.GetFullPath(request.DestinationPath);
        var name = string.IsNullOrWhiteSpace(request.Name) ? null : request.Name.Trim();
        switch (request.Kind)
        {
            case AgentOperationKind.CreateDirectory:
                RequireDirectory(destination, "Destination parent folder");
                ValidateSimpleName(name);
                break;
            case AgentOperationKind.Copy:
            case AgentOperationKind.Move:
                RequireEntry(source, "Source");
                RequireDirectory(destination, "Destination folder");
                break;
            case AgentOperationKind.Rename:
                RequireEntry(source, "Source");
                ValidateSimpleName(name);
                break;
            case AgentOperationKind.Trash:
                RequireEntry(source, "Source");
                break;
            case AgentOperationKind.WriteText:
                if (destination is null) throw new ArgumentException("Destination file is required.");
                if (request.Content is null) throw new ArgumentException("Text content is required.");
                if (Encoding.UTF8.GetByteCount(request.Content) > MaximumTextBytes)
                    throw new ArgumentException($"Text writes are limited to {MaximumTextBytes:N0} UTF-8 bytes per operation.");
                if (Directory.Exists(destination)) throw new IOException("The destination is a directory.");
                if (File.Exists(destination) && !request.Overwrite) throw new IOException("The destination exists; explicitly request overwrite.");
                break;
        }

        string? expected = request.ExpectedSha256;
        if (request.Kind == AgentOperationKind.WriteText && File.Exists(destination))
            expected = await ComputeFileHashAsync(destination!, cancellationToken);
        else if (request.Kind is AgentOperationKind.Copy or AgentOperationKind.Move or AgentOperationKind.Rename or AgentOperationKind.Trash && File.Exists(source))
            expected = await ComputeFileHashAsync(source!, cancellationToken);
        return request with { SourcePath = source, DestinationPath = destination, Name = name, ExpectedSha256 = expected };
    }

    private static void AddConflictFindings(AgentOperationRequest request, ICollection<AgentGuardFinding> findings)
    {
        string? target = request.Kind switch
        {
            AgentOperationKind.CreateDirectory => Path.Combine(request.DestinationPath!, request.Name!),
            AgentOperationKind.Copy or AgentOperationKind.Move => Path.Combine(request.DestinationPath!, Path.GetFileName(request.SourcePath!)),
            AgentOperationKind.Rename => Path.Combine(Path.GetDirectoryName(request.SourcePath!)!, request.Name!),
            AgentOperationKind.WriteText => request.DestinationPath,
            _ => null
        };
        if (target is not null && (File.Exists(target) || Directory.Exists(target)) && request.Kind != AgentOperationKind.WriteText)
            findings.Add(Deny("ConflictGuard", $"An item already exists at the destination: {target}"));
        else if (request.Kind == AgentOperationKind.WriteText && File.Exists(target))
            findings.Add(Strong("ConflictGuard", "An existing file will be replaced after version verification."));
        else
            findings.Add(Allow("ConflictGuard", "No destination conflict was detected."));
    }

    private async Task<IReadOnlyList<string>> GetApprovedRootsAsync(CancellationToken cancellationToken)
    {
        var session = (await odysseyStore.GetSessionsAsync(cancellationToken)).FirstOrDefault();
        if (session is null) return [];
        return (await odysseyStore.GetTargetsAsync(session.Id, cancellationToken))
            .Select(target => Path.GetFullPath(target.RootPath)).ToArray();
    }

    private static IReadOnlyList<string> GetAffectedPaths(AgentOperationRequest request)
    {
        var paths = new List<string>();
        if (request.SourcePath is not null) paths.Add(request.SourcePath);
        if (request.DestinationPath is not null) paths.Add(request.DestinationPath);
        if (request.Kind == AgentOperationKind.CreateDirectory)
            paths.Add(Path.Combine(request.DestinationPath!, request.Name!));
        if (request.Kind is AgentOperationKind.Copy or AgentOperationKind.Move)
            paths.Add(Path.Combine(request.DestinationPath!, Path.GetFileName(request.SourcePath!)));
        if (request.Kind == AgentOperationKind.Rename)
            paths.Add(Path.Combine(Path.GetDirectoryName(request.SourcePath!)!, request.Name!));
        return paths;
    }

    private static AgentOperationRisk DetermineRisk(
        AgentOperationKind kind,
        IReadOnlyCollection<AgentGuardFinding> findings,
        int items,
        long bytes)
    {
        if (findings.Any(item => item.Guard == "ProtectedPathGuard" && item.Decision == AgentGuardDecision.RequireStrongApproval))
            return AgentOperationRisk.Critical;
        if (findings.Any(item => item.Decision == AgentGuardDecision.RequireStrongApproval) || kind == AgentOperationKind.Trash || items > 1_000 || bytes > 1024L * 1024 * 1024)
            return AgentOperationRisk.High;
        return kind is AgentOperationKind.Move or AgentOperationKind.Rename or AgentOperationKind.WriteText
            ? AgentOperationRisk.Medium
            : AgentOperationRisk.Low;
    }

    private static (int Items, long Bytes) MeasureImpact(string? path)
    {
        if (path is null) return (1, 0);
        if (File.Exists(path)) return (1, new FileInfo(path).Length);
        if (!Directory.Exists(path)) return (1, 0);
        var items = 1;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(10_001))
            {
                items++;
                try { bytes += new FileInfo(file).Length; } catch { }
            }
        }
        catch { return (Math.Max(items, 1_001), bytes); }
        return (items, bytes);
    }

    private static void ValidatePreconditions(AgentOperationPlan plan)
    {
        var request = plan.Request;
        if (request.Kind is AgentOperationKind.Copy or AgentOperationKind.Move or AgentOperationKind.Rename or AgentOperationKind.Trash)
        {
            RequireEntry(request.SourcePath, "Source");
            if (File.Exists(request.SourcePath)) VerifyExpectedHash(request.SourcePath!, request.ExpectedSha256);
            var currentImpact = MeasureImpact(request.SourcePath);
            if (currentImpact.Items != plan.EstimatedItems || currentImpact.Bytes != plan.EstimatedBytes)
                throw new IOException("The approved source changed in size or item count; create a new operation plan.");
        }
        if (request.Kind is AgentOperationKind.Copy or AgentOperationKind.Move or AgentOperationKind.CreateDirectory)
            RequireDirectory(request.DestinationPath, "Destination folder");
        if (request.Kind == AgentOperationKind.WriteText)
            VerifyExpectedHash(request.DestinationPath!, request.ExpectedSha256);
        if (request.Kind is AgentOperationKind.Copy or AgentOperationKind.Move)
        {
            var target = Path.Combine(request.DestinationPath!, Path.GetFileName(request.SourcePath!));
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Destination conflict appeared after approval.");
        }
    }

    private static void VerifyExpectedHash(string path, string? expected)
    {
        if (!File.Exists(path))
        {
            if (expected is not null) throw new IOException("The approved source version no longer exists.");
            return;
        }
        if (expected is null) throw new IOException("An existing file cannot be overwritten without an approved version hash.");
        using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The file changed after approval; create a new operation plan.");
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string ComputePlanHash(AgentOperationPlan plan)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            plan.Id,
            plan.Request,
            plan.Summary,
            plan.Risk,
            plan.Guards,
            plan.PathTopologySha256,
            plan.CreatedAt,
            plan.ExpiresAt,
            plan.EstimatedBytes,
            plan.EstimatedItems,
            plan.RequiresStrongApproval
        });
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string CreateSummary(AgentOperationRequest request) => request.Kind switch
    {
        AgentOperationKind.CreateDirectory => $"Create folder '{request.Name}' in {request.DestinationPath}",
        AgentOperationKind.Copy => $"Copy {request.SourcePath} to {request.DestinationPath}",
        AgentOperationKind.Move => $"Move {request.SourcePath} to {request.DestinationPath}",
        AgentOperationKind.Rename => $"Rename {request.SourcePath} to {request.Name}",
        AgentOperationKind.Trash => $"Move {request.SourcePath} to trash",
        AgentOperationKind.WriteText => $"{(File.Exists(request.DestinationPath) ? "Replace" : "Create")} text file {request.DestinationPath}",
        _ => request.Kind.ToString()
    };

    private static bool IsWithinAny(string path, IReadOnlyList<string> roots) => roots.Any(root => IsSameOrInside(root, path));
    private static bool IsSameOrInside(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return string.Equals(normalizedRoot, normalizedCandidate, PathComparison) ||
               normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, PathComparison);
    }

    private static bool ContainsReparsePoint(string path)
    {
        var current = File.Exists(path) ? new FileInfo(path).Directory : new DirectoryInfo(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        while (current is not null)
        {
            try { if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return true; }
            catch { }
            current = current.Parent;
        }
        try { return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return false; }
    }

    private static string ComputePathTopologyHash(IEnumerable<string> paths)
    {
        var topology = new List<string>();
        foreach (var path in paths.Distinct(PathComparer).OrderBy(value => value, PathComparer))
        {
            var fullPath = Path.GetFullPath(path);
            topology.Add($"path:{fullPath}");
            if (File.Exists(fullPath)) AddLinkTopology(new FileInfo(fullPath), topology);

            var current = File.Exists(fullPath)
                ? new FileInfo(fullPath).Directory
                : new DirectoryInfo(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath) ?? fullPath);
            while (current is not null)
            {
                AddLinkTopology(current, topology);
                current = current.Parent;
            }
        }

        topology.Sort(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', topology)))).ToLowerInvariant();
    }

    private static void AddLinkTopology(FileSystemInfo entry, ICollection<string> topology)
    {
        try
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0) return;
            var resolved = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? entry.LinkTarget ?? "unresolved";
            topology.Add($"link:{Path.GetFullPath(entry.FullName)}->{resolved}");
        }
        catch (Exception ex)
        {
            topology.Add($"link:{Path.GetFullPath(entry.FullName)}->error:{ex.GetType().Name}");
        }
    }

    private static bool IsPseudoFileSystemPath(string path)
    {
        if (!OperatingSystem.IsLinux()) return false;
        return new[] { "/proc", "/sys", "/dev" }.Any(root => IsSameOrInside(root, path));
    }

    private static bool IsSystemProtectedPath(string path)
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddIfPresent(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        }
        else if (OperatingSystem.IsLinux())
        {
            roots.AddRange(["/etc", "/usr", "/bin", "/sbin", "/boot", "/root"]);
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) roots.AddRange([Path.Combine(profile, ".ssh"), Path.Combine(profile, ".gnupg")]);
        return roots.Any(root => IsSameOrInside(root, path));
    }

    private static void AddIfPresent(ICollection<string> roots, string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) roots.Add(path);
    }

    private static void ValidateClient(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 100)
            throw new ArgumentException("A short MCP client identifier is required.", nameof(clientId));
    }

    private bool IsWithinPlanRate(string clientId)
    {
        var window = _clientPlanWindows.GetOrAdd(clientId, _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            var cutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
            while (window.Count > 0 && window.Peek() < cutoff) window.Dequeue();
            if (window.Count >= 30) return false;
            window.Enqueue(DateTimeOffset.UtcNow);
            return true;
        }
    }

    private static void ValidateSimpleName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("A valid name without a path is required.");
    }

    private static void RequireEntry(string? path, string label)
    {
        if (path is null || (!File.Exists(path) && !Directory.Exists(path))) throw new FileNotFoundException($"{label} is unavailable.", path);
    }

    private static void RequireDirectory(string? path, string label)
    {
        if (path is null || !Directory.Exists(path)) throw new DirectoryNotFoundException($"{label} is unavailable: {path}");
    }

    private static AgentGuardFinding Allow(string guard, string message) => new(guard, AgentGuardDecision.Allow, message);
    private static AgentGuardFinding Approval(string guard, string message) => new(guard, AgentGuardDecision.RequireApproval, message);
    private static AgentGuardFinding Strong(string guard, string message) => new(guard, AgentGuardDecision.RequireStrongApproval, message);
    private static AgentGuardFinding Deny(string guard, string message) => new(guard, AgentGuardDecision.Deny, message);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
