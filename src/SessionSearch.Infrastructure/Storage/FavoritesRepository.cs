using System.Globalization;
using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;

namespace SessionSearch.Infrastructure.Storage;

public sealed class FavoritesRepository(SessionDatabase database)
{
    public async ValueTask<IReadOnlyList<string>> ListDirectoryFavoritesAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT display_path
            FROM directory_favorites
            ORDER BY path_key;
            """;
        List<string> paths = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    public async ValueTask SetSessionFavoriteAsync(
        SessionIdentity identity,
        bool isFavorite,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        if (isFavorite)
        {
            command.CommandText = """
                INSERT INTO session_favorites(provider, session_id, created_utc)
                VALUES ($provider, $session_id, $created_utc)
                ON CONFLICT(provider, session_id) DO NOTHING;
                """;
            command.Parameters.AddWithValue("$provider", (int)identity.Provider);
            command.Parameters.AddWithValue("$session_id", identity.SessionId.ToString("D"));
            command.Parameters.AddWithValue(
                "$created_utc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            command.CommandText = """
                DELETE FROM session_favorites
                WHERE provider=$provider AND session_id=$session_id;
                """;
            command.Parameters.AddWithValue("$provider", (int)identity.Provider);
            command.Parameters.AddWithValue("$session_id", identity.SessionId.ToString("D"));
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> IsSessionFavoriteAsync(
        SessionIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM session_favorites
                WHERE provider=$provider AND session_id=$session_id
            );
            """;
        command.Parameters.AddWithValue("$provider", (int)identity.Provider);
        command.Parameters.AddWithValue("$session_id", identity.SessionId.ToString("D"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    public async ValueTask SetDirectoryFavoriteAsync(
        string directoryPath,
        bool isFavorite,
        CancellationToken cancellationToken)
    {
        string displayPath = NormalizeDisplayPath(directoryPath);
        string key = CreatePathKey(displayPath);
        await using SqliteConnection connection = await database.OpenWriteConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;

        if (isFavorite)
        {
            command.CommandText = """
                INSERT INTO directory_favorites(path_key, display_path, created_utc)
                VALUES ($key, $display_path, $created_utc)
                ON CONFLICT(path_key) DO UPDATE SET display_path=excluded.display_path;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$display_path", displayPath);
            command.Parameters.AddWithValue(
                "$created_utc",
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        else
        {
            command.CommandText = "DELETE FROM directory_favorites WHERE path_key=$key;";
            command.Parameters.AddWithValue("$key", key);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> IsDirectoryFavoriteAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        string key = CreatePathKey(NormalizeDisplayPath(directoryPath));
        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM directory_favorites WHERE path_key=$key
            );
            """;
        command.Parameters.AddWithValue("$key", key);
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) != 0;
    }

    private static string NormalizeDisplayPath(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
    }

    private static string CreatePathKey(string displayPath) => displayPath.ToUpperInvariant();
}
