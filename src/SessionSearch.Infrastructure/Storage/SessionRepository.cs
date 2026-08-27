using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Sessions;
using SessionSearch.Core.Text;

namespace SessionSearch.Infrastructure.Storage;

public sealed class SessionRepository(SessionDatabase database)
{
    private const int SessionLookupBatchSize = 200;

    public async ValueTask<SessionDocument?> FindSessionAsync(
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_path, title, description, directory, branch, model,
                   created_utc, updated_utc, source_bytes, archived,
                   format_supported, source_present, parser_version
            FROM sessions
            WHERE provider=$provider AND session_id=$session_id;
            """;
        command.Parameters.AddWithValue("$provider", (int)identity.Provider);
        command.Parameters.AddWithValue("$session_id", identity.SessionId.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SessionDocument(
            identity,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : ParseTimestamp(reader.GetString(6)),
            ParseTimestamp(reader.GetString(7)),
            reader.GetInt64(8),
            reader.GetInt64(9) != 0,
            reader.GetInt64(10) != 0,
            reader.GetInt64(11) != 0,
            reader.GetInt32(12));
    }

    public async ValueTask<IReadOnlyDictionary<SessionIdentity, SessionDocument>>
        FindSessionsAsync(
            IReadOnlyCollection<SessionIdentity> identities,
            CancellationToken cancellationToken)
    {
        SessionIdentity[] requested = identities.Distinct().ToArray();
        Dictionary<SessionIdentity, SessionDocument> sessions = [];
        if (requested.Length == 0)
        {
            return sessions;
        }

        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        foreach (IGrouping<SessionProvider, SessionIdentity> providerGroup in requested
                     .GroupBy(identity => identity.Provider))
        {
            foreach (SessionIdentity[] batch in providerGroup.Chunk(SessionLookupBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using SqliteCommand command = connection.CreateCommand();
                string[] parameterNames = batch
                    .Select((_, index) => $"$id_{index}")
                    .ToArray();
                command.CommandText = $"""
                    SELECT session_id, source_path, title, description, directory, branch, model,
                           created_utc, updated_utc, source_bytes, archived,
                           format_supported, source_present, parser_version
                    FROM sessions
                    WHERE provider=$provider
                      AND session_id IN ({string.Join(", ", parameterNames)});
                    """;
                command.Parameters.AddWithValue("$provider", (int)providerGroup.Key);
                for (int index = 0; index < batch.Length; index++)
                {
                    command.Parameters.AddWithValue(
                        parameterNames[index],
                        batch[index].SessionId.ToString("D"));
                }

                await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                    cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid id))
                    {
                        continue;
                    }

                    SessionIdentity identity = new(providerGroup.Key, id);
                    sessions[identity] = ReadSession(reader, identity, columnOffset: 1);
                }
            }
        }

        return sessions;
    }

    public async ValueTask<SourceIndexState?> FindSourceStateAsync(
        string canonicalPath,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT length, last_write_utc, complete_offset, parser_version, status,
                   file_identity
            FROM source_files
            WHERE canonical_path=$canonical_path;
            """;
        command.Parameters.AddWithValue("$canonical_path", canonicalPath);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SourceIndexState(
            reader.GetInt64(0),
            ParseTimestamp(reader.GetString(1)),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    public async ValueTask<IReadOnlyDictionary<SessionIdentity, IReadOnlyList<Guid>>>
        ListChildSessionIdsAsync(
            IReadOnlyCollection<SessionIdentity> owners,
            CancellationToken cancellationToken)
    {
        SessionIdentity[] codexOwners = owners
            .Where(owner => owner.Provider == SessionProvider.Codex)
            .Distinct()
            .Take(50)
            .ToArray();
        if (codexOwners.Length == 0)
        {
            return new Dictionary<SessionIdentity, IReadOnlyList<Guid>>();
        }

        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        string[] parameterNames = codexOwners
            .Select((_, index) => $"$owner_{index}")
            .ToArray();
        command.CommandText = $"""
            SELECT owner_session_id, child_session_id
            FROM source_files
            WHERE provider=$provider
              AND child_session_id IS NOT NULL
              AND owner_session_id IN ({string.Join(", ", parameterNames)})
            ORDER BY owner_session_id, child_session_id;
            """;
        command.Parameters.AddWithValue("$provider", (int)SessionProvider.Codex);
        for (int index = 0; index < codexOwners.Length; index++)
        {
            command.Parameters.AddWithValue(
                parameterNames[index],
                codexOwners[index].SessionId.ToString("D"));
        }

        Dictionary<SessionIdentity, List<Guid>> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid ownerId) ||
                !Guid.TryParseExact(reader.GetString(1), "D", out Guid childId))
            {
                continue;
            }

            SessionIdentity owner = new(SessionProvider.Codex, ownerId);
            if (!values.TryGetValue(owner, out List<Guid>? children))
            {
                children = [];
                values.Add(owner, children);
            }

            children.Add(childId);
        }

