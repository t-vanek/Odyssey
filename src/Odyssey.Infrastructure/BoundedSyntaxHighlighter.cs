using Odyssey.Core;

namespace Odyssey.Infrastructure;

public sealed class BoundedSyntaxHighlighter(int maximumSpans = 4096) : IQuickViewSyntaxHighlighter
{
    private static readonly HashSet<string> CSharpKeywords = Words(
        "abstract as async await base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params partial private protected public readonly record ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield");
    private static readonly HashSet<string> JavaScriptKeywords = Words(
        "as async await break case catch class const continue debugger default delete do else enum export extends false finally for from function get if implements import in instanceof interface let new null of package private protected public return set static super switch this throw true try typeof undefined var void while with yield");
    private static readonly HashSet<string> CFamilyKeywords = Words(
        "alignas alignof asm auto bool break case catch char class const constexpr continue default delete do double else enum explicit export extern false float for friend goto if inline int interface long namespace new noexcept nullptr operator override private protected public register restrict return short signed sizeof static struct switch template this throw true try typedef typename union unsigned using virtual void volatile while");
    private static readonly HashSet<string> RustKeywords = Words(
        "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait true type unsafe use where while");
    private static readonly HashSet<string> GoKeywords = Words(
        "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var true false nil");
    private static readonly HashSet<string> PythonKeywords = Words(
        "and as assert async await break case class continue def del elif else except False finally for from global if import in is lambda match None nonlocal not or pass raise return True try while with yield");
    private static readonly HashSet<string> ShellKeywords = Words(
        "case do done elif else esac fi for function if in select then time until while export local readonly declare typeset true false");
    private static readonly HashSet<string> PowerShellKeywords = Words(
        "begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function hidden if in inlinescript parallel param process return sequence static switch throw trap try until using var while workflow true false null");
    private static readonly HashSet<string> SqlKeywords = Words(
        "add alter and as asc begin between by case check column commit constraint create database default delete desc distinct drop else end exists foreign from full grant group having if in index inner insert into is join key left like limit not null on or order outer primary references right rollback row select set table then union unique update values view when where with");
    private static readonly HashSet<string> JsonLiterals = Words("true false null");
    private static readonly HashSet<string> YamlLiterals = Words("true false null yes no on off");
    private readonly int _maximumSpans = maximumSpans > 0
        ? maximumSpans
        : throw new ArgumentOutOfRangeException(nameof(maximumSpans));

    public QuickViewSyntaxResult Highlight(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        cancellationToken.ThrowIfCancellationRequested();
        var format = ResolveFormat(path);
        if (format is null || content.Length == 0) return new QuickViewSyntaxResult(null, []);
        var collector = new SpanCollector(_maximumSpans);
        switch (format.Value.Family)
        {
            case SyntaxFamily.CLike:
                ScanCode(content, format.Value.Keywords!, collector, cancellationToken,
                    hashComments: false, powerShell: false);
                break;
            case SyntaxFamily.HashCode:
                ScanCode(content, format.Value.Keywords!, collector, cancellationToken,
                    hashComments: true, powerShell: format.Value.Language == "PowerShell");
                break;
            case SyntaxFamily.Json:
                ScanJson(content, collector, cancellationToken);
                break;
            case SyntaxFamily.Markup:
                ScanMarkup(content, collector, cancellationToken);
                break;
            case SyntaxFamily.Properties:
                ScanProperties(content, collector, cancellationToken,
                    format.Value.Language == "TOML");
                break;
            case SyntaxFamily.Css:
                ScanCss(content, collector, cancellationToken);
                break;
            case SyntaxFamily.Sql:
                ScanCode(content, SqlKeywords, collector, cancellationToken,
                    hashComments: false, powerShell: false, sqlComments: true);
                break;
            case SyntaxFamily.Markdown:
                ScanMarkdown(content, collector, cancellationToken);
                break;
        }
        return new QuickViewSyntaxResult(format.Value.Language, collector.Spans, collector.IsTruncated);
    }

