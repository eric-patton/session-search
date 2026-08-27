using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Search;
using SessionSearch.Core.Sessions;
using SessionSearch.Core.Text;
using SessionSearch.Infrastructure.Indexing;
using SessionSearch.Infrastructure.Search;
using SessionSearch.Infrastructure.Storage;
using SessionSearch.IntegrationTests.Storage;

namespace SessionSearch.IntegrationTests.Indexing;

public sealed class IndexingCoordinatorTests
{
    // feat-001/AC-2 feat-001/AC-13
    [Fact]
    public async Task Feat001Ac2PublishesEverySessionMetadataBeforeReadingTranscriptContent()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        MetadataFirstAdapter adapter = new(database, workspace.Root);
        IndexingCoordinator coordinator = new(
            database,
            [new ProviderRegistration(adapter, workspace.Root)]);

        IndexingReport report = await coordinator.ReconcileAsync(
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, report.DiscoveredSessions);
        Assert.Equal(2, report.CompletedSessions);
        Assert.Equal(2, adapter.ReadCount);
        Assert.True(report.MetadataElapsed <= report.Elapsed);
    }

    // feat-001/AC-13 feat-001/AC-17
    [Fact]
    public async Task Feat001Ac13StorageLimitKeepsMetadataUsableAndStopsContentAtBoundary()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        MetadataFirstAdapter adapter = new(database, workspace.Root);
        IndexingCoordinator coordinator = new(
            database,
            [new ProviderRegistration(adapter, workspace.Root)],
            new BlockedStorageGuard());

        IndexingReport report = await coordinator.ReconcileAsync(
            progress: null,
            TestContext.Current.CancellationToken);
        SessionSearchPage browse = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse(string.Empty).Query!),
            TestContext.Current.CancellationToken);

        Assert.True(report.IsPartial);
        Assert.Equal(2, browse.TotalCount);
        Assert.Equal(0, adapter.ReadCount);
        Assert.Equal(0, report.ChangedSources);
    }

    // feat-001/AC-1 feat-001/AC-6 feat-001/AC-13 feat-001/AC-17
    [Fact]
    public async Task Feat001Ac13CommitsAProviderSessionAndSkipsUnchangedSources()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        FakeProviderAdapter adapter = new();
        IndexingCoordinator coordinator = new(
            database,
            [new ProviderRegistration(adapter, workspace.Root)]);

        IndexingReport first = await coordinator.ReconcileAsync(
            progress: null,
            TestContext.Current.CancellationToken);
        IndexingReport second = await coordinator.ReconcileAsync(
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, first.CompletedSessions);
        Assert.Equal(2, first.ChangedSources);
        Assert.False(first.IsPartial);
        Assert.Equal(0, second.ChangedSources);
        Assert.Equal(2, adapter.ReadCount);

        SessionSearchPage title = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("Pinned fixture title").Query!),
            TestContext.Current.CancellationToken);
        Assert.Equal("Pinned fixture title", Assert.Single(title.Results).Session.Title);
        Assert.Equal("Latest human request", title.Results[0].Session.Description);

        SessionSearchPage child = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("copper nebula").Query!),
            TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(child.Results).SnippetFromChild);

        SessionSearchPage crossRecordPhrase = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("\"request Latest\"").Query!),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, crossRecordPhrase.TotalCount);

        SessionSearchPage sameRecordPhrase = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("\"First human\"").Query!),
            TestContext.Current.CancellationToken);
        Assert.Single(sameRecordPhrase.Results);

        SessionSearchPage crossRecordAtoms = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("First Latest").Query!),
            TestContext.Current.CancellationToken);
        Assert.Single(crossRecordAtoms.Results);

        SessionSearchPage snippet = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("First").Query!),
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(
            ProviderLimits.SearchRecordBoundaryToken,
            Assert.Single(snippet.Results).Snippet,
            StringComparison.Ordinal);

        await using var connection = await database.OpenReadConnectionAsync(
            TestContext.Current.CancellationToken);
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT count(*) FROM segments;";
        long segmentCount = (long)(await count.ExecuteScalarAsync(
            TestContext.Current.CancellationToken) ?? -1L);
        Assert.Equal(2, segmentCount);
    }

    private sealed class FakeProviderAdapter : ISessionProviderAdapter
    {
        private readonly SessionIdentity identity = new(
            SessionProvider.ClaudeCode,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));
        private readonly DateTimeOffset timestamp = new(
            2026,
            8,
            26,
            10,
            0,
            0,
            TimeSpan.Zero);

        public SessionProvider Provider => SessionProvider.ClaudeCode;

        public int ReadCount { get; private set; }

        public ValueTask<ProviderDiscoveryResult> DiscoverAsync(
            string rootPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderSource main = Source(rootPath, "main.jsonl", ProviderSourceKind.TopLevel);
            ProviderSource child = Source(rootPath, "agent-child.jsonl", ProviderSourceKind.Child);
            ProviderSessionSeed seed = new(
                identity,
                @"C:\repos\fixture",
                "main",
                "fixture-model",
                timestamp,
                timestamp.AddMinutes(2),
                Archived: false,
                FormatSupported: true,
                [main, child]);
            return ValueTask.FromResult(new ProviderDiscoveryResult([seed], [], false));
        }

        public ValueTask<ProviderReadResult> ReadAsync(
            ProviderSource source,
            long startOffset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(0, startOffset);
            ReadCount++;
            IReadOnlyList<ProviderRecord> records = source.Kind == ProviderSourceKind.TopLevel
                ?
                [
                    Record(source, 1, ProviderRecordKind.UserText, "First human request", UserTextKind.Human),
                    Record(source, 2, ProviderRecordKind.AiTitle, "Generated title"),
                    Record(source, 3, ProviderRecordKind.ExplicitName, "Pinned fixture title"),
                    Record(source, 4, ProviderRecordKind.UserText, "Latest human request", UserTextKind.Human),
                    Record(source, 5, ProviderRecordKind.UserText, "Synthetic control", UserTextKind.Synthetic),
                ]
                :
                [
                    Record(source, 1, ProviderRecordKind.AssistantText, "child copper nebula", isChild: true),
                ];
            return ValueTask.FromResult(new ProviderReadResult(
                records,
                [],
                source.Length,
                IsPartial: false));
        }

        private ProviderSource Source(
            string rootPath,
            string name,
            ProviderSourceKind kind) =>
            new(
                identity,
                Path.Combine(rootPath, name),
                name,
                kind,
                kind == ProviderSourceKind.Child ? Guid.NewGuid() : null,
                Archived: false,
                Length: 100,
                LastWriteUtc: timestamp,
                ParserVersion: 1);

        private ProviderRecord Record(
            ProviderSource source,
            long sequence,
            ProviderRecordKind kind,
            string text,
            UserTextKind? userKind = null,
            bool isChild = false) =>
            new(
                identity,
                source.RelativePath,
                sequence,
                timestamp.AddSeconds(sequence),
                kind,
                text,
                userKind,
                isChild);
    }

    private sealed class MetadataFirstAdapter(
        SessionDatabase database,
        string root) : ISessionProviderAdapter
    {
        private static readonly Guid FirstId =
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        private static readonly Guid SecondId =
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        private readonly DateTimeOffset timestamp = new(
            2026,
            8,
            26,
            10,
            0,
            0,
            TimeSpan.Zero);

        public SessionProvider Provider => SessionProvider.ClaudeCode;

        public int ReadCount { get; private set; }

        public ValueTask<ProviderDiscoveryResult> DiscoverAsync(
            string rootPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderSessionSeed[] sessions =
            [
                Seed(FirstId, "first.jsonl", timestamp),
                Seed(SecondId, "second.jsonl", timestamp.AddMinutes(1)),
            ];
            return ValueTask.FromResult(new ProviderDiscoveryResult(sessions, [], false));
        }

        public async ValueTask<ProviderReadResult> ReadAsync(
            ProviderSource source,
            long startOffset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            if (ReadCount == 1)
            {
                SessionSearchPage metadata = await new SessionSearchService(database).SearchAsync(
                    new SessionSearchRequest(QueryParser.Parse(string.Empty).Query!),
                    cancellationToken);
                Assert.Equal(2, metadata.TotalCount);
            }

            ProviderRecord record = new(
                source.Owner,
                source.RelativePath,
                1,
                timestamp,
                ProviderRecordKind.UserText,
                $"content for {source.Owner.SessionId:D}",
                UserTextKind.Human,
                IsChild: false);
            return new ProviderReadResult([record], [], source.Length, IsPartial: false);
        }

        private ProviderSessionSeed Seed(Guid id, string name, DateTimeOffset activity)
        {
            SessionIdentity identity = new(SessionProvider.ClaudeCode, id);
            ProviderSource source = new(
                identity,
                Path.Combine(root, name),
                name,
                ProviderSourceKind.TopLevel,
                ChildSessionId: null,
                Archived: false,
                Length: 100,
                activity,
                ParserVersion: 1);
            return new ProviderSessionSeed(
                identity,
                @"C:\repos\fixture",
                "main",
                "fixture-model",
                activity,
                activity,
                Archived: false,
                FormatSupported: true,
                [source]);
        }
    }

    private sealed class BlockedStorageGuard : IIndexStorageGuard
    {
        public IndexStorageCheck Check(string databasePath) => new(
            MayIndexContent: false,
            IndexBytes: PhysicalIndexStorageGuard.MaximumIndexBytes,
            AvailableFreeBytes: PhysicalIndexStorageGuard.MinimumFreeBytes,
            DiagnosticCode: "index-size-limit",
            Message: "Synthetic storage limit.");
    }
}
