using System.Text.RegularExpressions;
using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class ExtensionFileClassifier : IFileClassifier
{
    private static readonly IReadOnlyDictionary<string, FileCategory> Categories = BuildMap();

    public FileCategory Classify(string? extension, FileEntryType type)
    {
        if (type == FileEntryType.Directory || string.IsNullOrWhiteSpace(extension))
            return FileCategory.Other;

        return Categories.TryGetValue(Normalize(extension), out var category) ? category : FileCategory.Other;
    }

    private static string Normalize(string extension) =>
        extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";

    private static IReadOnlyDictionary<string, FileCategory> BuildMap()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        Add(FileCategory.Documents, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".txt", ".rtf", ".csv", ".md");
        Add(FileCategory.Images, ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tif", ".tiff", ".webp", ".svg", ".heic", ".raw");
        Add(FileCategory.Videos, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v");
        Add(FileCategory.Audio, ".mp3", ".wav", ".flac", ".aac", ".ogg", ".m4a", ".wma");
        Add(FileCategory.Archives, ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso");
        Add(FileCategory.SourceCode, ".cs", ".fs", ".vb", ".java", ".kt", ".cpp", ".c", ".h", ".py", ".js", ".ts", ".html", ".css", ".sql", ".json", ".xml", ".yaml", ".yml");
        Add(FileCategory.Executables, ".exe", ".dll", ".msi", ".com", ".bat", ".cmd", ".sh", ".appimage");
        return map;

        void Add(FileCategory category, params string[] extensions)
        {
            foreach (var extension in extensions)
                map[extension] = category;
        }
    }
}

public sealed class ConfigurableExclusionPolicy : IExclusionPolicy
{
    public static readonly IReadOnlyCollection<string> DefaultPatterns =
    [".git", "node_modules", "bin", "obj", ".cache", "System Volume Information", "$RECYCLE.BIN"];

    private readonly IReadOnlyCollection<string> _basePatterns;

    public ConfigurableExclusionPolicy(IEnumerable<string>? basePatterns = null) =>
        _basePatterns = (basePatterns ?? DefaultPatterns).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();

    public bool IsExcluded(string rootPath, string candidatePath, IReadOnlyCollection<string> configuredPatterns)
    {
        var relative = Path.GetRelativePath(rootPath, candidatePath);
        var name = Path.GetFileName(candidatePath);
        return _basePatterns.Concat(configuredPatterns)
            .Any(pattern => Matches(pattern.Trim(), name) || Matches(pattern.Trim(), relative));
    }

    private static bool Matches(string pattern, string value)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase)
                   || value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       .Any(part => string.Equals(part, pattern, StringComparison.OrdinalIgnoreCase));

        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return Regex.IsMatch(value, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
