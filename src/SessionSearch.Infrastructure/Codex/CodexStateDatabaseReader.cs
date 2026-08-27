using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Infrastructure.Storage;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.Infrastructure.Codex;

internal sealed record CodexStateEnrichment(
    string? Name,
    string? Title,
    string? Branch,
    string? Model);

internal sealed record CodexStateDatabaseReadResult(
    IReadOnlyDictionary<Guid, CodexStateEnrichment> Threads,
    IReadOnlyList<ProviderDiagnostic> Diagnostics,
    bool IsPartial);

internal sealed class CodexStateDatabaseReader(LocalPathPolicy pathPolicy)
{
    private const int MaxStateCandidates = 16;
    private const int QueryBatchSize = 128;
    private const long MaxStateDatabaseBytes = 512L * 1024 * 1024;
    private const long MaxStateWalBytes = 1024L * 1024 * 1024;
    private static readonly string[] ExpectedThreadColumns =
        ["id", "title", "name", "git_branch", "model"];
    private static readonly string[] SnapshotSuffixes =
        [string.Empty, "-wal", "-shm", "-journal"];

    public async ValueTask<CodexStateDatabaseReadResult> ReadAsync(
        string canonicalRoot,
        IReadOnlyCollection<Guid> sessionIds,
        CancellationToken cancellationToken)
    {
        Guid[] ids = sessionIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(ProviderLimits.MaxCandidatesPerProvider)
            .ToArray();
        if (ids.Length == 0)
        {
            return Empty();
        }

        string? databasePath;
        try
        {
            databasePath = FindLatestStateDatabase(canonicalRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return Failure("codex.state.enumeration-failed", "The Codex state database could not be enumerated.");
        }

        if (databasePath is null)
        {
            return Empty();
        }

        LocalPathValidation databaseValidation = pathPolicy.ValidateExistingFile(
            databasePath,
            Path.GetFileName(databasePath),
            trustedRoot: canonicalRoot);
        if (!databaseValidation.IsSafe ||
            !SidecarsAreSafe(databaseValidation.CanonicalPath!, canonicalRoot))
        {
            return Failure("codex.state.path-unsafe", "The Codex state database path is unsafe.");
        }

        StateSnapshot? snapshot = null;
        try
        {
            snapshot = await CreateSnapshotAsync(
                databaseValidation.CanonicalPath!,
                cancellationToken).ConfigureAwait(false);
            SqliteBootstrap.Initialize();
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = snapshot.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 2,
            };
            await using SqliteConnection connection = new(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureReadOnlyAsync(connection, cancellationToken).ConfigureAwait(false);
            if (!await HasExpectedThreadColumnsAsync(connection, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Failure(
                    "codex.state.schema-unsupported",
                    "The Codex state database schema is not supported.");
            }

            Dictionary<Guid, CodexStateEnrichment> values = [];
            foreach (Guid[] batch in ids.Chunk(QueryBatchSize))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadBatchAsync(connection, batch, values, cancellationToken)
                    .ConfigureAwait(false);
            }

            return new CodexStateDatabaseReadResult(values, [], IsPartial: false);
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failure("codex.state.read-failed", "The Codex state database could not be read.");
        }
        finally
        {
            snapshot?.Dispose();
        }
    }

    private static string? FindLatestStateDatabase(string canonicalRoot)
    {
        List<(int Version, string Path)> candidates = [];
        foreach (string path in Directory.EnumerateFiles(
            canonicalRoot,
            "state_*.sqlite",
            SearchOption.TopDirectoryOnly))
        {
            if (candidates.Count >= MaxStateCandidates)
            {
                break;
            }

            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length > "state_".Length &&
                int.TryParse(
                    name["state_".Length..],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int version))
            {
                candidates.Add((version, path));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Version)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    private bool SidecarsAreSafe(string databasePath, string canonicalRoot)
    {
        string[] sidecarSuffixes = ["-wal", "-shm"];
        foreach (string suffix in sidecarSuffixes)
        {
            string path = databasePath + suffix;
            LocalPathValidation validation = pathPolicy.ValidateExistingFile(
                path,
                Path.GetFileName(path),
                trustedRoot: canonicalRoot);
            if (!validation.IsSafe && validation.Failure != LocalPathFailure.Missing)
            {
                return false;
            }
        }

        return true;
    }

    private static async ValueTask<StateSnapshot> CreateSnapshotAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        string snapshotDirectory = Path.Combine(
            Path.GetTempPath(),
            "SessionSearch",
            "StateSnapshots",
            Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            AppDataSecurity.PrepareProtectedDirectory(snapshotDirectory);
        }
        else
        {
            Directory.CreateDirectory(snapshotDirectory);
        }
        string snapshotDatabasePath = Path.Combine(snapshotDirectory, "state.sqlite");
        try
        {
            string walPath = databasePath + "-wal";
            SourceStamp databaseStamp = ReadSourceStamp(databasePath, MaxStateDatabaseBytes);
            SourceStamp? walStamp = File.Exists(walPath)
                ? ReadSourceStamp(walPath, MaxStateWalBytes)
                : null;

            await CopySharedFileAsync(
                databasePath,
                snapshotDatabasePath,
                cancellationToken).ConfigureAwait(false);
            if (walStamp is not null)
            {
                await CopySharedFileAsync(
                    walPath,
                    snapshotDatabasePath + "-wal",
                    cancellationToken).ConfigureAwait(false);
            }

            if (databaseStamp != ReadSourceStamp(databasePath, MaxStateDatabaseBytes) ||
                walStamp != ReadOptionalSourceStamp(walPath, MaxStateWalBytes))
            {
                throw new IOException("The Codex state database changed while it was copied.");
            }

            return new StateSnapshot(snapshotDirectory, snapshotDatabasePath);
        }
        catch
        {
            DeleteSnapshotFiles(snapshotDirectory, snapshotDatabasePath);
            throw;
        }
    }

    private static SourceStamp ReadSourceStamp(string path, long maximumBytes)
    {
        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists || file.Length < 0 || file.Length > maximumBytes)
        {
            throw new IOException("The Codex state database has an unsupported size.");
        }

        return new SourceStamp(file.Length, file.LastWriteTimeUtc);
    }