    private static void ScanCode(
        string text,
        HashSet<string> keywords,
        SpanCollector spans,
        CancellationToken token,
        bool hashComments,
        bool powerShell,
        bool sqlComments = false)
    {
        for (var index = 0; index < text.Length && !spans.IsFull;)
        {
            CheckCancellation(index, token);
            var current = text[index];
            if (hashComments && current == '#' && (!powerShell || index + 1 >= text.Length || text[index + 1] != '>'))
            {
                var end = LineEnd(text, index);
                spans.Add(index, end - index, QuickViewSyntaxKind.Comment);
                index = end;
                continue;
            }
            if (powerShell && index + 1 < text.Length && current == '<' && text[index + 1] == '#')
            {
                var end = FindTerminator(text, index + 2, "#>");
                spans.Add(index, end - index, QuickViewSyntaxKind.Comment);
                index = end;
                continue;
            }
            if (!hashComments && index + 1 < text.Length && current == '/' && text[index + 1] == '/')
            {
                var end = LineEnd(text, index);
                spans.Add(index, end - index, QuickViewSyntaxKind.Comment);
                index = end;
                continue;
            }
            if (sqlComments && index + 1 < text.Length && current == '-' && text[index + 1] == '-')
            {
                var end = LineEnd(text, index);
                spans.Add(index, end - index, QuickViewSyntaxKind.Comment);
                index = end;
                continue;
            }
            if (index + 1 < text.Length && current == '/' && text[index + 1] == '*')
            {
                var end = FindTerminator(text, index + 2, "*/");
                spans.Add(index, end - index, QuickViewSyntaxKind.Comment);
                index = end;
                continue;
            }
            if (current is '\'' or '"' or '`')
            {
                var end = ScanQuoted(text, index, current);
                spans.Add(index, end - index, QuickViewSyntaxKind.String);
                index = end;
                continue;
            }
            if (powerShell && current == '$' && index + 1 < text.Length && IsIdentifierStart(text[index + 1]))
            {
                var end = ScanIdentifier(text, index + 1);
                spans.Add(index, end - index, QuickViewSyntaxKind.Property);
                index = end;
                continue;
            }
            if (char.IsDigit(current))
            {
                var end = ScanNumber(text, index);
                spans.Add(index, end - index, QuickViewSyntaxKind.Number);
                index = end;
                continue;
            }
            if (IsIdentifierStart(current))
            {
                var end = ScanIdentifier(text, index);
                var word = text[index..end];
                if (keywords.Contains(word) || sqlComments && keywords.Contains(word.ToLowerInvariant()))
                    spans.Add(index, end - index, QuickViewSyntaxKind.Keyword);
                index = end;
                continue;
            }
            index++;
        }
    }

