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

public sealed class IndexRetentionTests
{
    // feat-001/AC-13 feat-001/AC-17
    [Fact]
    public async Task Feat001Ac17PartialDiscoveryNeverDeletesCommittedSessions()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        MutableAdapter adapter = new(workspace.Root);
        IndexingCoordinator coordinator = Coordinator(database, adapter, workspace.Root);

        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);
        adapter.DiscoveryIsPartial = true;
        adapter.HideSessions = true;
        IndexingReport partial = await coordinator.ReconcileAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.True(partial.IsPartial);
        SessionSearchResult retained = await FindSingleAsync(database, "durable token");
        Assert.Equal(adapter.Identity, retained.Session.Identity);
    }

    // feat-001/AC-13 feat-001/AC-17
    [Fact]
    public async Task Feat001Ac17PartialReadPreservesPriorContentAndForcesRetry()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        MutableAdapter adapter = new(workspace.Root);
        IndexingCoordinator coordinator = Coordinator(database, adapter, workspace.Root);
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);

        adapter.Length = 200;
        adapter.LastWriteUtc = adapter.LastWriteUtc.AddMinutes(1);
        adapter.PartialRead = true;
        adapter.CurrentText = "uncommitted token";
        IndexingReport partial = await coordinator.ReconcileAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.True(partial.IsPartial);
        Assert.NotNull(await FindSingleAsync(database, "durable token"));
        Assert.Empty((await SearchAsync(database, "uncommitted token")).Results);

        adapter.PartialRead = false;
        adapter.CurrentText = "replacement token";
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);

        Assert.NotNull(await FindSingleAsync(database, "replacement token"));
        Assert.Empty((await SearchAsync(database, "durable token")).Results);
        Assert.Equal([0, 100, 0], adapter.StartOffsets);
    }

    // feat-001/AC-13
    [Fact]
    public async Task Feat001Ac13AppendReadsOnlyFromCommittedOffset()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        MutableAdapter adapter = new(workspace.Root);
        IndexingCoordinator coordinator = Coordinator(database, adapter, workspace.Root);
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);

        adapter.Length = 200;
        adapter.LastWriteUtc = adapter.LastWriteUtc.AddMinutes(1);
        adapter.CurrentText = "appended token";
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);

        Assert.NotNull(await FindSingleAsync(database, "durable token"));
        Assert.NotNull(await FindSingleAsync(database, "appended token"));
        Assert.Equal([0, 100], adapter.StartOffsets);
    }

    // feat-001/AC-13
    [Fact]
    public async Task Feat001Ac13ChangedFileIdentityForcesReplacement()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        MutableAdapter adapter = new(workspace.Root) { FileIdentity = "identity-a" };
        IndexingCoordinator coordinator = Coordinator(database, adapter, workspace.Root);
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);

        adapter.FileIdentity = "identity-b";
        adapter.CurrentText = "identity replacement";
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);

        Assert.NotNull(await FindSingleAsync(database, "identity replacement"));
        Assert.Empty((await SearchAsync(database, "durable token")).Results);
        Assert.Equal([0, 0], adapter.StartOffsets);
    }

    // feat-001/AC-7 feat-001/AC-12 feat-001/AC-17
    [Fact]
    public async Task Feat001Ac7FavoriteSurvivesCompleteSourceRemovalAsBlockedMetadata()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        MutableAdapter adapter = new(workspace.Root);
        IndexingCoordinator coordinator = Coordinator(database, adapter, workspace.Root);
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);
        await new FavoritesRepository(database).SetSessionFavoriteAsync(
            adapter.Identity,
            isFavorite: true,
            TestContext.Current.CancellationToken);

        adapter.HideSessions = true;
        adapter.DiscoveryIsPartial = false;
        IndexingReport removal = await coordinator.ReconcileAsync(
            null,
            TestContext.Current.CancellationToken);
        SessionSearchPage starred = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(
                QueryParser.Parse(string.Empty).Query!,
                SearchScope.Starred),
            TestContext.Current.CancellationToken);

        Assert.False(removal.IsPartial);
        SessionDocument retained = Assert.Single(starred.Results).Session;
        Assert.Equal(adapter.Identity, retained.Identity);
        Assert.False(retained.SourcePresent);
        AvailabilityDecision availability = AvailabilityEvaluator.Evaluate(
            new AvailabilityInputs(SourcePresent: retained.SourcePresent));
        Assert.Equal(AvailabilityStatus.SourceRemoved, availability.Status);
        Assert.False(availability.CanOpen);
    }

    // feat-001/AC-12
    [Fact]
    public async Task Feat001Ac12TrustedSessionTransitionsThroughUnsupportedFormatAndBack()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        MutableAdapter adapter = new(workspace.Root);
        IndexingCoordinator coordinator = Coordinator(database, adapter, workspace.Root);
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);

        adapter.FormatSupported = false;
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);
        SessionDocument unsupported = Assert.Single(
            (await new SessionSearchService(database).SearchAsync(
                new SessionSearchRequest(QueryParser.Parse(string.Empty).Query!),
                TestContext.Current.CancellationToken)).Results).Session;
        Assert.False(unsupported.FormatSupported);
        Assert.Equal(
            AvailabilityStatus.UnsupportedFormat,
            AvailabilityEvaluator.Evaluate(
                new AvailabilityInputs(FormatSupported: unsupported.FormatSupported)).Status);

        adapter.FormatSupported = true;
        await coordinator.ReconcileAsync(null, TestContext.Current.CancellationToken);
        SessionDocument supportedAgain = Assert.Single(
            (await new SessionSearchService(database).SearchAsync(
                new SessionSearchRequest(QueryParser.Parse(string.Empty).Query!),
                TestContext.Current.CancellationToken)).Results).Session;
        Assert.True(supportedAgain.FormatSupported);
    }

    private static IndexingCoordinator Coordinator(
        SessionDatabase database,
        ISessionProviderAdapter adapter,
        string root) =>
        new(database, [new ProviderRegistration(adapter, root)]);

    private static async ValueTask<SessionSearchResult> FindSingleAsync(
        SessionDatabase database,
        string query) =>
        Assert.Single((await SearchAsync(database, query)).Results);

    private static async ValueTask<SessionSearchPage> SearchAsync(
        SessionDatabase database,
        string query) =>
        await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse(query).Query!),
            TestContext.Current.CancellationToken);

    private sealed class MutableAdapter(string root) : ISessionProviderAdapter
    {
        public SessionIdentity Identity { get; } = new(
            SessionProvider.ClaudeCode,
            Guid.Parse("99999999-9999-9999-9999-999999999999"));

        public SessionProvider Provider => SessionProvider.ClaudeCode;

        public bool DiscoveryIsPartial { get; set; }

        public bool HideSessions { get; set; }

        public bool PartialRead { get; set; }

        public bool FormatSupported { get; set; } = true;

        public long Length { get; set; } = 100;

        public DateTimeOffset LastWriteUtc { get; set; } = new(
            2026,
            8,
            26,
            10,
            0,
            0,
            TimeSpan.Zero);

        public string? FileIdentity { get; set; }

        public string CurrentText { get; set; } = "durable token";

        public List<long> StartOffsets { get; } = [];

        public ValueTask<ProviderDiscoveryResult> DiscoverAsync(
            string rootPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HideSessions)
            {
                return ValueTask.FromResult(new ProviderDiscoveryResult(
                    [],
                    [],
                    DiscoveryIsPartial));
            }

            ProviderSource source = Source();
            ProviderSessionSeed session = new(
                Identity,
                @"C:\repos\fixture",
                "main",
                "fixture-model",
                LastWriteUtc,
                LastWriteUtc,
                Archived: false,
                FormatSupported,
                [source]);
            return ValueTask.FromResult(new ProviderDiscoveryResult(
                [session],
                [],
                DiscoveryIsPartial));
        }

        public ValueTask<ProviderReadResult> ReadAsync(
            ProviderSource source,
            long startOffset,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartOffsets.Add(startOffset);
            ProviderRecord record = new(
                Identity,
                source.RelativePath,
                startOffset + 1,
                LastWriteUtc,
                ProviderRecordKind.AssistantText,
                CurrentText,
                null,
                IsChild: false);
            return ValueTask.FromResult(new ProviderReadResult(
                [record],
                [],
                Length,
                PartialRead));
        }

        private ProviderSource Source() =>
            new(
                Identity,
                Path.Combine(root, "mutable.jsonl"),
                "mutable.jsonl",
                ProviderSourceKind.TopLevel,
                null,
                Archived: false,
                Length,
                LastWriteUtc,
                ParserVersion: 1,
                FileIdentity);
    }
}
