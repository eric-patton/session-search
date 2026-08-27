using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Search;
using SessionSearch.Core.Sessions;
using SessionSearch.Core.Text;
using SessionSearch.Infrastructure.Storage;

namespace SessionSearch.Infrastructure.Search;

public sealed class SessionSearchService(
    SessionDatabase database,
    Func<bool>? partialState = null)
{
    private const int MaxSnippetScalars = 240;
    private const string SnippetStartMarker = "\uE000\uE001\uE002\uE003";
    private const string SnippetEndMarker = "\uE003\uE002\uE001\uE000";

    public async ValueTask<SessionSearchPage> SearchAsync(
        SessionSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        long offset = (long)request.SafePage * request.SafePageSize;
        bool queryIsPartial = false;
        SearchPageData page;

        if (request.Query.IsBrowse)
        {
            page = await ReadPageAsync(
                request,
                BuildBrowseSql(),
                includeTranscript: false,
                offset,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            bool includeTranscript = request.ContentMode == SearchContentMode.All;
            try
            {
                page = await ReadPageAsync(
                    request,
                    BuildSearchSql(request.Query, includeTranscript),
                    includeTranscript,
                    offset,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException) when (
                includeTranscript && !cancellationToken.IsCancellationRequested)
            {
                page = await ReadPageAsync(
                    request,
                    BuildSearchSql(request.Query, includeTranscript: false),
                    includeTranscript: false,
                    offset,
                    cancellationToken).ConfigureAwait(false);
                queryIsPartial = true;
            }
        }

        List<SessionSearchResult> results = [.. page.Results];
        SessionIdentity[] transcriptIdentities = request.ContentMode == SearchContentMode.All
            ? results
            .Where(result => result.MatchClass == MatchClass.Transcript)
            .Select(result => result.Session.Identity)
            .ToArray()
            : [];

        if (transcriptIdentities.Length > 0)
        {
            try
            {
                Dictionary<SessionIdentity, SnippetHit> snippets = await ReadSnippetsAsync(
                    request.Query,
                    transcriptIdentities,
                    cancellationToken).ConfigureAwait(false);
                for (int index = 0; index < results.Count; index++)
                {
                    SessionSearchResult result = results[index];
                    if (snippets.TryGetValue(result.Session.Identity, out SnippetHit? snippet))
                    {
                        results[index] = result with
                        {
                            Snippet = snippet.Text,
                            SnippetFromChild = snippet.IsChild,
                        };
                    }
                }

                queryIsPartial |= snippets.Count < transcriptIdentities.Length;
            }
            catch (SqliteException) when (!cancellationToken.IsCancellationRequested)
            {
                queryIsPartial = true;
            }
        }

        return new SessionSearchPage(
            results,
            page.TotalCount,
            queryIsPartial || (partialState?.Invoke() ?? false));
    }

    private async ValueTask<SearchPageData> ReadPageAsync(
        SessionSearchRequest request,
        string commandText,
        bool includeTranscript,
        long offset,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        AddScopeParameters(command, request.Scope);
        command.Parameters.AddWithValue(
            "$directory_filter",
            string.IsNullOrWhiteSpace(request.DirectoryFilter)
                ? DBNull.Value
                : TextNormalization.NormalizeMetadata(request.DirectoryFilter));
        command.Parameters.AddWithValue("$limit", request.SafePageSize);
        command.Parameters.AddWithValue("$offset", offset);

        if (!request.Query.IsBrowse)
        {
            command.Parameters.AddWithValue("$query", request.Query.NormalizedText);
            for (int index = 0; index < request.Query.Atoms.Count; index++)
            {
                QueryAtom atom = request.Query.Atoms[index];
                command.Parameters.AddWithValue($"$atom{index}", atom.NormalizedText);
                if (includeTranscript && atom.TranscriptExpression.Length > 0)
                {
                    command.Parameters.AddWithValue(
                        $"$fts{index}",
                        atom.TranscriptExpression);
                }
            }
        }

        List<SessionSearchResult> results = [];
        long totalCount = 0;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totalCount = reader.GetInt64(0);
            if (reader.IsDBNull(1))
            {
                continue;
            }

            SessionDocument session = new(
                new SessionIdentity(
                    (SessionProvider)reader.GetInt32(1),
                    Guid.Parse(reader.GetString(2))),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
                ParseTimestamp(reader.GetString(10)),
                reader.GetInt64(11),
                reader.GetInt64(12) != 0,
                reader.GetInt64(13) != 0,
                reader.GetInt64(14) != 0,
                reader.GetInt32(15));
            MatchClass? matchClass = reader.IsDBNull(18)
                ? null
                : (MatchClass)reader.GetInt32(18);
            results.Add(new SessionSearchResult(
                session,
                matchClass,
                reader.GetDouble(19),
                null,
                false,
                reader.GetInt64(16) != 0,
                reader.GetInt64(17) != 0));
        }

        int boundedTotalCount = totalCount > int.MaxValue
            ? int.MaxValue
            : (int)totalCount;
        return new SearchPageData(results, boundedTotalCount);
    }

    private async ValueTask<Dictionary<SessionIdentity, SnippetHit>> ReadSnippetsAsync(
        ParsedQuery query,
        SessionIdentity[] identities,
        CancellationToken cancellationToken)
    {
        string expression = string.Join(
            " OR ",
            query.Atoms
                .Select(atom => atom.TranscriptExpression)
                .Where(atomExpression => atomExpression.Length > 0)
                .Select(atomExpression => $"({atomExpression})"));
        if (expression.Length == 0 || identities.Length == 0)
        {
            return [];
        }

        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = BuildSnippetSql(identities.Length);
        command.Parameters.AddWithValue("$snippetExpression", expression);
        command.Parameters.AddWithValue("$snippetStart", SnippetStartMarker);
        command.Parameters.AddWithValue("$snippetEnd", SnippetEndMarker);
        for (int index = 0; index < identities.Length; index++)
        {
            command.Parameters.AddWithValue(
                $"$snippetProvider{index}",
                (int)identities[index].Provider);
            command.Parameters.AddWithValue(
                $"$snippetSession{index}",
                identities[index].SessionId.ToString("D"));
        }

        Dictionary<SessionIdentity, SnippetHit> snippets = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            SessionIdentity identity = new(
                (SessionProvider)reader.GetInt32(0),
                Guid.Parse(reader.GetString(1)));
            snippets[identity] = new SnippetHit(
                BuildSnippet(reader.GetString(3)),
                reader.GetInt64(2) != 0);
        }

        return snippets;
    }

    private static string BuildBrowseSql()
    {
        StringBuilder sql = new();
        AppendScopedSessions(sql);
        sql.Append(
            """
            matches AS (
                SELECT
                    s.*,
                    NULL AS match_class,
                    0.0 AS bm25
                FROM scoped_sessions AS s
            ),
            """);
        AppendPageEnvelope(sql);
        return sql.ToString();
    }

    private static string BuildSearchSql(ParsedQuery query, bool includeTranscript)
    {
        StringBuilder sql = new();
        AppendScopedSessions(sql);

        List<int> transcriptAtomIndexes = [];
        if (includeTranscript)
        {
            for (int index = 0; index < query.Atoms.Count; index++)
            {
                if (query.Atoms[index].TranscriptExpression.Length == 0)
                {
                    continue;
                }

                transcriptAtomIndexes.Add(index);
                if (query.Atoms.Count == 1)
                {
                    sql.Append(CultureInfo.InvariantCulture, $$"""
                        atom_{{index}}_hits AS (
                            SELECT
                                sg.provider,
                                sg.owner_session_id,
                                min(transcript_fts.rank) AS score
                            FROM transcript_fts
                            JOIN segments AS sg ON sg.id=transcript_fts.rowid
                            WHERE transcript_fts MATCH $fts{{index}}
                            GROUP BY sg.provider, sg.owner_session_id
                        ),
                        """);
                    continue;
                }

                string scopedMetadataExpression = MetadataAnyExpression(index, "scoped");
                sql.Append(CultureInfo.InvariantCulture, $$"""
                    atom_{{index}}_hits AS (
                        SELECT
                            sg.provider,
                            sg.owner_session_id,
                            min(transcript_fts.rank) AS score
                        FROM transcript_fts
                        JOIN segments AS sg ON sg.id=transcript_fts.rowid
                        JOIN scoped_sessions AS scoped
                          ON scoped.provider=sg.provider
                         AND scoped.session_id=sg.owner_session_id
                        WHERE transcript_fts MATCH $fts{{index}}
                          AND NOT ({{scopedMetadataExpression}})
                        GROUP BY sg.provider, sg.owner_session_id
                    ),
                    """);
            }
        }

        string[] metadataExpressions = Enumerable.Range(0, query.Atoms.Count)
            .Select(MetadataAnyExpression)
            .ToArray();
        string allMetadataExpression = string.Join(
            " AND ",
            metadataExpressions.Select(expression => $"({expression})"));
        string anyTitleExpression = JoinAnyMetadataExpression(
            query.Atoms.Count,
            MetadataTitleExpression);
        string anyDescriptionExpression = JoinAnyMetadataExpression(
            query.Atoms.Count,
            MetadataDescriptionExpression);
        string anyDirectoryExpression = JoinAnyMetadataExpression(
            query.Atoms.Count,
            MetadataDirectoryExpression);
        HashSet<int> transcriptIndexSet = transcriptAtomIndexes.ToHashSet();
        string requiredAtomsExpression = string.Join(
            " AND ",
            metadataExpressions.Select((expression, index) =>
                transcriptIndexSet.Contains(index)
                    ? $"(({expression}) OR atom_{index}_hits.provider IS NOT NULL)"
                    : $"({expression})"));
        string transcriptScoreExpression = transcriptAtomIndexes.Count == 0
            ? "0.0"
            : string.Join(
                " + ",
                transcriptAtomIndexes.Select(index =>
                    $"CASE WHEN NOT ({metadataExpressions[index]}) "
                    + $"THEN COALESCE(atom_{index}_hits.score, 0.0) ELSE 0.0 END"));
        string transcriptJoins = string.Join(
            Environment.NewLine,
            transcriptAtomIndexes.Select(index =>
                $"    LEFT JOIN atom_{index}_hits"
                + $" ON atom_{index}_hits.provider=s.provider"
                + $" AND atom_{index}_hits.owner_session_id=s.session_id"));

        sql.Append(CultureInfo.InvariantCulture, $$"""
            matches AS (
                SELECT
                    s.*,
                    CASE
                        WHEN s.normalized_title=$query THEN 0
                        WHEN s.normalized_directory=$query THEN 1
                        WHEN instr(s.normalized_title, $query)=1 THEN 2
                        WHEN NOT ({{allMetadataExpression}}) THEN 7
                        WHEN ({{anyTitleExpression}}) THEN 3
                        WHEN ({{anyDescriptionExpression}}) THEN 4
                        WHEN ({{anyDirectoryExpression}}) THEN 5
                        ELSE 6
                    END AS match_class,
                    CASE
                        WHEN NOT ({{allMetadataExpression}})
                            THEN {{transcriptScoreExpression}}
                        ELSE 0.0
                    END AS bm25
                FROM scoped_sessions AS s
            {{transcriptJoins}}
                WHERE {{requiredAtomsExpression}}
            ),
            """);
        AppendPageEnvelope(sql);
        return sql.ToString();
    }

    private static string BuildSnippetSql(int identityCount)
    {
        string requestedRows = string.Join(
            ", ",
            Enumerable.Range(0, identityCount).Select(index =>
                $"($snippetProvider{index}, $snippetSession{index})"));
        return $$"""
            WITH ranked AS (
                SELECT
                    sg.provider,
                    sg.owner_session_id,
                    transcript_fts.rowid AS segment_id,
                    row_number() OVER (
                        PARTITION BY sg.provider, sg.owner_session_id
                        ORDER BY transcript_fts.rank, sg.ordinal, transcript_fts.rowid
                    ) AS result_number
                FROM transcript_fts
                JOIN segments AS sg ON sg.id=transcript_fts.rowid
                WHERE transcript_fts MATCH $snippetExpression
                  AND (sg.provider, sg.owner_session_id) IN (
                    VALUES {{requestedRows}}
                  )
            ),
            winners AS (
                SELECT provider, owner_session_id, segment_id
                FROM ranked
                WHERE result_number=1
            )
            SELECT
                winners.provider,
                winners.owner_session_id,
                sg.is_child,
                snippet(
                    transcript_fts,
                    0,
                    $snippetStart,
                    $snippetEnd,
                    '',
                    64)
            FROM winners
            JOIN transcript_fts ON transcript_fts.rowid=winners.segment_id
            JOIN segments AS sg ON sg.id=winners.segment_id
            WHERE transcript_fts MATCH $snippetExpression
            ORDER BY winners.provider, winners.owner_session_id;
            """;
    }

    private static void AppendScopedSessions(StringBuilder sql)
    {
        sql.Append(
            """
            WITH scoped_sessions AS (
                SELECT
                    s.provider,
                    s.session_id,
                    s.source_path,
                    s.title,
                    s.description,
                    s.directory,
                    s.branch,
                    s.model,
                    s.created_utc,
                    s.updated_utc,
                    s.source_bytes,
                    s.archived,
                    s.format_supported,
                    s.source_present,
                    s.parser_version,
                    CASE WHEN sf.session_id IS NULL THEN 0 ELSE 1 END
                        AS is_session_favorite,
                    CASE WHEN df.path_key IS NULL THEN 0 ELSE 1 END
                        AS is_directory_favorite,
                    s.normalized_title,
                    s.normalized_description,
                    s.normalized_directory,
                    s.normalized_branch,
                    s.normalized_model
                FROM sessions AS s
                LEFT JOIN session_favorites AS sf
                  ON sf.provider=s.provider AND sf.session_id=s.session_id
                LEFT JOIN directory_favorites AS df
                  ON df.path_key=s.normalized_directory
                WHERE ($provider IS NULL OR s.provider=$provider)
                  AND ($directory_filter IS NULL OR s.normalized_directory=$directory_filter)
                  AND (
                    $starred=0
                    OR sf.session_id IS NOT NULL
                    OR df.path_key IS NOT NULL)
            ),
            """);
    }

    private static void AppendPageEnvelope(StringBuilder sql)
    {
        sql.Append(
            """
            page_rows AS (
                SELECT *
                FROM matches
                ORDER BY
                    match_class ASC,
                    CASE WHEN match_class=7 THEN bm25 ELSE 0.0 END ASC,
                    updated_utc DESC,
                    provider ASC,
                    session_id COLLATE BINARY ASC
                LIMIT $limit OFFSET $offset
            ),
            total AS (
                SELECT count(*) AS total_count
                FROM matches
            )
            SELECT
                total.total_count,
                page_rows.provider,
                page_rows.session_id,
                page_rows.source_path,
                page_rows.title,
                page_rows.description,
                page_rows.directory,
                page_rows.branch,
                page_rows.model,
                page_rows.created_utc,
                page_rows.updated_utc,
                page_rows.source_bytes,
                page_rows.archived,
                page_rows.format_supported,
                page_rows.source_present,
                page_rows.parser_version,
                page_rows.is_session_favorite,
                page_rows.is_directory_favorite,
                page_rows.match_class,
                page_rows.bm25
            FROM total
            LEFT JOIN page_rows ON 1=1
            ORDER BY
                CASE WHEN page_rows.provider IS NULL THEN 1 ELSE 0 END,
                page_rows.match_class ASC,
                CASE WHEN page_rows.match_class=7 THEN page_rows.bm25 ELSE 0.0 END ASC,
                page_rows.updated_utc DESC,
                page_rows.provider ASC,
                page_rows.session_id COLLATE BINARY ASC;
            """);
    }

    private static string MetadataAnyExpression(int index) =>
        MetadataAnyExpression(index, "s");

    private static string MetadataAnyExpression(int index, string sessionAlias) =>
        $"{MetadataTitleExpression(index, sessionAlias)}"
        + $" OR {MetadataDescriptionExpression(index, sessionAlias)}"
        + $" OR {MetadataDirectoryExpression(index, sessionAlias)}"
        + $" OR instr(COALESCE({sessionAlias}.normalized_branch, ''), $atom{index}) > 0"
        + $" OR instr(COALESCE({sessionAlias}.normalized_model, ''), $atom{index}) > 0"
        + $" OR instr(upper({sessionAlias}.session_id), $atom{index}) > 0"
        + $" OR instr(CASE {sessionAlias}.provider WHEN 0 THEN 'CLAUDE CODE'"
        + $" WHEN 1 THEN 'CODEX' ELSE '' END, $atom{index}) > 0";

    private static string MetadataTitleExpression(int index) =>
        MetadataTitleExpression(index, "s");

    private static string MetadataTitleExpression(int index, string sessionAlias) =>
        $"instr({sessionAlias}.normalized_title, $atom{index}) > 0";

    private static string MetadataDescriptionExpression(int index) =>
        MetadataDescriptionExpression(index, "s");

    private static string MetadataDescriptionExpression(int index, string sessionAlias) =>
        $"instr({sessionAlias}.normalized_description, $atom{index}) > 0";

    private static string MetadataDirectoryExpression(int index) =>
        MetadataDirectoryExpression(index, "s");

    private static string MetadataDirectoryExpression(int index, string sessionAlias) =>
        $"instr({sessionAlias}.normalized_directory, $atom{index}) > 0";

    private static string JoinAnyMetadataExpression(
        int atomCount,
        Func<int, string> expressionFactory) =>
        string.Join(" OR ", Enumerable.Range(0, atomCount).Select(expressionFactory));

    private static void AddScopeParameters(SqliteCommand command, SearchScope scope)
    {
        object provider = scope switch
        {
            SearchScope.ClaudeCode => (int)SessionProvider.ClaudeCode,
            SearchScope.Codex => (int)SessionProvider.Codex,
            _ => DBNull.Value,
        };
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$starred", scope == SearchScope.Starred ? 1 : 0);
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string BuildSnippet(string markedValue)
    {
        string withoutBoundaries = markedValue
            .Replace(ProviderLimits.SearchRecordBoundary, "\n", StringComparison.Ordinal)
            .Replace(
                ProviderLimits.SearchRecordBoundaryToken,
                string.Empty,
                StringComparison.Ordinal);
        string safe = DisplayTextSanitizer.Sanitize(withoutBoundaries);
        int markerStart = safe.IndexOf(SnippetStartMarker, StringComparison.Ordinal);
        int markerEnd = markerStart < 0
            ? -1
            : safe.IndexOf(
                SnippetEndMarker,
                markerStart + SnippetStartMarker.Length,
                StringComparison.Ordinal);
        if (markerStart < 0 || markerEnd < 0)
        {
            return TakeScalars(safe, 0, MaxSnippetScalars);
        }

        string prefix = RemoveSnippetMarkers(safe[..markerStart]);
        int matchContentStart = markerStart + SnippetStartMarker.Length;
        string match = RemoveSnippetMarkers(safe[matchContentStart..markerEnd]);
        string suffix = RemoveSnippetMarkers(safe[(markerEnd + SnippetEndMarker.Length)..]);
        Rune[] runes = (prefix + match + suffix).EnumerateRunes().ToArray();
        int matchStart = prefix.EnumerateRunes().Count();
        int matchLength = match.EnumerateRunes().Count();
        int before = Math.Max(0, (MaxSnippetScalars - Math.Min(matchLength, MaxSnippetScalars)) / 2);
        int windowStart = Math.Max(0, matchStart - before);
        int windowEnd = Math.Min(runes.Length, windowStart + MaxSnippetScalars);
        if (windowEnd - windowStart < MaxSnippetScalars)
        {
            windowStart = Math.Max(0, windowEnd - MaxSnippetScalars);
        }

        return BuildRuneString(runes, windowStart, windowEnd - windowStart);
    }

    private static string RemoveSnippetMarkers(string value) =>
        value.Replace(SnippetStartMarker, string.Empty, StringComparison.Ordinal)
            .Replace(SnippetEndMarker, string.Empty, StringComparison.Ordinal);

    private static string TakeScalars(string value, int start, int count)
    {
        Rune[] runes = value.EnumerateRunes().ToArray();
        return BuildRuneString(
            runes,
            Math.Min(start, runes.Length),
            Math.Min(count, Math.Max(0, runes.Length - start)));
    }

    private static string BuildRuneString(Rune[] runes, int start, int count)
    {
        StringBuilder result = new();
        int end = start + count;
        for (int index = start; index < end; index++)
        {
            result.Append(runes[index].ToString());
        }

        return result.ToString();
    }

    private sealed record SearchPageData(
        List<SessionSearchResult> Results,
        int TotalCount);

    private sealed record SnippetHit(string Text, bool IsChild);
}