    private static void ScanJson(string text, SpanCollector spans, CancellationToken token)
    {
        for (var index = 0; index < text.Length && !spans.IsFull;)
        {
            CheckCancellation(index, token);
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] is '/' or '*')
            {
                var line = text[index + 1] == '/';
                var end = line ? LineEnd(text, index) : FindTerminator(text, index + 2, "*/");
                spans.Add(index, end - index, QuickViewSyntaxKind.Comment);
                index = end;
                continue;
            }
            if (text[index] == '"')
            {
                var end = ScanQuoted(text, index, '"');
                var next = SkipWhitespace(text, end);
                spans.Add(index, end - index,
                    next < text.Length && text[next] == ':'
                        ? QuickViewSyntaxKind.Property
                        : QuickViewSyntaxKind.String);
                index = end;
                continue;
            }
            if (text[index] == '-' || char.IsDigit(text[index]))
            {
                var end = ScanNumber(text, index);
                spans.Add(index, end - index, QuickViewSyntaxKind.Number);
                index = end;
                continue;
            }
            if (IsIdentifierStart(text[index]))
            {
                var end = ScanIdentifier(text, index);
                if (JsonLiterals.Contains(text[index..end]))
                    spans.Add(index, end - index, QuickViewSyntaxKind.Keyword);
                index = end;
                continue;
            }
            index++;
        }
    }

    private static void ScanMarkup(string text, SpanCollector spans, CancellationToken token)
    {
        for (var index = 0; index < text.Length && !spans.IsFull;)
        {
            CheckCancellation(index, token);
            if (text.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
            {
                var end = FindTerminator(text, index + 4, "-->");
                spans.Add(index, end - index, QuickViewSyntaxKind.Comment);
                index = end;
                continue;
            }
            if (text[index] != '<') { index++; continue; }
            var cursor = index + 1;
            if (cursor < text.Length && text[cursor] == '/') cursor++;
            if (cursor < text.Length && text[cursor] is '!' or '?') cursor++;
            var tagStart = cursor;
            while (cursor < text.Length && (IsIdentifierPart(text[cursor]) || text[cursor] is ':' or '-')) cursor++;
            if (cursor > tagStart) spans.Add(tagStart, cursor - tagStart, QuickViewSyntaxKind.Tag);
            while (cursor < text.Length && text[cursor] != '>' && !spans.IsFull)
            {
                if (text[cursor] is '\'' or '"')
                {
                    var end = ScanQuoted(text, cursor, text[cursor]);
                    spans.Add(cursor, end - cursor, QuickViewSyntaxKind.String);
                    cursor = end;
                }
                else if (IsIdentifierStart(text[cursor]))
                {
                    var start = cursor++;
                    while (cursor < text.Length && (IsIdentifierPart(text[cursor]) || text[cursor] is ':' or '-')) cursor++;
                    spans.Add(start, cursor - start, QuickViewSyntaxKind.Attribute);
                }
                else cursor++;
            }
            index = cursor < text.Length ? cursor + 1 : cursor;
        }
    }

    private static void ScanProperties(
        string text,
        SpanCollector spans,
        CancellationToken token,
        bool equalsSeparator)
    {
        var index = 0;
        while (index < text.Length && !spans.IsFull)
        {
            CheckCancellation(index, token);
            var end = LineEnd(text, index);
            var contentEnd = end;
            var quote = '\0';
            var comment = -1;
            for (var cursor = index; cursor < end; cursor++)
            {
                if (text[cursor] is '\'' or '"') quote = quote == '\0' ? text[cursor] : quote == text[cursor] ? '\0' : quote;
                if (quote == '\0' && text[cursor] == '#') { comment = cursor; contentEnd = cursor; break; }
            }
            var separator = text.IndexOf(equalsSeparator ? '=' : ':', index, contentEnd - index);
            if (separator > index)
            {
                var keyStart = SkipWhitespace(text, index);
                var keyEnd = separator;
                while (keyEnd > keyStart && char.IsWhiteSpace(text[keyEnd - 1])) keyEnd--;
                spans.Add(keyStart, keyEnd - keyStart, QuickViewSyntaxKind.Property);
                ScanValues(text, separator + 1, contentEnd, spans);
            }
            else ScanValues(text, index, contentEnd, spans);
            if (comment >= 0) spans.Add(comment, end - comment, QuickViewSyntaxKind.Comment);
            index = end < text.Length ? end + 1 : end;
        }
    }

    private static void ScanCss(string text, SpanCollector spans, CancellationToken token)
    {
        for (var index = 0; index < text.Length && !spans.IsFull;)
        {
            CheckCancellation(index, token);
            if (index + 1 < text.Length && text[index] == '/' && text[index + 1] == '*')
            {
                var end = FindTerminator(text, index + 2, "*/");
                spans.Add(index, end - index, QuickViewSyntaxKind.Comment);
                index = end;
                continue;
            }
            if (text[index] is '\'' or '"')
            {
                var end = ScanQuoted(text, index, text[index]);
                spans.Add(index, end - index, QuickViewSyntaxKind.String);
                index = end;
                continue;
            }
            if (text[index] == '@')
            {
                var end = ScanIdentifier(text, index + 1);
                spans.Add(index, end - index, QuickViewSyntaxKind.Keyword);
                index = end;
                continue;
            }
            if (char.IsDigit(text[index]))
            {
                var end = ScanNumber(text, index);
                spans.Add(index, end - index, QuickViewSyntaxKind.Number);
                index = end;
                continue;
            }
            if (IsIdentifierStart(text[index]) || text[index] == '-')
            {
                var start = index++;
                while (index < text.Length && (IsIdentifierPart(text[index]) || text[index] == '-')) index++;
                var next = SkipWhitespace(text, index);
                if (next < text.Length && text[next] == ':')
                    spans.Add(start, index - start, QuickViewSyntaxKind.Property);
                continue;
            }
            index++;
        }
    }

    private static void ScanMarkdown(string text, SpanCollector spans, CancellationToken token)
    {
        var index = 0;
        while (index < text.Length && !spans.IsFull)
        {
            CheckCancellation(index, token);
            var end = LineEnd(text, index);
            var cursor = index;
            while (cursor < end && text[cursor] == ' ') cursor++;
            if (cursor < end && text[cursor] == '#')
            {
                var markerEnd = cursor;
                while (markerEnd < end && text[markerEnd] == '#') markerEnd++;
                if (markerEnd < end && char.IsWhiteSpace(text[markerEnd]))
                    spans.Add(cursor, end - cursor, QuickViewSyntaxKind.Heading);
            }
            else if (cursor + 2 < end && text.AsSpan(cursor, 3).SequenceEqual("```"))
                spans.Add(cursor, end - cursor, QuickViewSyntaxKind.Keyword);
            else
            {
                if (cursor < end && text[cursor] == '>')
                    spans.Add(cursor, 1, QuickViewSyntaxKind.Comment);
                ScanMarkdownInline(text, cursor, end, spans);
            }
            index = end < text.Length ? end + 1 : end;
        }
    }

    private static void ScanMarkdownInline(string text, int start, int end, SpanCollector spans)
    {
        for (var index = start; index < end && !spans.IsFull; index++)
        {
            if (text[index] == '`')
            {
                var close = text.IndexOf('`', index + 1, end - index - 1);
                var finish = close < 0 ? end : close + 1;
                spans.Add(index, finish - index, QuickViewSyntaxKind.String);
                index = finish - 1;
            }
            else if (text[index] == '[')
            {
                var close = text.IndexOf("](", index + 1, StringComparison.Ordinal);
                if (close < 0 || close >= end) continue;
                var finish = text.IndexOf(')', close + 2, end - close - 2);
                if (finish < 0) continue;
                spans.Add(index, finish + 1 - index, QuickViewSyntaxKind.Link);
                index = finish;
            }
        }
    }

    private static void ScanValues(string text, int start, int end, SpanCollector spans)
    {
        for (var index = start; index < end && !spans.IsFull;)
        {
            if (text[index] is '\'' or '"')
            {
                var finish = Math.Min(end, ScanQuoted(text, index, text[index]));
                spans.Add(index, finish - index, QuickViewSyntaxKind.String);
                index = finish;
            }
            else if (text[index] == '-' || char.IsDigit(text[index]))
            {
                var finish = Math.Min(end, ScanNumber(text, index));
                spans.Add(index, finish - index, QuickViewSyntaxKind.Number);
                index = finish;
            }
            else if (IsIdentifierStart(text[index]))
            {
                var finish = Math.Min(end, ScanIdentifier(text, index));
                if (YamlLiterals.Contains(text[index..finish].ToLowerInvariant()))
                    spans.Add(index, finish - index, QuickViewSyntaxKind.Keyword);
                index = finish;
            }
            else index++;
        }
    }

    private static SyntaxFormat? ResolveFormat(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".cs" => new("C#", SyntaxFamily.CLike, CSharpKeywords),
            ".js" or ".jsx" or ".mjs" or ".cjs" or ".ts" or ".tsx" =>
                new("JavaScript / TypeScript", SyntaxFamily.CLike, JavaScriptKeywords),
            ".java" or ".kt" or ".kts" or ".swift" => new("Java / Kotlin / Swift", SyntaxFamily.CLike, CFamilyKeywords),
            ".c" or ".h" or ".cc" or ".cpp" or ".cxx" or ".hpp" => new("C / C++", SyntaxFamily.CLike, CFamilyKeywords),
            ".rs" => new("Rust", SyntaxFamily.CLike, RustKeywords),
            ".go" => new("Go", SyntaxFamily.CLike, GoKeywords),
            ".py" or ".pyw" => new("Python", SyntaxFamily.HashCode, PythonKeywords),
            ".sh" or ".bash" or ".zsh" or ".fish" => new("Shell", SyntaxFamily.HashCode, ShellKeywords),
            ".ps1" or ".psm1" or ".psd1" => new("PowerShell", SyntaxFamily.HashCode, PowerShellKeywords),
            ".json" or ".jsonc" => new("JSON", SyntaxFamily.Json),
            ".xml" or ".html" or ".htm" or ".xhtml" or ".xaml" or ".axaml" or ".svg" =>
                new("XML / HTML", SyntaxFamily.Markup),
            ".yaml" or ".yml" => new("YAML", SyntaxFamily.Properties),
            ".toml" => new("TOML", SyntaxFamily.Properties),
            ".css" or ".scss" or ".sass" or ".less" => new("CSS", SyntaxFamily.Css),
            ".sql" => new("SQL", SyntaxFamily.Sql, SqlKeywords),
            ".md" or ".markdown" => new("Markdown", SyntaxFamily.Markdown),
            _ => null
        };
    }

    private static int ScanQuoted(string text, int start, char quote)
    {
        var triple = start + 2 < text.Length && text[start + 1] == quote && text[start + 2] == quote;
        var index = start + (triple ? 3 : 1);
        while (index < text.Length)
        {
            if (text[index] == '\\') { index = Math.Min(text.Length, index + 2); continue; }
            if (triple && index + 2 < text.Length && text[index] == quote
                       && text[index + 1] == quote && text[index + 2] == quote) return index + 3;
            if (!triple && text[index] == quote) return index + 1;
            index++;
        }
        return text.Length;
    }

    private static int ScanNumber(string text, int start)
    {
        var index = start;
        if (index < text.Length && text[index] == '-') index++;
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '.' or '_' or '+' or '-')) index++;
        return index;
    }

    private static int ScanIdentifier(string text, int start)
    {
        var index = start;
        while (index < text.Length && IsIdentifierPart(text[index])) index++;
        return index;
    }

    private static int SkipWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        return index;
    }

    private static int LineEnd(string text, int start)
    {
        var index = text.IndexOf('\n', start);
        return index < 0 ? text.Length : index;
    }

    private static int FindTerminator(string text, int start, string terminator)
    {
        var index = text.IndexOf(terminator, start, StringComparison.Ordinal);
        return index < 0 ? text.Length : index + terminator.Length;
    }

    private static void CheckCancellation(int index, CancellationToken token)
    {
        if ((index & 0x3FF) == 0) token.ThrowIfCancellationRequested();
    }

    private static bool IsIdentifierStart(char value) => char.IsLetter(value) || value is '_' or '$';
    private static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';
    private static HashSet<string> Words(string words) => words.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    private enum SyntaxFamily { CLike, HashCode, Json, Markup, Properties, Css, Sql, Markdown }
    private readonly record struct SyntaxFormat(
        string Language,
        SyntaxFamily Family,
        HashSet<string>? Keywords = null);

    private sealed class SpanCollector(int maximum)
    {
        private readonly List<QuickViewSyntaxSpan> _spans = [];
        public IReadOnlyList<QuickViewSyntaxSpan> Spans => _spans;
        public bool IsFull => _spans.Count >= maximum;
        public bool IsTruncated { get; private set; }

        public void Add(int start, int length, QuickViewSyntaxKind kind)
        {
            if (length <= 0) return;
            if (IsFull) { IsTruncated = true; return; }
            if (_spans.Count > 0 && start < _spans[^1].Start + _spans[^1].Length) return;
            _spans.Add(new QuickViewSyntaxSpan(start, length, kind));
            if (IsFull) IsTruncated = true;
        }
    }
}
