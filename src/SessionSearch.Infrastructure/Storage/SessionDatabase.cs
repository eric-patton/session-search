using System.Globalization;
using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;
using SessionSearch.Core.Sessions;

namespace SessionSearch.Infrastructure.Storage;

public delegate ValueTask MigrationHook(
    SqliteConnection connection,
    SqliteTransaction transaction,
    CancellationToken cancellationToken);

public sealed class SessionDatabaseException : Exception
{
    public SessionDatabaseException(string message)
        : base(message)
    {
    }
}

public sealed record SessionDatabaseValidation(
    bool IsValid,
    IReadOnlyList<string> Errors);

public sealed class SessionDatabase : IAsyncDisposable
{
    public const int ApplicationId = 1_397_966_163;
    public const int SchemaVersion = 1;

    private readonly string connectionString;
    private readonly bool protectDirectory;
    private readonly string protectedDirectory;
    private bool disposed;

    private SessionDatabase(string databasePath, bool protectDirectory)
    {
        DatabasePath = databasePath;
        this.protectDirectory = protectDirectory;
        protectedDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new SessionDatabaseException("The database path has no parent directory.");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string DatabasePath { get; }

    public static async ValueTask<SessionDatabase> CreateAsync(
        string databasePath,
        bool protectDirectory,
        CancellationToken cancellationToken,
        MigrationHook? migrationHook = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        SqliteBootstrap.Initialize();

        string fullPath;
        if (protectDirectory)
        {
            Windows.LocalPathValidation lexical = Windows.LocalPathPolicy.ValidateLexically(
                databasePath);
            if (!lexical.IsSafe)
            {
                throw new SessionDatabaseException(
                    $"The database path is unsafe: {lexical.Reason}");
            }

            fullPath = lexical.CanonicalPath!;
        }
        else
        {
            fullPath = Path.GetFullPath(databasePath);
        }

        string directory = Path.GetDirectoryName(fullPath)
            ?? throw new SessionDatabaseException("The database path has no parent directory.");

        if (protectDirectory)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException(
                    "Protected app storage requires Windows.");
            }

            AppDataSecurity.PrepareProtectedDirectory(directory);
            VerifyProtectedArtifacts(directory, fullPath);
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(fullPath) &&
            await RequiresProtectedCleanRebuildAsync(fullPath, cancellationToken)
                .ConfigureAwait(false))
        {
            await RebuildProtectedIndexAsync(
                fullPath,
                protectDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        SessionDatabase database = new(fullPath, protectDirectory);
        try
        {
            await database.InitializeAsync(migrationHook, cancellationToken).ConfigureAwait(false);
            database.VerifyProtectedArtifacts();
            return database;
        }
        catch
        {
            await database.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SqliteConnection> OpenReadConnectionAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SqliteConnectionStringBuilder builder = new(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
        };
        SqliteConnection connection = new(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(
                connection,
                queryOnly: true,
                cancellationToken).ConfigureAwait(false);
            await ValidateReadyConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<SessionDatabaseValidation> ValidateAsync(
        bool includeFtsIntegrityCheck,
        CancellationToken cancellationToken)
    {
        List<string> errors = [];
        await using SqliteConnection connection = await OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);

        string quickCheck = await ExecuteScalarTextAsync(
            connection,
            "PRAGMA quick_check;",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("SQLite quick_check did not return ok.");
        }

        IReadOnlySet<string> actualObjects = await ReadSchemaObjectsAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        string[] missing = DatabaseSchema.ExpectedObjects.Except(actualObjects).Order().ToArray();
        string[] unexpected = actualObjects.Except(DatabaseSchema.ExpectedObjects).Order().ToArray();
        if (missing.Length > 0)
        {
            errors.Add($"Missing schema objects: {string.Join(", ", missing)}.");
        }

        if (unexpected.Length > 0)
        {
            errors.Add($"Unexpected schema objects: {string.Join(", ", unexpected)}.");
        }

        if (includeFtsIntegrityCheck)
        {
            try
            {
                await ExecuteNonQueryAsync(
                    connection,
                    "INSERT INTO transcript_fts(transcript_fts) VALUES('integrity-check');",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                errors.Add("FTS integrity-check failed.");
            }
        }

        return new SessionDatabaseValidation(errors.Count == 0, errors);
    }

    internal async ValueTask<SqliteConnection> OpenWriteConnectionAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        SqliteConnection connection = new(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(
                connection,
                queryOnly: false,
                cancellationToken).ConfigureAwait(false);
            await ValidateReadyConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<bool> CheckpointAndTruncateAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        bool complete = await TryCheckpointAndTruncateAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        if (complete)
        {
            return true;
        }

        try
        {
            await using SqliteCommand schedule = connection.CreateCommand();
            schedule.CommandText = """
                INSERT INTO settings(key, value)
                VALUES('clean_rebuild_required', '1')
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            await schedule.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            return false;
        }

        return false;
    }

    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            ClearConnectionPool();
        }

        return ValueTask.CompletedTask;
    }

    public void ClearConnectionPool()
    {
        ClearPool(connectionString);

        SqliteConnectionStringBuilder readOnly = new(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
        };
        ClearPool(readOnly.ToString());
    }

    private static void ClearPool(string poolConnectionString)
    {
        using SqliteConnection poolKey = new(poolConnectionString);
        SqliteConnection.ClearPool(poolKey);
    }

    private async ValueTask InitializeAsync(
        MigrationHook? migrationHook,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(
            connection,
            queryOnly: false,
            cancellationToken).ConfigureAwait(false);

        long applicationId = await ExecuteScalarLongAsync(
            connection,
            "PRAGMA application_id;",
            cancellationToken).ConfigureAwait(false);
        long schemaVersion = await ExecuteScalarLongAsync(
            connection,
            "PRAGMA user_version;",
            cancellationToken).ConfigureAwait(false);
        long userObjectCount = await ExecuteScalarLongAsync(
            connection,
            "SELECT count(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';",
            cancellationToken).ConfigureAwait(false);

        if (applicationId != 0 && applicationId != ApplicationId)
        {
            throw new SessionDatabaseException(
                $"The database application ID {applicationId} is not recognized.");
        }

        if (applicationId == 0 && userObjectCount != 0)
        {
            throw new SessionDatabaseException(
                "The database has objects but no recognized application ID.");
        }

        if (schemaVersion > SchemaVersion)
        {
            throw new SessionDatabaseException(
                $"Database schema version {schemaVersion} is newer than supported version {SchemaVersion}.");
        }

        if (schemaVersion == 0)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = DatabaseSchema.VersionOneSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (migrationHook is not null)
            {
                await migrationHook(connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (migrationHook is not null)
        {
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
            await migrationHook(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await ValidateIdentityAsync(connection, cancellationToken).ConfigureAwait(false);
        SessionDatabaseValidation validation = await ValidateAsyncOnConnectionAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid)
        {
            throw new SessionDatabaseException(string.Join(" ", validation.Errors));
        }

        await CompleteScheduledPurgeAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> RequiresProtectedCleanRebuildAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 2,
        };
        try
        {
            await using SqliteConnection connection = new(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.EnableExtensions(enable: false);
            await ExecuteNonQueryAsync(
                connection,
                "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA mmap_size=0;",
                cancellationToken).ConfigureAwait(false);
            long hasSettings = await ExecuteScalarLongAsync(
                connection,
                "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='settings');",
                cancellationToken).ConfigureAwait(false);
            if (hasSettings == 0)
            {
                return false;
            }

            return await ExecuteScalarLongAsync(
                connection,
                "SELECT EXISTS(SELECT 1 FROM settings WHERE key='clean_rebuild_required');",
                cancellationToken).ConfigureAwait(false) != 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async ValueTask RebuildProtectedIndexAsync(
        string databasePath,
        bool protectDirectory,
        CancellationToken cancellationToken)
    {
        FavoriteRecoveryData recovery = await ReadFavoriteRecoveryDataAsync(
            databasePath,
            cancellationToken).ConfigureAwait(false);
        string token = Guid.NewGuid().ToString("N");
        string replacementPath = databasePath + ".rebuild-" + token;
        string backupPath = databasePath + ".purge-old-" + token;
        bool replaced = false;
        List<(string Original, string Backup)> movedSidecars = [];
        try
        {
            await using (SessionDatabase replacement = await CreateAsync(
                replacementPath,
                protectDirectory,
                cancellationToken).ConfigureAwait(false))
            {
                SessionRepository sessions = new(replacement);
                await sessions.UpsertSessionsAsync(
                    recovery.Sessions,
                    cancellationToken).ConfigureAwait(false);
                FavoritesRepository favorites = new(replacement);
                foreach (SessionIdentity identity in recovery.SessionFavorites)
                {
                    await favorites.SetSessionFavoriteAsync(
                        identity,
                        isFavorite: true,
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (string favoriteDirectory in recovery.DirectoryFavorites)
                {
                    await favorites.SetDirectoryFavoriteAsync(
                        favoriteDirectory,
                        isFavorite: true,
                        cancellationToken).ConfigureAwait(false);
                }

                if (!await replacement.CheckpointAndTruncateAsync(cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw new SessionDatabaseException(
                        "The protected replacement index could not be finalized.");
                }
            }

            SqliteConnection.ClearAllPools();
            DeleteExactFile(replacementPath + "-wal");
            DeleteExactFile(replacementPath + "-shm");
            MoveSidecarForReplacement(databasePath + "-wal", backupPath + "-wal", movedSidecars);
            MoveSidecarForReplacement(databasePath + "-shm", backupPath + "-shm", movedSidecars);
            File.Replace(replacementPath, databasePath, backupPath, ignoreMetadataErrors: true);
            replaced = true;
            DeleteExactFile(backupPath);
            foreach ((_, string backup) in movedSidecars)
            {
                DeleteExactFile(backup);
            }

            string parentDirectory = Path.GetDirectoryName(databasePath)
                ?? throw new SessionDatabaseException("The database path has no parent directory.");
            if (protectDirectory)
            {
                VerifyProtectedArtifacts(parentDirectory, databasePath);
            }
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            if (!replaced)
            {
                for (int index = movedSidecars.Count - 1; index >= 0; index--)
                {
                    (string original, string backup) = movedSidecars[index];
                    if (File.Exists(backup) && !File.Exists(original))
                    {
                        File.Move(backup, original);
                    }
                }
            }

            DeleteExactFile(replacementPath);
            DeleteExactFile(replacementPath + "-wal");
            DeleteExactFile(replacementPath + "-shm");
            throw;
        }
    }

    private static async ValueTask<FavoriteRecoveryData> ReadFavoriteRecoveryDataAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5,
        };
        List<SessionDocument> sessions = [];
        List<SessionIdentity> sessionFavorites = [];
        List<string> directoryFavorites = [];
        await using SqliteConnection connection = new(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        connection.EnableExtensions(enable: false);
        await ExecuteNonQueryAsync(
            connection,
            "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA mmap_size=0;",
            cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT s.provider, s.session_id, s.source_path, s.title, s.description,
                       s.directory, s.branch, s.model, s.created_utc, s.updated_utc,
                       s.archived, s.format_supported, s.parser_version
                FROM sessions AS s
                JOIN session_favorites AS f
                  ON f.provider=s.provider AND f.session_id=s.session_id
                ORDER BY s.provider, s.session_id;
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                SessionIdentity identity = new(
                    (SessionProvider)reader.GetInt32(0),
                    Guid.Parse(reader.GetString(1)));
                sessions.Add(new SessionDocument(
                    identity,
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8)
                        ? null
                        : DateTimeOffset.Parse(
                            reader.GetString(8),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind),
                    DateTimeOffset.Parse(
                        reader.GetString(9),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                    SourceBytes: 0,
                    reader.GetInt64(10) != 0,
                    reader.GetInt64(11) != 0,
                    SourcePresent: false,
                    reader.GetInt32(12)));
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT provider, session_id FROM session_favorites ORDER BY provider, session_id;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sessionFavorites.Add(new SessionIdentity(
                    (SessionProvider)reader.GetInt32(0),
                    Guid.Parse(reader.GetString(1))));
            }
        }

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT display_path FROM directory_favorites ORDER BY path_key;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                directoryFavorites.Add(reader.GetString(0));
            }
        }

        return new FavoriteRecoveryData(sessions, sessionFavorites, directoryFavorites);
    }

    private static void MoveSidecarForReplacement(
        string original,
        string backup,
        List<(string Original, string Backup)> moved)
    {
        if (!File.Exists(original))
        {
            return;
        }

        File.Move(original, backup);
        moved.Add((original, backup));
    }

    private static void DeleteExactFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        bool queryOnly,
        CancellationToken cancellationToken)
    {
        connection.EnableExtensions(enable: false);
        ApplyRuntimeLimits(connection);

        string queryOnlyValue = queryOnly ? "ON" : "OFF";
        await ExecuteNonQueryAsync(
            connection,
            $"""
            PRAGMA trusted_schema=OFF;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-8192;
            PRAGMA secure_delete=ON;
            PRAGMA mmap_size=0;
            PRAGMA query_only={queryOnlyValue};
            """,
            cancellationToken).ConfigureAwait(false);

        if (!queryOnly)
        {
            string journalMode = await ExecuteScalarTextAsync(
                connection,
                "PRAGMA journal_mode=WAL;",
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new SessionDatabaseException("SQLite could not enable WAL mode.");
            }
        }
    }

    private static async ValueTask ValidateIdentityAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        long applicationId = await ExecuteScalarLongAsync(
            connection,
            "PRAGMA application_id;",
            cancellationToken).ConfigureAwait(false);
        long schemaVersion = await ExecuteScalarLongAsync(
            connection,
            "PRAGMA user_version;",
            cancellationToken).ConfigureAwait(false);

        if (applicationId != ApplicationId || schemaVersion != SchemaVersion)
        {
            throw new SessionDatabaseException(
                $"Database identity mismatch. Application ID {applicationId}, schema version {schemaVersion}.");
        }
    }

    private static async ValueTask ValidateReadyConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ValidateIdentityAsync(connection, cancellationToken).ConfigureAwait(false);
        IReadOnlySet<string> actualObjects = await ReadSchemaObjectsAsync(
            connection,
            cancellationToken).ConfigureAwait(false);
        if (!actualObjects.SetEquals(DatabaseSchema.ExpectedObjects))
        {
            throw new SessionDatabaseException(
                "The database schema object allowlist does not match.");
        }
    }

    private static void ApplyRuntimeLimits(SqliteConnection connection)
    {
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_LENGTH, 32 * 1024 * 1024);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_SQL_LENGTH, 64 * 1024);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_COLUMN, 128);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_EXPR_DEPTH, 64);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_COMPOUND_SELECT, 16);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_VDBE_OP, 250_000);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_FUNCTION_ARG, 32);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_ATTACHED, 0);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_LIKE_PATTERN_LENGTH, 2_048);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER, 256);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_TRIGGER_DEPTH, 16);
        SetRuntimeLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_WORKER_THREADS, 0);
    }

    private static void SetRuntimeLimit(
        SqliteConnection connection,
        int identifier,
        int ceiling)
    {
        SQLitePCL.raw.sqlite3_limit(connection.Handle, identifier, ceiling);
        int applied = SQLitePCL.raw.sqlite3_limit(connection.Handle, identifier, -1);
        if (applied > ceiling)
        {
            throw new SessionDatabaseException("SQLite did not apply a reduced runtime limit.");
        }
    }

    private static async ValueTask<SessionDatabaseValidation> ValidateAsyncOnConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        List<string> errors = [];
        IReadOnlySet<string> actualObjects = await ReadSchemaObjectsAsync(
            connection,
            cancellationToken).ConfigureAwait(false);

        if (!actualObjects.SetEquals(DatabaseSchema.ExpectedObjects))
        {
            string[] missing = DatabaseSchema.ExpectedObjects
                .Except(actualObjects)
                .Order()
                .ToArray();
            string[] unexpected = actualObjects
                .Except(DatabaseSchema.ExpectedObjects)
                .Order()
                .ToArray();
            errors.Add(
                $"The database schema object allowlist does not match. Missing: {string.Join(", ", missing)}. Unexpected: {string.Join(", ", unexpected)}.");
        }

        string quickCheck = await ExecuteScalarTextAsync(
            connection,
            "PRAGMA quick_check;",
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("SQLite quick_check did not return ok.");
        }

        return new SessionDatabaseValidation(errors.Count == 0, errors);
    }

    private static async ValueTask<IReadOnlySet<string>> ReadSchemaObjectsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> objects = new(StringComparer.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT name
            FROM sqlite_schema
            WHERE name IS NOT NULL
              AND name <> ''
              AND name NOT LIKE 'sqlite_%';
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            objects.Add(reader.GetString(0));
        }

        return objects;
    }

    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async ValueTask<string> ExecuteScalarTextAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async ValueTask ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask CompleteScheduledPurgeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        long required = await ExecuteScalarLongAsync(
            connection,
            "SELECT EXISTS(SELECT 1 FROM settings WHERE key='clean_rebuild_required');",
            cancellationToken).ConfigureAwait(false);
        if (required == 0)
        {
            return;
        }

        throw new SessionDatabaseException(
            "The database requires a protected clean rebuild before use.");
    }

    private static async ValueTask<bool> TryCheckpointAndTruncateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using SqliteDataReader reader = await checkpoint.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        long busy = reader.GetInt64(0);
        long remainingFrames = reader.GetInt64(1);
        return busy == 0 && remainingFrames == 0;
    }

    private void VerifyProtectedArtifacts()
    {
        if (protectDirectory)
        {
            VerifyProtectedArtifacts(protectedDirectory, DatabasePath);
        }
    }

    private static void VerifyProtectedArtifacts(string directory, string databasePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected app storage requires Windows.");
        }

        AppDataSecurity.VerifyProtectedFileIfExists(directory, databasePath);
        AppDataSecurity.VerifyProtectedFileIfExists(directory, databasePath + "-wal");
        AppDataSecurity.VerifyProtectedFileIfExists(directory, databasePath + "-shm");
    }

    private sealed record FavoriteRecoveryData(
        IReadOnlyList<SessionDocument> Sessions,
        IReadOnlyList<SessionIdentity> SessionFavorites,
        IReadOnlyList<string> DirectoryFavorites);
}
