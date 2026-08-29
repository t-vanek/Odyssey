using System.Text;
using System.Text.RegularExpressions;

namespace Odyssey.Core;

public static class FileNamePatternMatcher
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static bool TryCreate(
        DirectoryNameFilter filter,
        out Predicate<string> matcher,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var pattern = filter.Pattern ?? string.Empty;
        if (pattern.Length == 0)
        {
            matcher = static _ => true;
            error = null;
            return true;
        }

        if (filter.Mode == FileNameFilterMode.Contains)
        {
            var comparison = filter.MatchCase
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            matcher = value => value.Contains(pattern, comparison);
            error = null;
            return true;
        }

        try
        {
            var expression = filter.Mode == FileNameFilterMode.Glob
                ? GlobToRegex(pattern)
                : pattern;
            var options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
            if (!filter.MatchCase) options |= RegexOptions.IgnoreCase;
            var regex = new Regex(expression, options, MatchTimeout);
            matcher = value => regex.IsMatch(value);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            matcher = static _ => false;
            error = ex.Message;
            return false;
        }
    }

    private static string GlobToRegex(string pattern)
    {
        var result = new StringBuilder("\\A(?:");
        var alternatives = pattern.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (alternatives.Length == 0) alternatives = [pattern];
        for (var alternativeIndex = 0; alternativeIndex < alternatives.Length; alternativeIndex++)
        {
            if (alternativeIndex > 0) result.Append('|');
            foreach (var character in alternatives[alternativeIndex])
            {
                switch (character)
                {
                    case '*': result.Append(".*"); break;
                    case '?': result.Append('.'); break;
                    default: result.Append(Regex.Escape(character.ToString())); break;
                }
            }
        }
        return result.Append(")\\z").ToString();
    }
}