    private static SourceStamp? ReadOptionalSourceStamp(string path, long maximumBytes) =>
        File.Exists(path) ? ReadSourceStamp(path, maximumBytes) : null;

    private static async ValueTask CopySharedFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream destination = new(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void DeleteSnapshotFiles(string directoryPath, string databasePath)
    {
        foreach (string suffix in SnapshotSuffixes)
        {
            try
            {
                File.Delete(databasePath + suffix);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(directoryPath, recursive: false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static async ValueTask ConfigureReadOnlyAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        connection.EnableExtensions(enable: false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA query_only=ON;
            PRAGMA trusted_schema=OFF;
            PRAGMA mmap_size=0;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> HasExpectedThreadColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> columns = new(StringComparer.Ordinal);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(threads);";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return ExpectedThreadColumns.All(columns.Contains);
    }

    private static async ValueTask ReadBatchAsync(
        SqliteConnection connection,
        Guid[] ids,
        Dictionary<Guid, CodexStateEnrichment> values,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        string[] parameterNames = ids
            .Select((_, index) => $"$id_{index}")
            .ToArray();
        command.CommandText = $"""
            SELECT id, NULLIF(name, ''), NULLIF(title, ''),
                   NULLIF(git_branch, ''), NULLIF(model, '')
            FROM threads
            WHERE id IN ({string.Join(", ", parameterNames)});
            """;
        for (int index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], ids[index].ToString("D"));
        }

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid id))
            {
                continue;
            }

            values[id] = new CodexStateEnrichment(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4));
        }
    }

    private static CodexStateDatabaseReadResult Empty() =>
        new(new Dictionary<Guid, CodexStateEnrichment>(), [], IsPartial: false);

    private static CodexStateDatabaseReadResult Failure(string code, string message) =>
        new(
            new Dictionary<Guid, CodexStateEnrichment>(),
            [new ProviderDiagnostic(
                SessionProvider.Codex,
                ProviderDiagnosticSeverity.Warning,
                code,
                "state-database",
                message)],
            IsPartial: true);

    private sealed class StateSnapshot(string directoryPath, string databasePath) : IDisposable
    {
        public string DatabasePath { get; } = databasePath;

        public void Dispose() => DeleteSnapshotFiles(directoryPath, DatabasePath);
    }

    private readonly record struct SourceStamp(long Length, DateTime LastWriteUtc);
}