        return values.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<Guid>)pair.Value);
    }

    public async ValueTask UpsertSessionAsync(
        SessionDocument session,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        await UpsertSessionAsync(connection, transaction, session, cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask UpsertSessionsAsync(
        IReadOnlyCollection<SessionDocument> sessions,
        CancellationToken cancellationToken)
    {
        if (sessions.Count == 0)
        {
            return;
        }

        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (SessionDocument session in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertSessionAsync(connection, transaction, session, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ReplaceSourceContentAsync(
        ProviderSource source,
        IReadOnlyList<SessionSegment> segments,
        long completeOffset,
        CancellationToken cancellationToken) =>
        await CommitSourceContentAsync(
            session: null,
            source,
            segments,
            completeOffset,
            replaceExisting: true,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask ReplaceSourceContentAsync(
        SessionDocument? session,
        ProviderSource source,
        IReadOnlyList<SessionSegment> segments,
        long completeOffset,
        CancellationToken cancellationToken) =>
        await CommitSourceContentAsync(
            session,
            source,
            segments,
            completeOffset,
            replaceExisting: true,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask AppendSourceContentAsync(
        SessionDocument? session,
        ProviderSource source,
        IReadOnlyList<SessionSegment> segments,
        long completeOffset,
        CancellationToken cancellationToken) =>
        await CommitSourceContentAsync(
            session,
            source,
            segments,
            completeOffset,
            replaceExisting: false,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask MarkSourceFailedAsync(
        ProviderSource source,
        string errorCode,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_files(
                provider, owner_session_id, canonical_path, relative_path,
                source_kind, child_session_id, archived, file_identity, length,
                last_write_utc, complete_offset, parser_version, status,
                retry_count, last_error)
            VALUES(
                $provider, $owner_session_id, $canonical_path, $relative_path,
                $source_kind, $child_session_id, $archived, $file_identity, $length,
                $last_write_utc, 0, $parser_version, 1, 1, $last_error)
            ON CONFLICT(canonical_path) DO UPDATE SET
                length=excluded.length,
                last_write_utc=excluded.last_write_utc,
                file_identity=excluded.file_identity,
                parser_version=excluded.parser_version,
                status=1,
                retry_count=source_files.retry_count + 1,
                last_error=excluded.last_error;
            """;
        AddSourceParameters(command, source, completeOffset: 0);
        command.Parameters.AddWithValue(
            "$last_error",
            errorCode.Length <= 120 ? errorCode : errorCode[..120]);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask CommitSourceContentAsync(
        SessionDocument? session,
        ProviderSource source,
        IReadOnlyList<SessionSegment> segments,
        long completeOffset,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

        if (session is not null)
        {
            await UpsertSessionAsync(connection, transaction, session, cancellationToken)
                .ConfigureAwait(false);
        }

        long sourceId = await UpsertSourceAsync(
            connection,
            transaction,
            source,
            completeOffset,
            cancellationToken).ConfigureAwait(false);

        if (replaceExisting)
        {
            await using SqliteCommand delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM segments WHERE source_file_id=$source_id;";
            delete.Parameters.AddWithValue("$source_id", sourceId);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (SessionSegment segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chunkOrdinal = 0;
            foreach (string boundedText in SplitUtf8(
                segment.Text,
                ProviderLimits.MaxStoredSegmentBytes))
            {
                await using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO segments(
                        provider, owner_session_id, source_file_id, ordinal,
                        chunk_ordinal, role, timestamp_utc, segment_kind,
                        is_child, text)
                    VALUES(
                        $provider, $owner_session_id, $source_file_id, $ordinal,
                        $chunk_ordinal, $role, $timestamp_utc, $segment_kind,
                        $is_child, $text)
                    ON CONFLICT(source_file_id, ordinal, chunk_ordinal) DO UPDATE SET
                        role=excluded.role,
                        timestamp_utc=excluded.timestamp_utc,
                        segment_kind=excluded.segment_kind,
                        is_child=excluded.is_child,
                        text=excluded.text;
                    """;
                insert.Parameters.AddWithValue("$provider", (int)source.Owner.Provider);
                insert.Parameters.AddWithValue(
                    "$owner_session_id",
                    source.Owner.SessionId.ToString("D"));
                insert.Parameters.AddWithValue("$source_file_id", sourceId);
                insert.Parameters.AddWithValue("$ordinal", segment.Ordinal);
                insert.Parameters.AddWithValue("$chunk_ordinal", chunkOrdinal++);
                insert.Parameters.AddWithValue("$role", (int)segment.Role);
                insert.Parameters.AddWithValue(
                    "$timestamp_utc",
                    segment.TimestampUtc?.ToString("O", CultureInfo.InvariantCulture)
                        ?? (object)DBNull.Value);
                insert.Parameters.AddWithValue("$segment_kind", (int)segment.Kind);
                insert.Parameters.AddWithValue("$is_child", segment.IsChild ? 1 : 0);
                insert.Parameters.AddWithValue("$text", boundedText);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ReconcileSessionSourcesAsync(
        SessionIdentity identity,
        IReadOnlyCollection<string> currentCanonicalPaths,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        await CreateTemporaryStringSetAsync(
            connection,
            transaction,
            "current_source_paths",
            currentCanonicalPaths,
            cancellationToken).ConfigureAwait(false);

        await using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM source_files
            WHERE provider=$provider
              AND owner_session_id=$session_id
              AND canonical_path NOT IN (SELECT value FROM current_source_paths);
            """;
        delete.Parameters.AddWithValue("$provider", (int)identity.Provider);
        delete.Parameters.AddWithValue("$session_id", identity.SessionId.ToString("D"));
        int deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted == 0 ||
            await database.CheckpointAndTruncateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> ReconcileProviderGenerationAsync(
        SessionProvider provider,
        IReadOnlyCollection<Guid> discoveredSessionIds,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        await CreateTemporaryStringSetAsync(
            connection,
            transaction,
            "discovered_session_ids",
            discoveredSessionIds.Select(id => id.ToString("D")),
            cancellationToken).ConfigureAwait(false);

        int deleted = 0;
        await using (SqliteCommand purgeSources = connection.CreateCommand())
        {
            purgeSources.Transaction = transaction;
            purgeSources.CommandText = """
                DELETE FROM source_files
                WHERE provider=$provider
                  AND owner_session_id NOT IN (
                      SELECT value FROM discovered_session_ids
                  );
                """;
            purgeSources.Parameters.AddWithValue("$provider", (int)provider);
            deleted += await purgeSources.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand retainFavorites = connection.CreateCommand())
        {
            retainFavorites.Transaction = transaction;
            retainFavorites.CommandText = """
                UPDATE sessions
                SET source_present=0,
                    source_bytes=0
                WHERE provider=$provider
                  AND session_id NOT IN (
                      SELECT value FROM discovered_session_ids
                  )
                  AND EXISTS(
                      SELECT 1 FROM session_favorites AS sf
                      WHERE sf.provider=sessions.provider
                        AND sf.session_id=sessions.session_id
                  );
                """;
            retainFavorites.Parameters.AddWithValue("$provider", (int)provider);
            await retainFavorites.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand removeOthers = connection.CreateCommand())
        {
            removeOthers.Transaction = transaction;
            removeOthers.CommandText = """
                DELETE FROM sessions
                WHERE provider=$provider
                  AND session_id NOT IN (
                      SELECT value FROM discovered_session_ids
                  )
                  AND NOT EXISTS(
                      SELECT 1 FROM session_favorites AS sf
                      WHERE sf.provider=sessions.provider
                        AND sf.session_id=sessions.session_id
                  );
                """;
            removeOthers.Parameters.AddWithValue("$provider", (int)provider);
            deleted += await removeOthers.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted == 0 ||
            await database.CheckpointAndTruncateAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask UpsertSessionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionDocument session,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(
                provider, session_id, source_path, title, description, directory,
                branch, model, created_utc, updated_utc, source_bytes, archived,
                format_supported, source_present, parser_version,
                normalized_title, normalized_description, normalized_directory,
                normalized_branch, normalized_model)
            VALUES(
                $provider, $session_id, $source_path, $title, $description, $directory,
                $branch, $model, $created_utc, $updated_utc, $source_bytes, $archived,
                $format_supported, $source_present, $parser_version,
                $normalized_title, $normalized_description, $normalized_directory,
                $normalized_branch, $normalized_model)
            ON CONFLICT(provider, session_id) DO UPDATE SET
                source_path=excluded.source_path,
                title=excluded.title,
                description=excluded.description,
                directory=excluded.directory,
                branch=excluded.branch,
                model=excluded.model,
                created_utc=excluded.created_utc,
                updated_utc=excluded.updated_utc,
                source_bytes=excluded.source_bytes,
                archived=excluded.archived,
                format_supported=excluded.format_supported,
                source_present=excluded.source_present,
                parser_version=excluded.parser_version,
                normalized_title=excluded.normalized_title,
                normalized_description=excluded.normalized_description,
                normalized_directory=excluded.normalized_directory,
                normalized_branch=excluded.normalized_branch,
                normalized_model=excluded.normalized_model;
            """;
        command.Parameters.AddWithValue("$provider", (int)session.Identity.Provider);
        command.Parameters.AddWithValue("$session_id", session.Identity.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$source_path", session.SourcePath);
        command.Parameters.AddWithValue("$title", DisplayTextSanitizer.Sanitize(session.Title));
        command.Parameters.AddWithValue(
            "$description",
            TextNormalization.TruncateDescription(DisplayTextSanitizer.Sanitize(session.Description)));
        command.Parameters.AddWithValue("$directory", DisplayTextSanitizer.Sanitize(session.Directory));
        command.Parameters.AddWithValue("$branch", DbValue(session.Branch));
        command.Parameters.AddWithValue("$model", DbValue(session.Model));
        command.Parameters.AddWithValue(
            "$created_utc",
            session.CreatedUtc?.ToString("O", CultureInfo.InvariantCulture)
                ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(
            "$updated_utc",
            session.LastActivityUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source_bytes", session.SourceBytes);
        command.Parameters.AddWithValue("$archived", session.Archived ? 1 : 0);
        command.Parameters.AddWithValue("$format_supported", session.FormatSupported ? 1 : 0);
        command.Parameters.AddWithValue("$source_present", session.SourcePresent ? 1 : 0);
        command.Parameters.AddWithValue("$parser_version", session.ParserVersion);
        command.Parameters.AddWithValue(
            "$normalized_title",
            TextNormalization.NormalizeMetadata(session.Title));
        command.Parameters.AddWithValue(
            "$normalized_description",
            TextNormalization.NormalizeMetadata(session.Description));
        command.Parameters.AddWithValue(
            "$normalized_directory",
            TextNormalization.NormalizeMetadata(session.Directory));
        command.Parameters.AddWithValue(
            "$normalized_branch",
            DbValue(TextNormalization.NormalizeMetadata(session.Branch)));
        command.Parameters.AddWithValue(
            "$normalized_model",
            DbValue(TextNormalization.NormalizeMetadata(session.Model)));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> UpsertSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ProviderSource source,
        long completeOffset,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO source_files(
                provider, owner_session_id, canonical_path, relative_path,
                source_kind, child_session_id, archived, file_identity, length, last_write_utc,
                complete_offset, parser_version, status)
            VALUES(
                $provider, $owner_session_id, $canonical_path, $relative_path,
                $source_kind, $child_session_id, $archived, $file_identity, $length, $last_write_utc,
                $complete_offset, $parser_version, 0)
            ON CONFLICT(canonical_path) DO UPDATE SET
                provider=excluded.provider,
                owner_session_id=excluded.owner_session_id,
                relative_path=excluded.relative_path,
                source_kind=excluded.source_kind,
                child_session_id=excluded.child_session_id,
                archived=excluded.archived,
                file_identity=excluded.file_identity,
                length=excluded.length,
                last_write_utc=excluded.last_write_utc,
                complete_offset=excluded.complete_offset,
                parser_version=excluded.parser_version,
                status=0,
                retry_count=0,
                last_error=NULL
            RETURNING id;
            """;
        AddSourceParameters(command, source, completeOffset);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static void AddSourceParameters(
        SqliteCommand command,
        ProviderSource source,
        long completeOffset)
    {
        command.Parameters.AddWithValue("$provider", (int)source.Owner.Provider);
        command.Parameters.AddWithValue(
            "$owner_session_id",
            source.Owner.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$canonical_path", source.CanonicalPath);
        command.Parameters.AddWithValue("$relative_path", source.RelativePath);
        command.Parameters.AddWithValue("$source_kind", (int)source.Kind);
        command.Parameters.AddWithValue(
            "$child_session_id",
            source.ChildSessionId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$archived", source.Archived ? 1 : 0);
        command.Parameters.AddWithValue(
            "$file_identity",
            source.FileIdentity ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$length", source.Length);
        command.Parameters.AddWithValue(
            "$last_write_utc",
            source.LastWriteUtc.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$complete_offset", completeOffset);
        command.Parameters.AddWithValue("$parser_version", source.ParserVersion);
    }

    private static async ValueTask CreateTemporaryStringSetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        IEnumerable<string> values,
        CancellationToken cancellationToken)
    {
        string quotedName = tableName switch
        {
            "current_source_paths" => "current_source_paths",
            "discovered_session_ids" => "discovered_session_ids",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
        };

        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText = $"""
                CREATE TEMP TABLE IF NOT EXISTS {quotedName}(
                    value TEXT PRIMARY KEY
                ) WITHOUT ROWID;
                DELETE FROM {quotedName};
                """;
            await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (string value in values)
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT OR IGNORE INTO {quotedName}(value) VALUES($value);";
            insert.Parameters.AddWithValue("$value", value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static SessionDocument ReadSession(
        SqliteDataReader reader,
        SessionIdentity identity,
        int columnOffset) =>
        new(
            identity,
            reader.GetString(columnOffset),
            reader.GetString(columnOffset + 1),
            reader.GetString(columnOffset + 2),
            reader.GetString(columnOffset + 3),
            reader.IsDBNull(columnOffset + 4) ? null : reader.GetString(columnOffset + 4),
            reader.IsDBNull(columnOffset + 5) ? null : reader.GetString(columnOffset + 5),
            reader.IsDBNull(columnOffset + 6)
                ? null
                : ParseTimestamp(reader.GetString(columnOffset + 6)),
            ParseTimestamp(reader.GetString(columnOffset + 7)),
            reader.GetInt64(columnOffset + 8),
            reader.GetInt64(columnOffset + 9) != 0,
            reader.GetInt64(columnOffset + 10) != 0,
            reader.GetInt64(columnOffset + 11) != 0,
            reader.GetInt32(columnOffset + 12));

    private static object DbValue(string? value) =>
        string.IsNullOrEmpty(value) ? DBNull.Value : value;

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static IEnumerable<string> SplitUtf8(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            if (value.Length > 0)
            {
                yield return value;
            }

            yield break;
        }

        StringBuilder chunk = new();
        int chunkBytes = 0;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (chunkBytes > 0 && chunkBytes + rune.Utf8SequenceLength > maxBytes)
            {
                yield return chunk.ToString();
                chunk.Clear();
                chunkBytes = 0;
            }

            chunk.Append(rune.ToString());
            chunkBytes += rune.Utf8SequenceLength;
        }

        if (chunk.Length > 0)
        {
            yield return chunk.ToString();
        }
    }
}

public sealed record SourceIndexState(
    long Length,
    DateTimeOffset LastWriteUtc,
    long CompleteOffset,
    int ParserVersion,
    int Status,
    string? FileIdentity);
