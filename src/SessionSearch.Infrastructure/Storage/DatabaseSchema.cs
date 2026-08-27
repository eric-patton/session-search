namespace SessionSearch.Infrastructure.Storage;

internal static class DatabaseSchema
{
    public const string VersionOneSql = """
        CREATE TABLE schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_utc TEXT NOT NULL
        ) STRICT;

        CREATE TABLE sessions (
            provider INTEGER NOT NULL,
            session_id TEXT NOT NULL,
            source_path TEXT NOT NULL,
            title TEXT NOT NULL,
            description TEXT NOT NULL,
            directory TEXT NOT NULL,
            branch TEXT,
            model TEXT,
            created_utc TEXT,
            updated_utc TEXT NOT NULL,
            source_bytes INTEGER NOT NULL DEFAULT 0,
            archived INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0, 1)),
            format_supported INTEGER NOT NULL DEFAULT 1 CHECK (format_supported IN (0, 1)),
            source_present INTEGER NOT NULL DEFAULT 1 CHECK (source_present IN (0, 1)),
            parser_version INTEGER NOT NULL,
            normalized_title TEXT NOT NULL,
            normalized_description TEXT NOT NULL,
            normalized_directory TEXT NOT NULL,
            normalized_branch TEXT,
            normalized_model TEXT,
            PRIMARY KEY (provider, session_id)
        ) STRICT, WITHOUT ROWID;

        CREATE INDEX sessions_updated_idx
            ON sessions(updated_utc DESC, provider, session_id);
        CREATE INDEX sessions_directory_idx
            ON sessions(normalized_directory, updated_utc DESC);

        CREATE TABLE source_files (
            id INTEGER PRIMARY KEY,
            provider INTEGER NOT NULL,
            owner_session_id TEXT NOT NULL,
            canonical_path TEXT NOT NULL UNIQUE,
            relative_path TEXT NOT NULL,
            source_kind INTEGER NOT NULL,
            child_session_id TEXT,
            archived INTEGER NOT NULL DEFAULT 0 CHECK (archived IN (0, 1)),
            file_identity TEXT,
            length INTEGER NOT NULL,
            last_write_utc TEXT NOT NULL,
            complete_offset INTEGER NOT NULL DEFAULT 0,
            parser_version INTEGER NOT NULL,
            status INTEGER NOT NULL DEFAULT 0,
            retry_count INTEGER NOT NULL DEFAULT 0,
            last_error TEXT,
            FOREIGN KEY (provider, owner_session_id)
                REFERENCES sessions(provider, session_id)
                ON DELETE CASCADE
        ) STRICT;

        CREATE INDEX source_files_owner_idx
            ON source_files(provider, owner_session_id);

        CREATE TABLE segments (
            id INTEGER PRIMARY KEY,
            provider INTEGER NOT NULL,
            owner_session_id TEXT NOT NULL,
            source_file_id INTEGER NOT NULL,
            ordinal INTEGER NOT NULL,
            chunk_ordinal INTEGER NOT NULL DEFAULT 0,
            role INTEGER NOT NULL,
            timestamp_utc TEXT,
            segment_kind INTEGER NOT NULL,
            is_child INTEGER NOT NULL CHECK (is_child IN (0, 1)),
            text TEXT NOT NULL,
            UNIQUE (source_file_id, ordinal, chunk_ordinal),
            FOREIGN KEY (provider, owner_session_id)
                REFERENCES sessions(provider, session_id)
                ON DELETE CASCADE,
            FOREIGN KEY (source_file_id)
                REFERENCES source_files(id)
                ON DELETE CASCADE
        ) STRICT;

        CREATE VIRTUAL TABLE transcript_fts USING fts5(
            text,
            content='segments',
            content_rowid='id',
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER segments_after_insert AFTER INSERT ON segments BEGIN
            INSERT INTO transcript_fts(rowid, text) VALUES (new.id, new.text);
        END;
        CREATE TRIGGER segments_after_delete AFTER DELETE ON segments BEGIN
            INSERT INTO transcript_fts(transcript_fts, rowid, text)
                VALUES ('delete', old.id, old.text);
        END;
        CREATE TRIGGER segments_after_update AFTER UPDATE ON segments BEGIN
            INSERT INTO transcript_fts(transcript_fts, rowid, text)
                VALUES ('delete', old.id, old.text);
            INSERT INTO transcript_fts(rowid, text) VALUES (new.id, new.text);
        END;

        CREATE TABLE session_favorites (
            provider INTEGER NOT NULL,
            session_id TEXT NOT NULL,
            created_utc TEXT NOT NULL,
            PRIMARY KEY (provider, session_id)
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE directory_favorites (
            path_key TEXT PRIMARY KEY,
            display_path TEXT NOT NULL,
            created_utc TEXT NOT NULL
        ) STRICT, WITHOUT ROWID;

        CREATE TABLE diagnostics (
            id INTEGER PRIMARY KEY,
            occurred_utc TEXT NOT NULL,
            provider INTEGER,
            severity INTEGER NOT NULL,
            code TEXT NOT NULL,
            source_alias TEXT NOT NULL,
            message TEXT NOT NULL,
            parser_version INTEGER,
            retry_state INTEGER NOT NULL DEFAULT 0,
            elapsed_ms INTEGER,
            exception_type TEXT
        ) STRICT;

        CREATE INDEX diagnostics_time_idx ON diagnostics(occurred_utc DESC);

        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        ) STRICT, WITHOUT ROWID;

        INSERT INTO transcript_fts(transcript_fts, rank)
            VALUES ('secure-delete', 1);
        INSERT INTO schema_migrations(version, applied_utc)
            VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
        PRAGMA application_id=1397966163;
        PRAGMA user_version=1;
        """;

    public static IReadOnlySet<string> ExpectedObjects { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "diagnostics",
            "diagnostics_time_idx",
            "directory_favorites",
            "schema_migrations",
            "segments",
            "segments_after_delete",
            "segments_after_insert",
            "segments_after_update",
            "session_favorites",
            "sessions",
            "sessions_directory_idx",
            "sessions_updated_idx",
            "settings",
            "source_files",
            "source_files_owner_idx",
            "transcript_fts",
            "transcript_fts_config",
            "transcript_fts_data",
            "transcript_fts_docsize",
            "transcript_fts_idx",
        };
}
