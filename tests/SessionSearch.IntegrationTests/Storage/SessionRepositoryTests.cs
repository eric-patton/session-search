using System.Text;
using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Sessions;
using SessionSearch.Infrastructure.Storage;

namespace SessionSearch.IntegrationTests.Storage;

public sealed class SessionRepositoryTests
{
    // feat-001/AC-13
    [Fact]
    public async Task Feat001Ac13StoresAllTextInUtf8ChunksNoLargerThan64Kib()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        SessionIdentity identity = new(
            SessionProvider.Codex,
            Guid.Parse("88888888-8888-8888-8888-888888888888"));
        DateTimeOffset timestamp = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        SessionDocument session = new(
            identity,
            Path.Combine(workspace.Root, "large.jsonl"),
            "Large fixture",
            "Chunk verification",
            workspace.Root,
            null,
            "fixture-model",
            timestamp,
            timestamp,
            200_000,
            Archived: false,
            FormatSupported: true,
            SourcePresent: true,
            ParserVersion: 1);
        ProviderSource source = new(
            identity,
            session.SourcePath,
            "large.jsonl",
            ProviderSourceKind.TopLevel,
            null,
            Archived: false,
            Length: session.SourceBytes,
            LastWriteUtc: timestamp,
            ParserVersion: 1);
        string text = string.Concat(
            new string('a', ProviderLimits.MaxStoredSegmentBytes - 1),
            "😀",
            new string('b', ProviderLimits.MaxStoredSegmentBytes + 17));

        await repository.UpsertSessionAsync(session, TestContext.Current.CancellationToken);
        await repository.ReplaceSourceContentAsync(
            source,
            [
                new SessionSegment(
                    1,
                    ProviderRecordKind.AssistantText,
                    timestamp,
                    ProviderRecordKind.AssistantText,
                    IsChild: false,
                    text),
            ],
            completeOffset: source.Length,
            TestContext.Current.CancellationToken);

        await using SqliteConnection connection = await database.OpenReadConnectionAsync(
            TestContext.Current.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM segments ORDER BY chunk_ordinal;";
        List<string> chunks = [];
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            string chunk = reader.GetString(0);
            Assert.InRange(
                Encoding.UTF8.GetByteCount(chunk),
                1,
                ProviderLimits.MaxStoredSegmentBytes);
            chunks.Add(chunk);
        }

        Assert.True(chunks.Count >= 3);
        Assert.Equal(text, string.Concat(chunks));
    }

    // feat-001/AC-19
    [Fact]
    public async Task Feat001Ac19PurgesRemovedSentinelFromClosedDatabaseAndSidecars()
    {
        using TestWorkspace workspace = new();
        string sentinel = "PURGE_SENTINEL_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        DateTimeOffset timestamp = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        SessionIdentity identity = new(
            SessionProvider.Codex,
            Guid.Parse("77777777-7777-7777-7777-777777777777"));

        await using (SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken))
        {
            SessionRepository repository = new(database);
            SessionDocument session = new(
                identity,
                Path.Combine(workspace.Root, "purge.jsonl"),
                "Purge fixture",
                "Synthetic purge verification",
                workspace.Root,
                null,
                null,
                timestamp,
                timestamp,
                100,
                Archived: false,
                FormatSupported: true,
                SourcePresent: true,
                ParserVersion: 1);
            ProviderSource source = new(
                identity,
                session.SourcePath,
                "purge.jsonl",
                ProviderSourceKind.TopLevel,
                null,
                Archived: false,
                Length: 100,
                LastWriteUtc: timestamp,
                ParserVersion: 1);
            await repository.UpsertSessionAsync(session, TestContext.Current.CancellationToken);
            await repository.ReplaceSourceContentAsync(
                source,
                [
                    new SessionSegment(
                        1,
                        ProviderRecordKind.AssistantText,
                        timestamp,
                        ProviderRecordKind.AssistantText,
                        IsChild: false,
                        sentinel),
                ],
                completeOffset: 100,
                TestContext.Current.CancellationToken);

            bool purgeComplete = await repository.ReconcileProviderGenerationAsync(
                SessionProvider.Codex,
                [],
                TestContext.Current.CancellationToken);
            Assert.True(purgeComplete);
        }

        SqliteConnection.ClearAllPools();
        byte[] sentinelBytes = Encoding.UTF8.GetBytes(sentinel);
        foreach (string path in
            new[]
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
}
