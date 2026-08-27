using System.Globalization;
using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;

namespace SessionSearch.Infrastructure.Storage;

public sealed record PersistedProviderDiagnostic(
    DateTimeOffset OccurredUtc,
    SessionProvider? Provider,
    ProviderDiagnosticSeverity Severity,
    string Code,
    string SourceAlias,
    string Message,
    int? ParserVersion,
    int RetryState,
    long? ElapsedMilliseconds,
    string? ExceptionType);

public sealed class DiagnosticsRepository(SessionDatabase database)
{
    public async ValueTask<IReadOnlyList<PersistedProviderDiagnostic>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        int boundedLimit = Math.Clamp(limit, 1, 100);
        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                occurred_utc, provider, severity, code, source_alias, message,
                parser_version, retry_state, elapsed_ms, exception_type
            FROM diagnostics
            ORDER BY id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", boundedLimit);

        List<PersistedProviderDiagnostic> values = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values.Add(new PersistedProviderDiagnostic(
                DateTimeOffset.Parse(
                    reader.GetString(0),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.IsDBNull(1) ? null : (SessionProvider)reader.GetInt32(1),
                (ProviderDiagnosticSeverity)reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return values;
    }

    public async ValueTask AddRangeAsync(
        IEnumerable<ProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        ProviderDiagnostic[] values = diagnostics.Take(500).ToArray();
        if (values.Length == 0)
        {
            return;
        }

        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        foreach (ProviderDiagnostic diagnostic in values)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO diagnostics(
                    occurred_utc, provider, severity, code, source_alias, message,
                    parser_version, retry_state, elapsed_ms, exception_type)
                VALUES(
                    $occurred_utc, $provider, $severity, $code, $source_alias, $message,
                    $parser_version, $retry_state, $elapsed_ms, $exception_type);
                """;
            command.Parameters.AddWithValue(
                "$occurred_utc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$provider", (int)diagnostic.Provider);
            command.Parameters.AddWithValue("$severity", (int)diagnostic.Severity);
            command.Parameters.AddWithValue("$code", Bound(diagnostic.Code, 80));
            command.Parameters.AddWithValue("$source_alias", Bound(diagnostic.SourceAlias, 260));
            command.Parameters.AddWithValue("$message", Bound(diagnostic.Message, 500));
            command.Parameters.AddWithValue(
                "$parser_version",
                diagnostic.ParserVersion ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$retry_state", diagnostic.RetryState);
            command.Parameters.AddWithValue(
                "$elapsed_ms",
                diagnostic.ElapsedMilliseconds ?? (object)DBNull.Value);
            command.Parameters.AddWithValue(
                "$exception_type",
                diagnostic.ExceptionType is null
                    ? DBNull.Value
                    : Bound(diagnostic.ExceptionType, 120));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await TrimAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask TrimAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM diagnostics
            WHERE id NOT IN (
                SELECT id FROM diagnostics ORDER BY id DESC LIMIT 2000
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Bound(string value, int length) =>
        value.Length <= length ? value : value[..length];
}
