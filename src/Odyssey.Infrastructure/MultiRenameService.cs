using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

internal enum MultiRenameCheckpoint { SourceStaged, DestinationPublished, RollbackCompleted }

public sealed class MultiRenameService : IMultiRenameService
{
    private const int MaximumItems = 10_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MultiRenameUndoState? _lastUndo;

    public FileAccessMode AccessMode { get; set; } = FileAccessMode.ReadOnly;
    public bool CanUndoLastBatch => _lastUndo is { IsUndone: false };
    internal Action<MultiRenameCheckpoint, int>? Checkpoint { get; init; }

    public MultiRenamePlan CreatePlan(MultiRenameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request with
        {
            SourcePaths = request.SourcePaths ?? [],
            Prefix = request.Prefix ?? string.Empty,
            Suffix = request.Suffix ?? string.Empty,
            SearchText = request.SearchText ?? string.Empty,
            ReplacementText = request.ReplacementText ?? string.Empty,
            CounterSeparator = request.CounterSeparator ?? string.Empty,
            DateFormat = request.DateFormat ?? string.Empty,
            ExtensionReplacement = request.ExtensionReplacement ?? string.Empty,
            ManualNames = request.ManualNames ?? new Dictionary<string, string>()
        };
        var globalErrors = new List<string>();
        if (request.SourcePaths.Count == 0) globalErrors.Add("Select at least one item.");
        if (request.SourcePaths.Count > MaximumItems) globalErrors.Add("The batch item limit was exceeded.");
        if (request.CounterPadding is < 1 or > 18) globalErrors.Add("Counter padding must be between 1 and 18.");
        if (request.CounterStep == 0) globalErrors.Add("Counter step cannot be zero.");
        if (request.DateFormat.Length is 0 or > 64) globalErrors.Add("Date format is invalid.");
        if (request.CounterSeparator.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            globalErrors.Add("Counter separator contains invalid filename characters.");

        Regex? regex = null;
        if (request.UseRegex && request.SearchText.Length > 0)
        {
            try
            {
                regex = new Regex(request.SearchText,
                    RegexOptions.CultureInvariant | (request.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase),
                    RegexTimeout);
            }
            catch (ArgumentException exception)
            {
                globalErrors.Add($"Regular expression is invalid: {exception.Message}");
            }
        }

        var caseSensitive = request.CaseSensitivity switch
        {
            FileSystemCaseMode.CaseSensitive => true,
            FileSystemCaseMode.CaseInsensitive => false,
            _ => !OperatingSystem.IsWindows()
        };
        var nameComparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var pathComparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var sources = new List<SourceSnapshot>();
        var seenSources = new HashSet<string>(pathComparer);
        foreach (var sourcePath in request.SourcePaths.Take(MaximumItems))
        {
            try
            {
                var fullPath = Path.GetFullPath(sourcePath);
                if (!seenSources.Add(Path.TrimEndingDirectorySeparator(fullPath)))
                {
                    globalErrors.Add($"The source is selected more than once: {fullPath}");
                    continue;
                }
                FileSystemInfo info = Directory.Exists(fullPath)
                    ? new DirectoryInfo(fullPath)
                    : new FileInfo(fullPath);
                info.Refresh();
                if (!info.Exists)
                {
                    globalErrors.Add($"The source is unavailable: {fullPath}");
                    continue;
                }
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    globalErrors.Add($"Links and reparse points cannot be batch-renamed: {fullPath}");
                    continue;
                }
                if (HasLinkOrReparseAncestor(fullPath))
                {
                    globalErrors.Add($"A source ancestor is a link or reparse point: {fullPath}");
                    continue;
                }
                if (Path.GetDirectoryName(fullPath) is null)
                {
                    globalErrors.Add($"A filesystem root cannot be renamed: {fullPath}");
                    continue;
                }
                sources.Add(new SourceSnapshot(
                    fullPath,
                    info.Name,
                    info is DirectoryInfo ? FileEntryType.Directory : FileEntryType.File,
                    info is FileInfo file ? file.Length : 0,
                    new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                globalErrors.Add($"The source could not be inspected: {exception.Message}");
            }
        }

        var pathComparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var outer = 0; outer < sources.Count; outer++)
        for (var inner = outer + 1; inner < sources.Count; inner++)
        {
            if (!IsDescendant(sources[outer].Path, sources[inner].Path, pathComparison)
                && !IsDescendant(sources[inner].Path, sources[outer].Path, pathComparison)) continue;
            globalErrors.Add("A batch cannot contain both a directory and one of its descendants.");
            outer = sources.Count;
            break;
        }

        var ordered = Sort(sources, request.SortMode, request.SortDescending).ToArray();
        var items = new List<MultiRenamePlanItem>(ordered.Length);
        for (var index = 0; index < ordered.Length; index++)
        {
            var source = ordered[index];
            var counterValue = (long)request.CounterStart + (long)index * request.CounterStep;
            var counter = counterValue is >= int.MinValue and <= int.MaxValue
                ? (int)counterValue
                : request.CounterStart;
            var manual = FindManualName(request.ManualNames, source.Path, pathComparer);
            string proposed;
            var errors = new List<string>();
            try { proposed = manual ?? BuildName(source, request, regex, counter); }
            catch (FormatException)
            {
                proposed = source.Name;
                errors.Add("The date format is invalid.");
            }
            catch (RegexMatchTimeoutException)
            {
                proposed = source.Name;
                errors.Add("The regular expression exceeded its safety timeout.");
            }
            if (counterValue is < int.MinValue or > int.MaxValue)
                errors.Add("The counter exceeds the supported integer range.");
            errors.AddRange(ValidateName(proposed, caseSensitive));
            string destination;
            try { destination = Path.Combine(Path.GetDirectoryName(source.Path)!, proposed); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                destination = source.Path;
                errors.Add(exception.Message);
            }
            items.Add(new MultiRenamePlanItem
            {
                SourcePath = source.Path,
                DestinationPath = destination,
                OriginalName = source.Name,
                ProposedName = proposed,
                Type = source.Type,
                Length = source.Length,
                CreatedAt = source.CreatedAt,
                ModifiedAt = source.ModifiedAt,
                IsManual = manual is not null,
                Errors = errors
            });
        }

        AddCollisionErrors(items, sources, nameComparer, pathComparer, caseSensitive);
        if (items.Count > 0 && items.All(item => !item.HasChange))
            globalErrors.Add("The plan does not change any names.");
        var hash = ComputeHash(request, caseSensitive, items);
        return new MultiRenamePlan
        {
            SchemaVersion = MultiRenamePlan.CurrentSchemaVersion,
            Id = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Request = request,
            IsCaseSensitive = caseSensitive,
            Items = items,
            Errors = globalErrors,
            PlanHashSha256 = hash
        };
    }

    public string ExportPlan(MultiRenamePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return JsonSerializer.Serialize(plan, JsonOptions);
    }

    public MultiRenamePlan ImportPlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Rename plan JSON is empty.");
        MultiRenamePlan imported;
        try
        {
            imported = JsonSerializer.Deserialize<MultiRenamePlan>(json, JsonOptions)
                       ?? throw new InvalidDataException("Rename plan JSON is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Rename plan JSON is invalid.", exception);
        }
        if (imported.SchemaVersion != MultiRenamePlan.CurrentSchemaVersion)
            throw new InvalidDataException("Rename plan schema is unsupported.");
        if (imported.Request is null || imported.Items is null || string.IsNullOrWhiteSpace(imported.PlanHashSha256))
            throw new InvalidDataException("Rename plan is incomplete.");
        var importedHash = ComputeHash(imported.Request, imported.IsCaseSensitive, imported.Items);
        try
        {
            if (imported.PlanHashSha256.Length != 64 || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(imported.PlanHashSha256), Convert.FromHexString(importedHash)))
                throw new InvalidDataException("Rename plan integrity check failed.");
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("Rename plan integrity check failed.", exception);
        }
        var refreshed = CreatePlan(imported.Request);
        if (!string.Equals(refreshed.PlanHashSha256, imported.PlanHashSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The filesystem or rename result changed since the plan was exported.");
        return refreshed;
    }

    public async Task<MultiRenameBatchResult> ExecuteAsync(
        MultiRenamePlan plan,
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(plan);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var fresh = CreatePlan(plan.Request);
            if (!plan.IsValid || !fresh.IsValid
                              || !string.Equals(plan.PlanHashSha256, fresh.PlanHashSha256,
                                  StringComparison.OrdinalIgnoreCase))
                throw new IOException("The rename plan is invalid or the filesystem changed after preview.");
            var changes = fresh.Items.Where(item => item.HasChange).ToArray();
            var states = AllocateTemporaryPaths(fresh.Id, changes);
            try
            {
                for (var index = 0; index < states.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MoveEntry(states[index].Item.SourcePath, states[index].TemporaryPath);
                    states[index] = states[index] with { IsStaged = true };
                    Checkpoint?.Invoke(MultiRenameCheckpoint.SourceStaged, index);
                    progress?.Report(new FileOperationProgress(index + 1, changes.Length * 2L,
                        states[index].Item.SourcePath));
                }
                for (var index = 0; index < states.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MoveEntry(states[index].TemporaryPath, states[index].Item.DestinationPath);
                    states[index] = states[index] with { IsPublished = true };
                    Checkpoint?.Invoke(MultiRenameCheckpoint.DestinationPublished, index);
                    progress?.Report(new FileOperationProgress(changes.Length + index + 1, changes.Length * 2L,
                        states[index].Item.DestinationPath));
                }
            }
            catch (Exception executionError)
            {
                var rollbackErrors = Rollback(states);
                if (rollbackErrors.Count > 0)
                    throw new AggregateException("Batch rename failed and rollback was incomplete.",
                        [executionError, .. rollbackErrors]);
                throw;
            }

            _lastUndo = new MultiRenameUndoState(fresh.Id, changes, false);
            return new MultiRenameBatchResult(fresh.Id, changes.Length, DateTimeOffset.UtcNow, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<MultiRenameBatchResult?> UndoLastBatchAsync(
        IProgress<FileOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastUndo is not { IsUndone: false } undo) return null;
            var batchDestinations = undo.Items.Select(item => item.DestinationPath).ToHashSet(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var item in undo.Items)
            {
                ValidateSnapshot(item.DestinationPath, item);
                if (EntryExists(item.SourcePath) && !batchDestinations.Contains(item.SourcePath))
                    throw new IOException($"The original name is occupied and the batch cannot be undone: {item.SourcePath}");
            }
            var reversed = undo.Items.Select(item => item with
            {
                SourcePath = item.DestinationPath,
                DestinationPath = item.SourcePath,
                OriginalName = item.ProposedName,
                ProposedName = item.OriginalName
            }).ToArray();
            var states = AllocateTemporaryPaths(Guid.NewGuid(), reversed);
            try
            {
                for (var index = 0; index < states.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MoveEntry(states[index].Item.SourcePath, states[index].TemporaryPath);
                    states[index] = states[index] with { IsStaged = true };
                }
                for (var index = 0; index < states.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MoveEntry(states[index].TemporaryPath, states[index].Item.DestinationPath);
                    states[index] = states[index] with { IsPublished = true };
                    progress?.Report(new FileOperationProgress(index + 1, states.Length,
                        states[index].Item.DestinationPath));
                }
            }
            catch (Exception executionError)
            {
                var rollbackErrors = Rollback(states);
                if (rollbackErrors.Count > 0)
                    throw new AggregateException("Batch rename Undo failed and rollback was incomplete.",
                        [executionError, .. rollbackErrors]);
                throw;
            }
            _lastUndo = undo with { IsUndone = true };
            return new MultiRenameBatchResult(undo.BatchId, undo.Items.Count, DateTimeOffset.UtcNow, false);
        }
        finally { _gate.Release(); }
    }

    private static IEnumerable<SourceSnapshot> Sort(
        IEnumerable<SourceSnapshot> items,
        MultiRenameSortMode mode,
        bool descending)
    {
        Func<SourceSnapshot, object> key = mode switch
        {
            MultiRenameSortMode.Extension => item => Path.GetExtension(item.Name),
            MultiRenameSortMode.Created => item => item.CreatedAt,
            MultiRenameSortMode.Modified => item => item.ModifiedAt,
            MultiRenameSortMode.Size => item => item.Length,
            _ => item => item.Name
        };
        var ordered = descending
            ? items.OrderByDescending(key).ThenByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
            : items.OrderBy(key).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
        return ordered.ThenBy(item => item.Path, StringComparer.Ordinal);
    }

    private static string BuildName(
        SourceSnapshot source,
        MultiRenameRequest request,
        Regex? regex,
        int counter)
    {
        var extension = source.Type == FileEntryType.File ? Path.GetExtension(source.Name) : string.Empty;
        var name = source.Type == FileEntryType.File
            ? Path.GetFileNameWithoutExtension(source.Name)
            : source.Name;
        if (request.SearchText.Length > 0)
        {
            name = regex is not null
                ? regex.Replace(name, request.ReplacementText)
                : name.Replace(request.SearchText, request.ReplacementText,
                    request.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        name = request.CaseMode switch
        {
            MultiRenameCaseMode.Lower => name.ToLowerInvariant(),
            MultiRenameCaseMode.Upper => name.ToUpperInvariant(),
            MultiRenameCaseMode.Title => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower()),
            _ => name
        };
        var counterText = counter.ToString($"D{request.CounterPadding}", CultureInfo.InvariantCulture);
        var prefix = ExpandTokens(request.Prefix, source, request.DateFormat, counterText);
        var suffix = ExpandTokens(request.Suffix, source, request.DateFormat, counterText);
        var containsCounterToken = request.Prefix.Contains("{counter}", StringComparison.OrdinalIgnoreCase)
                                   || request.Suffix.Contains("{counter}", StringComparison.OrdinalIgnoreCase);
        var result = prefix + name + suffix;
        if (request.IncludeCounter && !containsCounterToken) result += request.CounterSeparator + counterText;
        if (source.Type == FileEntryType.File)
        {
            if (!request.PreserveExtension)
                extension = NormalizeExtension(request.ExtensionReplacement);
            result += extension;
        }
        return result;
    }

    private static string ExpandTokens(string value, SourceSnapshot source, string dateFormat, string counter) =>
        value.Replace("{counter}", counter, StringComparison.OrdinalIgnoreCase)
            .Replace("{created}", source.CreatedAt.ToString(dateFormat, CultureInfo.CurrentCulture),
                StringComparison.OrdinalIgnoreCase)
            .Replace("{modified}", source.ModifiedAt.ToString(dateFormat, CultureInfo.CurrentCulture),
                StringComparison.OrdinalIgnoreCase);

    private static string NormalizeExtension(string extension)
    {
        var trimmed = extension.Trim();
        if (trimmed.Length == 0) return string.Empty;
        return trimmed.StartsWith('.') ? trimmed : "." + trimmed;
    }

    private static string? FindManualName(
        IReadOnlyDictionary<string, string> manualNames,
        string sourcePath,
        StringComparer comparer)
    {
        foreach (var item in manualNames)
        {
            try
            {
                if (comparer.Equals(Path.GetFullPath(item.Key), sourcePath)) return item.Value;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        }
        return null;
    }

    private static bool HasLinkOrReparseAncestor(string path)
    {
        for (var current = Directory.GetParent(path); current is not null; current = current.Parent)
        {
            current.Refresh();
            if (current.LinkTarget is not null || current.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        return false;
    }

    private static bool IsDescendant(string path, string potentialParent, StringComparison comparison)
    {
        var parent = Path.TrimEndingDirectorySeparator(potentialParent) + Path.DirectorySeparatorChar;
        return path.StartsWith(parent, comparison);
    }

    private static IEnumerable<string> ValidateName(string name, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") yield return "The result name is empty or reserved.";
        if (name.Length > 255) yield return "The result name exceeds 255 characters.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            yield return "The result contains invalid filename characters.";
        if ((OperatingSystem.IsWindows() || !caseSensitive) && (name.EndsWith(' ') || name.EndsWith('.')))
            yield return "The destination filesystem does not accept trailing spaces or periods.";
        var stem = Path.GetFileNameWithoutExtension(name);
        if ((OperatingSystem.IsWindows() || !caseSensitive) && WindowsReservedNames.Contains(stem))
            yield return "The result is a reserved Windows device name.";
    }

    private static void AddCollisionErrors(
        List<MultiRenamePlanItem> items,
        IReadOnlyCollection<SourceSnapshot> sources,
        StringComparer nameComparer,
        StringComparer pathComparer,
        bool caseSensitive)
    {
        var sourcePaths = sources.Select(item => item.Path).ToHashSet(pathComparer);
        foreach (var group in items.GroupBy(item =>
                     (Parent: Path.GetDirectoryName(item.DestinationPath)!, Name: item.ProposedName),
                     new DestinationKeyComparer(pathComparer, nameComparer)))
        {
            if (group.Count() < 2) continue;
            foreach (var item in group.ToArray()) AddItemError(items, item.SourcePath,
                "Another item in the batch has the same result name.");
        }
        foreach (var item in items.Where(item => item.Errors.Count == 0 && item.HasChange).ToArray())
        {
            var parent = Path.GetDirectoryName(item.DestinationPath)!;
            string? collision = null;
            try
            {
                collision = Directory.EnumerateFileSystemEntries(parent)
                    .FirstOrDefault(path => nameComparer.Equals(Path.GetFileName(path), item.ProposedName));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                AddItemError(items, item.SourcePath, $"The destination directory cannot be inspected: {exception.Message}");
                continue;
            }
            if (collision is not null && !sourcePaths.Contains(Path.GetFullPath(collision)))
                AddItemError(items, item.SourcePath, caseSensitive
                    ? "The result collides with an existing filesystem item."
                    : "The result collides with an existing item under case-insensitive comparison.");
        }
    }

    private static void AddItemError(List<MultiRenamePlanItem> items, string sourcePath, string error)
    {
        var index = items.FindIndex(item => string.Equals(item.SourcePath, sourcePath, StringComparison.Ordinal));
        if (index < 0) return;
        items[index] = items[index] with { Errors = [.. items[index].Errors, error] };
    }

    private static RenameState[] AllocateTemporaryPaths(Guid batchId, IReadOnlyList<MultiRenamePlanItem> items)
    {
        var states = new RenameState[items.Count];
        for (var index = 0; index < items.Count; index++)
        {
            var parent = Path.GetDirectoryName(items[index].SourcePath)!;
            var temporary = Path.Combine(parent, $".odyssey-rename-{batchId:N}-{index:D5}");
            if (EntryExists(temporary)) throw new IOException($"A rename staging path already exists: {temporary}");
            states[index] = new RenameState(items[index], temporary, false, false);
        }
        return states;
    }

    private List<Exception> Rollback(RenameState[] states)
    {
        var errors = new List<Exception>();
        foreach (var state in states.Where(item => item.IsPublished).Reverse())
        {
            try
            {
                if (EntryExists(state.Item.DestinationPath) && !EntryExists(state.TemporaryPath))
                    MoveEntry(state.Item.DestinationPath, state.TemporaryPath);
            }
            catch (Exception exception) { errors.Add(exception); }
        }
        foreach (var state in states.Where(item => item.IsStaged).Reverse())
        {
            try
            {
                if (EntryExists(state.TemporaryPath) && !EntryExists(state.Item.SourcePath))
                    MoveEntry(state.TemporaryPath, state.Item.SourcePath);
            }
            catch (Exception exception) { errors.Add(exception); }
        }
        if (errors.Count == 0) Checkpoint?.Invoke(MultiRenameCheckpoint.RollbackCompleted, -1);
        return errors;
    }

    private static void ValidateSnapshot(string path, MultiRenamePlanItem snapshot)
    {
        FileSystemInfo info = snapshot.Type == FileEntryType.Directory
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        info.Refresh();
        if (!info.Exists) throw new IOException($"A renamed item is unavailable: {path}");
        var length = info is FileInfo file ? file.Length : 0;
        if (length != snapshot.Length || info.LastWriteTimeUtc != snapshot.ModifiedAt.UtcDateTime)
            throw new IOException($"A renamed item changed and cannot be undone safely: {path}");
    }

    private static string ComputeHash(
        MultiRenameRequest request,
        bool caseSensitive,
        IReadOnlyList<MultiRenamePlanItem> items)
    {
        var payload = JsonSerializer.Serialize(new HashPayload(request, caseSensitive, items.Select(item => new HashItem(
            item.SourcePath, item.DestinationPath, item.Type, item.Length, item.CreatedAt, item.ModifiedAt,
            item.IsManual, item.Errors)).ToArray()), JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private void EnsureWritable()
    {
        if (AccessMode == FileAccessMode.ReadOnly)
            throw new InvalidOperationException("Odyssey is in read-only mode. Enable file management first.");
    }

    private static bool EntryExists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void MoveEntry(string source, string destination)
    {
        if (File.Exists(source)) File.Move(source, destination, overwrite: false);
        else if (Directory.Exists(source)) Directory.Move(source, destination);
        else throw new FileNotFoundException("The rename source is unavailable.", source);
    }

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private sealed record SourceSnapshot(
        string Path, string Name, FileEntryType Type, long Length,
        DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt);
    private sealed record RenameState(
        MultiRenamePlanItem Item, string TemporaryPath, bool IsStaged, bool IsPublished);
    private sealed record MultiRenameUndoState(
        Guid BatchId, IReadOnlyList<MultiRenamePlanItem> Items, bool IsUndone);
    private sealed record HashPayload(
        MultiRenameRequest Request, bool IsCaseSensitive, IReadOnlyList<HashItem> Items);
    private sealed record HashItem(
        string SourcePath, string DestinationPath, FileEntryType Type, long Length,
        DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt, bool IsManual, IReadOnlyList<string> Errors);

    private sealed class DestinationKeyComparer(StringComparer pathComparer, StringComparer nameComparer)
        : IEqualityComparer<(string Parent, string Name)>
    {
        public bool Equals((string Parent, string Name) x, (string Parent, string Name) y) =>
            pathComparer.Equals(x.Parent, y.Parent) && nameComparer.Equals(x.Name, y.Name);
        public int GetHashCode((string Parent, string Name) value) =>
            HashCode.Combine(pathComparer.GetHashCode(value.Parent), nameComparer.GetHashCode(value.Name));
    }
}
