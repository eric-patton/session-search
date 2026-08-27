using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Sessions;
using SessionSearch.Infrastructure.Storage;

namespace SessionSearch.IntegrationTests.Storage;

public sealed class SessionDatabaseTests
{
    // feat-001/AC-19 feat-001/AC-20
    [Fact]
    public async Task Feat001Ac19CreatesHardenedFtsDatabase()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            TestContext.Current.CancellationToken);

        Assert.Equal(
            SessionDatabase.ApplicationId,
            await ScalarLongAsync(connection, "PRAGMA application_id;"));
        Assert.Equal(
            SessionDatabase.SchemaVersion,
            await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(0, await ScalarLongAsync(connection, "PRAGMA trusted_schema;"));
        Assert.Equal(0, await ScalarLongAsync(connection, "PRAGMA mmap_size;"));
        Assert.Equal(1, await ScalarLongAsync(connection, "PRAGMA foreign_keys;"));

        await using SqliteCommand fts = connection.CreateCommand();
        fts.CommandText = "SELECT count(*) FROM transcript_fts WHERE transcript_fts MATCH 'fixture';";
        Assert.Equal(
            0L,
            (long)(await fts.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? -1L));

        SessionDatabaseValidation validation = await database.ValidateAsync(
            includeFtsIntegrityCheck: true,
            TestContext.Current.CancellationToken);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
    }

    // feat-001/AC-19
    [Fact]
    public async Task Feat001Ac19RejectsUnexpectedApplicationId()
    {
        using TestWorkspace workspace = new();
        SqliteBootstrap.Initialize();
        await using (SqliteConnection connection = new(
            $"Data Source={workspace.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA application_id=1234; CREATE TABLE hostile(value TEXT);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        SessionDatabaseException error = await Assert.ThrowsAsync<SessionDatabaseException>(
            async () => await SessionDatabase.CreateAsync(
                workspace.DatabasePath,
                protectDirectory: false,
                TestContext.Current.CancellationToken));

        Assert.Contains("application ID", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // feat-001/AC-19
    [Fact]
    public async Task Feat001Ac19RollsBackAnInjectedMigrationFailure()
    {
        using TestWorkspace workspace = new();

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await SessionDatabase.CreateAsync(
                workspace.DatabasePath,
                protectDirectory: false,
                TestContext.Current.CancellationToken,
                static (_, _, _) => throw new InvalidOperationException("Injected migration failure.")));

        SqliteBootstrap.Initialize();
        await using SqliteConnection connection = new(
            $"Data Source={workspace.DatabasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, await ScalarLongAsync(connection, "PRAGMA application_id;"));
        Assert.Equal(0, await ScalarLongAsync(connection, "PRAGMA user_version;"));
        Assert.Equal(
            0,
            await ScalarLongAsync(
                connection,
                "SELECT count(*) FROM sqlite_schema WHERE name='sessions';"));
    }

    // feat-001/AC-19
    [Fact]
    public async Task Feat001Ac19FailedExistingDatabaseMigrationPreservesBytesAndUsability()
    {
        using TestWorkspace workspace = new();
        await using (SessionDatabase original = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken))
        {
            Assert.NotNull(original);
        }

        SqliteConnection.ClearAllPools();
        await using (SqliteConnection seedConnection = new(
            $"Data Source={workspace.DatabasePath};Pooling=False"))
        {
            await seedConnection.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand seed = seedConnection.CreateCommand();
            seed.CommandText = """
                INSERT INTO settings(key, value) VALUES('migration-sentinel', 'present');
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        byte[] before = await File.ReadAllBytesAsync(
            workspace.DatabasePath,
            TestContext.Current.CancellationToken);
        string beforeHash = Convert.ToHexString(SHA256.HashData(before));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await SessionDatabase.CreateAsync(
                workspace.DatabasePath,
                protectDirectory: false,
                TestContext.Current.CancellationToken,
                static async (connection, transaction, cancellationToken) =>
                {
                    await using SqliteCommand command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        CREATE TABLE migration_should_rollback(value TEXT);
                        PRAGMA user_version=2;
                        """;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                    throw new InvalidOperationException("Injected existing migration failure.");
                }));

        SqliteConnection.ClearAllPools();
        byte[] after = await File.ReadAllBytesAsync(
            workspace.DatabasePath,
            TestContext.Current.CancellationToken);
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(beforeHash, Convert.ToHexString(SHA256.HashData(after)));

        await using SessionDatabase reopened = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        await using SqliteConnection read = await reopened.OpenReadConnectionAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(SessionDatabase.SchemaVersion, await ScalarLongAsync(read, "PRAGMA user_version;"));
        Assert.Equal(
            0,
            await ScalarLongAsync(
                read,
                "SELECT count(*) FROM sqlite_schema WHERE name='migration_should_rollback';"));
        Assert.Equal(
            1,
            await ScalarLongAsync(
                read,
                "SELECT count(*) FROM settings WHERE key='migration-sentinel' AND value='present';"));
    }

    // feat-001/AC-7 feat-001/AC-19
    [Fact]
    public async Task Feat001Ac19ScheduledPurgeCreatesCleanIndexAndRetainsOnlyFavoriteMetadata()
    {
        using TestWorkspace workspace = new();
        SessionIdentity identity = new(
            SessionProvider.ClaudeCode,
            Guid.Parse("12345678-1234-1234-1234-123456789abc"));
        string directory = @"C:\repos\favorite-rebuild";
        string sentinel = "CLEAN_REBUILD_SENTINEL_" + Guid.NewGuid().ToString("N");
        DateTimeOffset timestamp = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        SessionDocument document = new(
            identity,
            Path.Combine(workspace.Root, "source.jsonl"),
            "Favorite rebuild fixture",
            "Favorite metadata survives",
            directory,
            "main",
            "fixture-model",
            timestamp,
            timestamp,
            SourceBytes: 100,
            Archived: false,
            FormatSupported: true,
            SourcePresent: true,
            ParserVersion: 1);
        ProviderSource source = new(
            identity,
            document.SourcePath,
            "source.jsonl",
            ProviderSourceKind.TopLevel,
            ChildSessionId: null,
            Archived: false,
            Length: 100,
            timestamp,
            ParserVersion: 1);

        await using (SessionDatabase original = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken))
        {
            SessionRepository sessions = new(original);
            await sessions.UpsertSessionAsync(document, TestContext.Current.CancellationToken);
            await sessions.ReplaceSourceContentAsync(
                document,
                source,
                [new SessionSegment(
                    1,
                    ProviderRecordKind.UserText,
                    timestamp,
                    ProviderRecordKind.UserText,
                    IsChild: false,
                    sentinel)],
                completeOffset: 100,
                TestContext.Current.CancellationToken);
            FavoritesRepository favorites = new(original);
            await favorites.SetSessionFavoriteAsync(
                identity,
                isFavorite: true,
                TestContext.Current.CancellationToken);
            await favorites.SetDirectoryFavoriteAsync(
                directory,
                isFavorite: true,
                TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();
        await using (SqliteConnection schedule = new(
            $"Data Source={workspace.DatabasePath};Pooling=False"))
        {
            await schedule.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = schedule.CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO settings(key, value)
                VALUES('clean_rebuild_required', '1');
                PRAGMA wal_checkpoint(TRUNCATE);
                """;
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using SessionDatabase rebuilt = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionDocument retained = Assert.IsType<SessionDocument>(
            await new SessionRepository(rebuilt).FindSessionAsync(
                identity,
                TestContext.Current.CancellationToken));
        FavoritesRepository rebuiltFavorites = new(rebuilt);

        Assert.Equal("Favorite rebuild fixture", retained.Title);
        Assert.False(retained.SourcePresent);
        Assert.Equal(0, retained.SourceBytes);
        Assert.True(await rebuiltFavorites.IsSessionFavoriteAsync(
            identity,
            TestContext.Current.CancellationToken));
        Assert.True(await rebuiltFavorites.IsDirectoryFavoriteAsync(
            directory,
            TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            Directory.EnumerateFiles(workspace.Root),
            path => path.Contains(".rebuild-", StringComparison.Ordinal) ||
                path.Contains(".purge-old-", StringComparison.Ordinal));

        await rebuilt.DisposeAsync();
        SqliteConnection.ClearAllPools();
        byte[] sentinelBytes = System.Text.Encoding.UTF8.GetBytes(sentinel);
        foreach (string path in new[]
                 {
                     workspace.DatabasePath,
                     workspace.DatabasePath + "-wal",
                     workspace.DatabasePath + "-shm",
                 })
        {
            if (File.Exists(path))
            {
                Assert.Equal(-1, File.ReadAllBytes(path).AsSpan().IndexOf(sentinelBytes));
            }
        }
    }

    // feat-001/AC-17 feat-001/AC-19
    [Fact]
    public async Task Feat001Ac17DiagnosticsPersistParserRetryTimingAndSafeExceptionType()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        DiagnosticsRepository diagnostics = new(database);
        await diagnostics.AddRangeAsync(
            [new ProviderDiagnostic(
                SessionProvider.Codex,
                ProviderDiagnosticSeverity.Warning,
                "source-read",
                "sessions/year/file.jsonl",
                "The source could not be read.",
                ParserVersion: 3,
                RetryState: 2,
                ElapsedMilliseconds: 17,
                ExceptionType: nameof(IOException))],
            TestContext.Current.CancellationToken);

        PersistedProviderDiagnostic recent = Assert.Single(
            await diagnostics.ListRecentAsync(
                20,
                TestContext.Current.CancellationToken));
        Assert.Equal(SessionProvider.Codex, recent.Provider);
        Assert.Equal("source-read", recent.Code);
        Assert.Equal("sessions/year/file.jsonl", recent.SourceAlias);
        Assert.Equal(3, recent.ParserVersion);
        Assert.Equal(2, recent.RetryState);
        Assert.Equal(17, recent.ElapsedMilliseconds);
        Assert.Equal(nameof(IOException), recent.ExceptionType);

        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT parser_version, retry_state, elapsed_ms, exception_type
            FROM diagnostics
            WHERE code='source-read';
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(17, reader.GetInt64(2));
        Assert.Equal(nameof(IOException), reader.GetString(3));
    }

    // feat-001/AC-20
    [Fact]
    public async Task Feat001Ac20AppliesReducedRuntimeLimitsAndDisablesExtensions()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            TestContext.Current.CancellationToken);

        Assert.True(ReadLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_LENGTH) <= 32 * 1024 * 1024);
        Assert.True(ReadLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_SQL_LENGTH) <= 64 * 1024);
        Assert.True(ReadLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_COLUMN) <= 128);
        Assert.True(ReadLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_EXPR_DEPTH) <= 64);
        Assert.True(ReadLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_VARIABLE_NUMBER) <= 256);
        Assert.Equal(0, ReadLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_ATTACHED));
        Assert.Equal(0, ReadLimit(connection, SQLitePCL.raw.SQLITE_LIMIT_WORKER_THREADS));

        await using SqliteCommand extension = connection.CreateCommand();
        extension.CommandText = "SELECT load_extension('missing-extension');";
        await Assert.ThrowsAsync<SqliteException>(
            async () => await extension.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    // feat-001/AC-20
    [Fact]
    public async Task Feat001Ac20EveryNewConnectionRejectsSchemaTampering()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);

        SqliteConnection.ClearAllPools();
        await using (SqliteConnection tamper = new(
            $"Data Source={workspace.DatabasePath};Pooling=False"))
        {
            await tamper.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = tamper.CreateCommand();
            command.CommandText = "CREATE TABLE unexpected_fixture(value TEXT);";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        SessionDatabaseException error = await Assert.ThrowsAsync<SessionDatabaseException>(
            async () => await database.OpenReadConnectionAsync(
                TestContext.Current.CancellationToken));
        Assert.Contains("allowlist", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long> ScalarLongAsync(
        SqliteConnection connection,
        string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ReadLimit(SqliteConnection connection, int identifier) =>
        SQLitePCL.raw.sqlite3_limit(connection.Handle, identifier, -1);
}
