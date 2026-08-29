using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using Microsoft.Win32;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

/// <summary>
/// Reads only search terms that the operating system has already persisted for
/// the current user. Source data is never modified or copied to Odyssey's DB.
/// </summary>
public sealed class SystemSearchHistoryService : ISystemSearchHistoryService
{
    private const int MaximumQueries = 200;
    private const int MaximumQueryLength = 240;
    private readonly Func<IReadOnlyList<string>> _loader;
    private string[] _queries = [];

    public SystemSearchHistoryService() : this(LoadPlatformQueries) { }

    internal SystemSearchHistoryService(Func<IReadOnlyList<string>> loader) => _loader = loader;

    public async Task WarmupAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await Task.Run(_loader, CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
        _queries = NormalizeQueries(loaded).Take(MaximumQueries).ToArray();
    }

    public IReadOnlyList<string> Suggest(string query, int limit)
    {
        var prefix = NormalizeQuery(query);
        if (prefix.Length < 2 || limit <= 0) return [];
        return _queries
            .Where(item => item.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            .Take(Math.Clamp(limit, 1, 12))
            .ToArray();
    }

    private static IReadOnlyList<string> LoadPlatformQueries()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ReadWindowsExplorerQueries();
            if (OperatingSystem.IsLinux()) return ReadLinuxSearchUris();
        }
        catch
        {
            // Search history is optional and must never make startup fail.
        }
        return [];
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<string> ReadWindowsExplorerQueries()
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\WordWheelQuery";
        using var root = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        if (root is null) return [];
        var results = new List<string>();
        ReadWindowsKey(root, results, depth: 0);
        return results;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadWindowsKey(RegistryKey key, List<string> output, int depth)
    {
        var values = new Dictionary<int, string>();
        foreach (var name in key.GetValueNames())
        {
            if (!int.TryParse(name, out var index) || key.GetValue(name) is not byte[] bytes) continue;
            var value = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            if (!string.IsNullOrWhiteSpace(value)) values[index] = value;
        }

        if (key.GetValue("MRUListEx") is byte[] order)
        {
            for (var offset = 0; offset + sizeof(int) <= order.Length; offset += sizeof(int))
            {
                var index = BinaryPrimitives.ReadInt32LittleEndian(order.AsSpan(offset, sizeof(int)));
                if (index < 0) break;
                if (values.Remove(index, out var value)) output.Add(value);
            }
        }
        foreach (var value in values.OrderBy(pair => pair.Key).Select(pair => pair.Value)) output.Add(value);

        if (depth >= 3) return;
        foreach (var childName in key.GetSubKeyNames())
        {
            using var child = key.OpenSubKey(childName, writable: false);
            if (child is not null) ReadWindowsKey(child, output, depth + 1);
        }
    }

    private static IReadOnlyList<string> ReadLinuxSearchUris()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome))
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var candidates = new[]
        {
            Path.Combine(dataHome, "recently-used.xbel"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".recently-used.xbel")
        };
        return NormalizeQueries(candidates.SelectMany(ReadSearchUrisFromXbel)).ToArray();
    }

    internal static IReadOnlyList<string> ReadSearchUrisFromXbel(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 16 * 1024 * 1024) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024
            });
            var results = new List<string>();
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "bookmark") continue;
                if (TryExtractSearchQuery(reader.GetAttribute("href"), out var query)) results.Add(query);
            }
            return results;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }
    }

    internal static bool TryExtractSearchQuery(string? value, out string query)
    {
        query = string.Empty;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme is not ("baloosearch" or "filenamesearch" or "search")) return false;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0) continue;
            var name = part[..separator];
            if (name is not ("query" or "search" or "q" or "pattern")) continue;
            try { query = NormalizeQuery(Uri.UnescapeDataString(part[(separator + 1)..].Replace('+', ' '))); }
            catch (UriFormatException) { return false; }
            return query.Length >= 2;
        }
        return false;
    }

    private static IEnumerable<string> NormalizeQueries(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var value in values)
        {
            var normalized = NormalizeQuery(value);
            if (normalized.Length is >= 2 and <= MaximumQueryLength && seen.Add(normalized)) yield return normalized;
        }
    }

    private static string NormalizeQuery(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
