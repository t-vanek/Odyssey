using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Odyssey.Core;
using Odyssey.Infrastructure;

namespace Odyssey.Search;

public sealed partial class SqliteSearchService(
    SqliteConnectionFactory connections,
    ILogger<SqliteSearchService> logger) : ISearchService
{
    private readonly ConcurrentDictionary<string, CachedHistoryEntry[]> _historyCache = new(StringComparer.Ordinal);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='FilesFts';";
        var existingSchema = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        var needsRebuild = existingSchema is null || !existingSchema.Contains("ContentText", StringComparison.OrdinalIgnoreCase);
        if (existingSchema is not null && needsRebuild)
        {
            await using var migrate = connection.CreateCommand();
            migrate.CommandText = """
                DROP TRIGGER IF EXISTS Files_AfterInsert;
                DROP TRIGGER IF EXISTS Files_AfterDelete;
                DROP TRIGGER IF EXISTS Files_AfterUpdate;
                DROP TABLE FilesFts;
                """;
            await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DROP TRIGGER IF EXISTS Files_AfterUpdate;

            CREATE VIRTUAL TABLE IF NOT EXISTS FilesFts USING fts5(
                Name, FullPath, ParentPath, ContentText,
                content='Files', content_rowid='Id',
                tokenize='unicode61 remove_diacritics 2'
            );

            CREATE TRIGGER IF NOT EXISTS Files_AfterInsert AFTER INSERT ON Files BEGIN
                INSERT INTO FilesFts(rowid, Name, FullPath, ParentPath, ContentText)
                VALUES(new.Id, new.Name, new.FullPath, new.ParentPath, new.ContentText);
            END;

            CREATE TRIGGER IF NOT EXISTS Files_AfterDelete AFTER DELETE ON Files BEGIN
                INSERT INTO FilesFts(FilesFts, rowid, Name, FullPath, ParentPath, ContentText)
                VALUES('delete', old.Id, old.Name, old.FullPath, old.ParentPath, old.ContentText);
            END;

            CREATE TRIGGER IF NOT EXISTS Files_AfterUpdate AFTER UPDATE OF Name, FullPath, ParentPath, ContentText ON Files
            WHEN old.Name IS NOT new.Name OR old.FullPath IS NOT new.FullPath
              OR old.ParentPath IS NOT new.ParentPath OR old.ContentText IS NOT new.ContentText BEGIN
                INSERT INTO FilesFts(FilesFts, rowid, Name, FullPath, ParentPath, ContentText)
                VALUES('delete', old.Id, old.Name, old.FullPath, old.ParentPath, old.ContentText);
                INSERT INTO FilesFts(rowid, Name, FullPath, ParentPath, ContentText)
                VALUES(new.Id, new.Name, new.FullPath, new.ParentPath, new.ContentText);
            END;

            CREATE TABLE IF NOT EXISTS SearchHistory(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                SessionId TEXT NOT NULL,
                Query TEXT NOT NULL,
                NormalizedQuery TEXT NOT NULL,
                UseCount INTEGER NOT NULL DEFAULT 1,
                LastUsedAt TEXT NOT NULL,
                UNIQUE(SessionId, NormalizedQuery)
            );
            CREATE INDEX IF NOT EXISTS IX_SearchHistory_Session_LastUsed
                ON SearchHistory(SessionId, LastUsedAt DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        if (needsRebuild)
        {
            await using var rebuild = connection.CreateCommand();
            rebuild.CommandText = "INSERT INTO FilesFts(FilesFts) VALUES('rebuild');";
            await rebuild.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WarmupAsync(Guid? sessionId, CancellationToken cancellationToken = default)
    {
        var session = sessionId?.ToString() ?? string.Empty;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        _historyCache[session] = await LoadHistoryAsync(connection, session, cancellationToken).ConfigureAwait(false);

        // Prime the FTS virtual table and shared SQLite page cache without
        // scanning indexed rows or delaying startup on a large catalogue.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rowid FROM FilesFts LIMIT 1;";
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SearchResponse> SearchAsync(SearchRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var terms = Tokenize(request.Query);
        var sql = new StringBuilder();
        var usesFts = terms.Count > 0;
        sql.Append(usesFts
            ? """
              SELECT f.Id,f.Name,f.FullPath,f.EntryType,f.Category,f.Size,f.CreatedAt,f.ModifiedAt,f.TargetId,f.IsMissing,
                     bm25(FilesFts, 9.0, 1.2, 0.7, 0.35) AS Rank,
                     snippet(FilesFts, 3, '', '', ' … ', 18) AS MatchSnippet
              FROM FilesFts JOIN Files f ON f.Id=FilesFts.rowid JOIN ScanTargets t ON t.Id=f.TargetId
              WHERE FilesFts MATCH $query
              """
            : """
              SELECT f.Id,f.Name,f.FullPath,f.EntryType,f.Category,f.Size,f.CreatedAt,f.ModifiedAt,f.TargetId,f.IsMissing,
                     0.0 AS Rank, NULL AS MatchSnippet
              FROM Files f JOIN ScanTargets t ON t.Id=f.TargetId
              WHERE 1=1
              """);

        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        if (usesFts) command.Parameters.AddWithValue("$query", BuildFtsQuery(terms));
        AddFilters(sql, command, request);
        sql.Append(usesFts
            ? " ORDER BY Rank ASC, length(f.Name) ASC, f.Name COLLATE NOCASE ASC, f.Id ASC"
            : " ORDER BY f.ModifiedAt DESC, f.Name COLLATE NOCASE ASC, f.Id ASC");
        sql.Append(" LIMIT $limit OFFSET $offset;");
        var requestedLimit = Math.Clamp(request.Limit, 1, 1000);
        command.Parameters.AddWithValue("$limit", requestedLimit + 1);
        command.Parameters.AddWithValue("$offset", Math.Max(0, request.Offset));
        command.CommandText = sql.ToString();

        var results = new List<SearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var path = reader.GetString(2);
            var rank = reader.GetDouble(10);
            var (score, evidence) = usesFts
                ? CalculateConfidence(request.Query, terms, name, path, rank, reader.GetBoolean(9))
                : (0d, SearchMatchEvidence.None);
            results.Add(new SearchResult
            {
                FileId = reader.GetInt64(0),
                Name = name,
                FullPath = path,
                Type = (FileEntryType)reader.GetInt32(3),
                Category = (FileCategory)reader.GetInt32(4),
                Size = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                CreatedAt = ReadDate(reader, 6),
                ModifiedAt = ReadDate(reader, 7),
                TargetId = Guid.Parse(reader.GetString(8)),
                IsMissing = reader.GetBoolean(9),
                Score = score,
                MatchEvidence = evidence,
                MatchSnippet = reader.IsDBNull(11) ? null : reader.GetString(11),
                MatchedTerms = terms
            });
        }

        var hasMore = results.Count > requestedLimit;
        if (hasMore) results.RemoveAt(results.Count - 1);
        if (usesFts)
            results.Sort((left, right) =>
            {
                var scoreComparison = right.Score.CompareTo(left.Score);
                return scoreComparison != 0
                    ? scoreComparison
                    : StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            });

        stopwatch.Stop();
        logger.LogDebug("Search returned {Count} results in {ElapsedMilliseconds} ms", results.Count, stopwatch.Elapsed.TotalMilliseconds);
        return new SearchResponse(results, stopwatch.Elapsed, results.Count, hasMore);
    }

    public async Task<IReadOnlyList<SearchSuggestion>> SuggestAsync(
        SearchSuggestionRequest request,
        CancellationToken cancellationToken)
    {
        var query = CollapseWhitespace(request.Query);
        var normalized = query.ToLowerInvariant();
        var terms = Tokenize(query);
        if (normalized.Length < 2 || terms.Count == 0) return [];

        var limit = Math.Clamp(request.Limit, 1, 12);
        var session = request.SessionId?.ToString() ?? string.Empty;
        var suggestions = new List<SearchSuggestion>(limit);
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase) { query };
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (_historyCache.TryGetValue(session, out var cachedHistory))
        {
            foreach (var item in cachedHistory.Where(item =>
                         item.NormalizedQuery.StartsWith(normalized, StringComparison.Ordinal)))
            {
                if (seen.Add(item.Query))
                    suggestions.Add(new SearchSuggestion(item.Query, SearchSuggestionKind.History));
                if (suggestions.Count >= limit) break;
            }
        }
        else
        {
            await using var history = connection.CreateCommand();
            history.CommandText = """
                SELECT Query
                FROM SearchHistory
                WHERE SessionId=$session AND instr(NormalizedQuery, $query)=1
                ORDER BY UseCount DESC, LastUsedAt DESC
                LIMIT $limit;
                """;
            history.Parameters.AddWithValue("$session", session);
            history.Parameters.AddWithValue("$query", normalized);
            history.Parameters.AddWithValue("$limit", limit);
            await using var reader = await history.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = reader.GetString(0);
                if (seen.Add(value)) suggestions.Add(new SearchSuggestion(value, SearchSuggestionKind.History));
            }
        }

        if (suggestions.Count >= limit) return suggestions;
        await using (var files = connection.CreateCommand())
        {
            var sql = new StringBuilder("""
                SELECT f.Name, bm25(FilesFts, 9.0, 1.2, 0.7, 0.35) AS Rank
                FROM FilesFts
                JOIN Files f ON f.Id=FilesFts.rowid
                JOIN ScanTargets t ON t.Id=f.TargetId
                WHERE FilesFts MATCH $query AND f.EntryType=$entryType
                """);
            if (request.SessionId is not null) sql.Append(" AND t.SessionId=$session");
            sql.Append(" ORDER BY Rank ASC, f.ModifiedAt DESC LIMIT $candidateLimit;");
            files.CommandText = sql.ToString();
            files.Parameters.AddWithValue("$query", BuildFtsQuery(terms));
            files.Parameters.AddWithValue("$entryType", (int)FileEntryType.File);
            files.Parameters.AddWithValue("$candidateLimit", Math.Min(60, (limit - suggestions.Count) * 8));
            if (request.SessionId is not null) files.Parameters.AddWithValue("$session", session);
            await using var reader = await files.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (suggestions.Count < limit && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var value = CreateSuggestionText(reader.GetString(0));
                if (value.Length >= 2 && seen.Add(value))
                    suggestions.Add(new SearchSuggestion(value, SearchSuggestionKind.FileName));
            }
        }

        return suggestions;
    }

    public async Task RememberSearchAsync(
        string query,
        Guid? sessionId,
        CancellationToken cancellationToken = default)
    {
        var value = CollapseWhitespace(query);
        if (value.Length < 2) return;
        if (value.Length > 240) value = value[..240];
        var normalized = value.ToLowerInvariant();
        var session = sessionId?.ToString() ?? string.Empty;
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO SearchHistory(SessionId, Query, NormalizedQuery, UseCount, LastUsedAt)
            VALUES($session, $query, $normalized, 1, $now)
            ON CONFLICT(SessionId, NormalizedQuery) DO UPDATE SET
                Query=excluded.Query,
                UseCount=SearchHistory.UseCount+1,
                LastUsedAt=excluded.LastUsedAt;

            DELETE FROM SearchHistory
            WHERE Id IN (
                SELECT Id FROM SearchHistory
                WHERE SessionId=$session
                ORDER BY LastUsedAt DESC
                LIMIT -1 OFFSET 100
            );
            """;
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$query", value);
        command.Parameters.AddWithValue("$normalized", normalized);
        command.Parameters.AddWithValue("$now", FormatDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        _historyCache[session] = await LoadHistoryAsync(connection, session, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CachedHistoryEntry[]> LoadHistoryAsync(
        SqliteConnection connection,
        string session,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Query, NormalizedQuery
            FROM SearchHistory
            WHERE SessionId=$session
            ORDER BY UseCount DESC, LastUsedAt DESC
            LIMIT 100;
            """;
        command.Parameters.AddWithValue("$session", session);
        var results = new List<CachedHistoryEntry>(100);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            results.Add(new CachedHistoryEntry(reader.GetString(0), reader.GetString(1)));
        return results.ToArray();
    }

    private static void AddFilters(StringBuilder sql, SqliteCommand command, SearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Extension))
        {
            sql.Append(" AND f.Extension=$extension");
            var extension = request.Extension.Trim();
            command.Parameters.AddWithValue("$extension", extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}");
        }
        if (request.Category is not null) { sql.Append(" AND f.Category=$category"); command.Parameters.AddWithValue("$category", (int)request.Category.Value); }
        if (request.MinimumSize is not null) { sql.Append(" AND f.Size>=$minimumSize"); command.Parameters.AddWithValue("$minimumSize", request.MinimumSize.Value); }
        if (request.MaximumSize is not null) { sql.Append(" AND f.Size<=$maximumSize"); command.Parameters.AddWithValue("$maximumSize", request.MaximumSize.Value); }
        if (request.ModifiedFrom is not null) { sql.Append(" AND f.ModifiedAt>=$modifiedFrom"); command.Parameters.AddWithValue("$modifiedFrom", FormatDate(request.ModifiedFrom.Value)); }
        if (request.ModifiedTo is not null) { sql.Append(" AND f.ModifiedAt<=$modifiedTo"); command.Parameters.AddWithValue("$modifiedTo", FormatDate(request.ModifiedTo.Value)); }
        if (request.TargetId is not null) { sql.Append(" AND f.TargetId=$target"); command.Parameters.AddWithValue("$target", request.TargetId.Value.ToString()); }
        if (request.SessionId is not null) { sql.Append(" AND t.SessionId=$session"); command.Parameters.AddWithValue("$session", request.SessionId.Value.ToString()); }
    }

    private static string BuildFtsQuery(IEnumerable<string> terms) =>
        string.Join(" AND ", terms.Select(term => $"\"{term.Replace("\"", "\"\"")}\"*"));

    private static (double Score, SearchMatchEvidence Evidence) CalculateConfidence(
        string query,
        IReadOnlyList<string> queryTerms,
        string name,
        string fullPath,
        double rank,
        bool isMissing)
    {
        var terms = queryTerms.Select(FoldForComparison)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (terms.Length == 0) return (0d, SearchMatchEvidence.None);

        var foldedName = FoldForComparison(name);
        var foldedStem = FoldForComparison(Path.GetFileNameWithoutExtension(name));
        var foldedParent = FoldForComparison(Path.GetDirectoryName(fullPath) ?? string.Empty);
        var foldedQuery = FoldForComparison(query);
        var nameMatches = 0;
        var pathMatches = 0;
        var contentMatches = 0;
        foreach (var term in terms)
        {
            if (foldedName.Contains(term, StringComparison.Ordinal)) nameMatches++;
            else if (foldedParent.Contains(term, StringComparison.Ordinal)) pathMatches++;
            else contentMatches++;
        }

        var evidence = SearchMatchEvidence.None;
        if (nameMatches > 0) evidence |= SearchMatchEvidence.Name;
        if (pathMatches > 0) evidence |= SearchMatchEvidence.Path;
        if (contentMatches > 0) evidence |= SearchMatchEvidence.Content;
        var exactName = foldedStem == foldedQuery || foldedName == foldedQuery;
        if (exactName) evidence |= SearchMatchEvidence.ExactName;

        var count = (double)terms.Length;
        var score = 0.28
                    + 0.47 * (nameMatches / count)
                    + 0.18 * (pathMatches / count)
                    + 0.27 * (contentMatches / count);
        if (exactName) score += 0.20;
        else if (foldedStem.Contains(foldedQuery, StringComparison.Ordinal)) score += 0.10;
        if (nameMatches == terms.Length && terms.Length > 1) score += 0.04;
        score += Math.Tanh(Math.Abs(rank) / 8d) * 0.04;
        if (isMissing) score -= 0.22;
        return (Math.Clamp(score, 0.08, 0.98), evidence);
    }

    private static string CreateSuggestionText(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(stem)) stem = fileName;
        return CollapseWhitespace(SuggestionSeparatorPattern().Replace(stem, " "));
    }

    private static string CollapseWhitespace(string value) => WhitespacePattern().Replace(value.Trim(), " ");

    private static string FoldForComparison(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSeparator = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSeparator && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else pendingSeparator = true;
        }
        return builder.ToString();
    }

    private static IReadOnlyList<string> Tokenize(string query) => TermPattern().Matches(query)
        .Select(match => match.Value.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Take(16).ToArray();

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private sealed record CachedHistoryEntry(string Query, string NormalizedQuery);

    [GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
    private static partial Regex TermPattern();

    [GeneratedRegex(@"[_\-.]+", RegexOptions.CultureInvariant)]
    private static partial Regex SuggestionSeparatorPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
