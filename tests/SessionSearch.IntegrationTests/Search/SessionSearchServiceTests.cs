using System.Text;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Search;
using SessionSearch.Core.Sessions;
using SessionSearch.Infrastructure.Search;
using SessionSearch.Infrastructure.Storage;
using SessionSearch.IntegrationTests.Storage;

namespace SessionSearch.IntegrationTests.Search;

public sealed class SessionSearchServiceTests
{
    // feat-001/AC-3 feat-001/AC-4 feat-001/AC-5
    [Fact]
    public async Task Feat001Ac4MergesMetadataAndTranscriptWithStableClasses()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        DateTimeOffset now = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

        await SeedAsync(
            repository,
            Document("00000000-0000-0000-0000-000000000001", "Tile error", now),
            ["metadata only"],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document("00000000-0000-0000-0000-000000000002", "Tile repair", now.AddMinutes(1)),
            ["fatal error after resume"],
            TestContext.Current.CancellationToken);

        ParsedQuery query = QueryParser.Parse("tile error").Query!;
        SessionSearchPage page = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(query),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(MatchClass.ExactTitle, page.Results[0].MatchClass);
        Assert.Equal(MatchClass.Transcript, page.Results[1].MatchClass);
        Assert.Contains("fatal error", page.Results[1].Snippet, StringComparison.Ordinal);
    }

    // feat-001/AC-3 feat-001/AC-4
    [Fact]
    public async Task Feat001Ac3MetadataOnlyModeAvoidsTranscriptEvaluation()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        DateTimeOffset now = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        SessionDocument metadata = Document(
            "00000000-0000-0000-0000-000000000201",
            "Needle metadata",
            now);
        SessionDocument transcript = Document(
            "00000000-0000-0000-0000-000000000202",
            "Unrelated",
            now.AddMinutes(1));
        await SeedAsync(
            repository,
            metadata,
            ["unrelated text"],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            transcript,
            ["needle transcript"],
            TestContext.Current.CancellationToken);

        SessionSearchService service = new(database);
        ParsedQuery query = QueryParser.Parse("needle").Query!;
        SessionSearchPage metadataOnly = await service.SearchAsync(
            new SessionSearchRequest(
                query,
                ContentMode: SearchContentMode.MetadataOnly),
            TestContext.Current.CancellationToken);
        SessionSearchPage complete = await service.SearchAsync(
            new SessionSearchRequest(query),
            TestContext.Current.CancellationToken);

        Assert.Equal(metadata.Identity, Assert.Single(metadataOnly.Results).Session.Identity);
        Assert.Equal(2, complete.TotalCount);
        Assert.Contains(
            complete.Results,
            result => result.Session.Identity == transcript.Identity
                && result.MatchClass == MatchClass.Transcript);
    }

    // feat-001/AC-4
    [Fact]
    public async Task Feat001Ac4AllowsRequiredAtomsInDifferentSegments()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        SessionDocument document = Document(
            "00000000-0000-0000-0000-000000000003",
            "Child investigation",
            new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero));
        await SeedAsync(
            repository,
            document,
            ["copper token", "nebula token"],
            TestContext.Current.CancellationToken,
            child: true);

        SessionSearchPage page = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("copper nebula").Query!),
            TestContext.Current.CancellationToken);

        SessionSearchResult result = Assert.Single(page.Results);
        Assert.Equal(document.Identity, result.Session.Identity);
        Assert.Equal(MatchClass.Transcript, result.MatchClass);
        Assert.True(result.SnippetFromChild);
    }

    // feat-001/AC-3 feat-001/AC-7
    [Fact]
    public async Task Feat001Ac3BrowseAndStarredScopesArePagedWithoutFts()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        FavoritesRepository favorites = new(database);
        DateTimeOffset old = new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);
        SessionDocument first = Document(
            "00000000-0000-0000-0000-000000000004",
            "Older",
            old);
        SessionDocument second = Document(
            "00000000-0000-0000-0000-000000000005",
            "Newer",
            old.AddDays(1));
        await SeedAsync(repository, first, [], TestContext.Current.CancellationToken);
        await SeedAsync(repository, second, [], TestContext.Current.CancellationToken);
        await favorites.SetSessionFavoriteAsync(
            first.Identity,
            isFavorite: true,
            TestContext.Current.CancellationToken);

        SessionSearchService service = new(database, partialState: static () => true);
        ParsedQuery browse = QueryParser.Parse(string.Empty).Query!;
        SessionSearchPage all = await service.SearchAsync(
            new SessionSearchRequest(browse, PageSize: 1),
            TestContext.Current.CancellationToken);
        SessionSearchPage starred = await service.SearchAsync(
            new SessionSearchRequest(browse, SearchScope.Starred),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, all.TotalCount);
        Assert.Equal(second.Identity, Assert.Single(all.Results).Session.Identity);
        Assert.True(all.IsPartial);
        Assert.Equal(first.Identity, Assert.Single(starred.Results).Session.Identity);
    }

    // feat-001/AC-4 feat-001/AC-7
    [Fact]
    public async Task Feat001Ac4DirectoryScopeRestrictsResultsWithoutChangingQuery()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        DateTimeOffset updated = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000006",
                "Shared task one",
                updated,
                directory: @"C:\repos\one"),
            [],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000007",
                "Shared task two",
                updated,
                directory: @"C:\repos\two"),
            [],
            TestContext.Current.CancellationToken);
        ParsedQuery query = QueryParser.Parse("shared").Query!;
        SessionSearchRequest request = new(
            query,
            DirectoryFilter: @"c:\REPOS\one");

        SessionSearchPage page = await new SessionSearchService(database).SearchAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.Equal("shared", request.Query.OriginalText);
        Assert.Equal(@"C:\repos\one", Assert.Single(page.Results).Session.Directory);
    }

    // feat-001/AC-4 feat-001/AC-18
    [Fact]
    public async Task Feat001Ac4ReservedLookingTextStaysAParameterLiteral()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionSearchService service = new(database);

        SessionSearchPage page = await service.SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("OR NEAR(foo) * title:fake").Query!),
            TestContext.Current.CancellationToken);

        Assert.Empty(page.Results);
    }

    // feat-001/AC-4
    [Fact]
    public async Task Feat001Ac4PreservesEveryMatchClassInDatabaseOrder()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        DateTimeOffset updated = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

        await SeedAsync(
            repository,
            Document("00000000-0000-0000-0000-000000000101", "tile error", updated),
            [],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000102",
                "Directory exact",
                updated,
                directory: "tile error"),
            [],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000103",
                "tile error follow-up",
                updated),
            [],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000104",
                "Repair tile",
                updated,
                branch: "error"),
            [],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000105",
                "Description match",
                updated,
                description: "contains tile and error"),
            [],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000106",
                "Directory match",
                updated,
                directory: @"C:\tile\error"),
            [],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000107",
                "Other metadata",
                updated,
                branch: "tile",
                model: "error"),
            [],
            TestContext.Current.CancellationToken);
        await SeedAsync(
            repository,
            Document(
                "00000000-0000-0000-0000-000000000108",
                "Transcript match",
                updated),
            ["tile error"],
            TestContext.Current.CancellationToken);

        SessionSearchPage page = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("tile error").Query!),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            Enum.GetValues<MatchClass>(),
            page.Results.Select(result => result.MatchClass!.Value));
    }

    // feat-001/AC-4
    [Fact]
    public async Task Feat001Ac4BuildsSnippetAroundTheActualMatch()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        SessionDocument document = Document(
            "00000000-0000-0000-0000-000000000109",
            "Long transcript",
            new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero));
        string text = "BEGIN-CANARY " + new string('x', 320) + " needle " + new string('y', 320);
        await SeedAsync(
            repository,
            document,
            [text],
            TestContext.Current.CancellationToken);

        SessionSearchPage page = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("needle").Query!),
            TestContext.Current.CancellationToken);

        string snippet = Assert.Single(page.Results).Snippet!;
        Assert.Contains("needle", snippet, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN-CANARY", snippet, StringComparison.Ordinal);
        Assert.True(snippet.EnumerateRunes().Count() <= 240);
    }

    // feat-001/AC-4 feat-001/AC-17
    [Fact]
    public async Task Feat001Ac17RetainsMetadataAndMarksPartialWhenFtsFails()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        SessionDocument document = Document(
            "00000000-0000-0000-0000-000000000110",
            "Needle metadata",
            new DateTimeOffset(2026, 8, 26, 10, 0, 0, TimeSpan.Zero));
        await SeedAsync(
            repository,
            document,
            ["unrelated transcript"],
            TestContext.Current.CancellationToken);
        QueryAtom invalidFtsAtom = new(
            QueryAtomKind.Term,
            "needle",
            "NEEDLE",
            ["needle"],
            "\"");
        ParsedQuery query = new("needle", "NEEDLE", [invalidFtsAtom], "\"");

        SessionSearchPage page = await new SessionSearchService(database).SearchAsync(
            new SessionSearchRequest(query),
            TestContext.Current.CancellationToken);

        SessionSearchResult result = Assert.Single(page.Results);
        Assert.Equal(document.Identity, result.Session.Identity);
        Assert.Equal(MatchClass.TitlePrefix, result.MatchClass);
        Assert.True(page.IsPartial);
    }

    // feat-001/AC-3 feat-001/AC-4
    [Fact]
    public async Task Feat001Ac4PagesFiftyRowsAndHandlesMaximumPageWithoutOverflow()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        SessionRepository repository = new(database);
        DateTimeOffset updated = new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);
        for (int index = 1; index <= 55; index++)
        {
            await SeedAsync(
                repository,
                Document(
                    $"00000000-0000-0000-0000-{index:D12}",
                    $"Session {index}",
                    updated.AddMinutes(index)),
                [],
                TestContext.Current.CancellationToken);
        }

        SessionSearchService service = new(database);
        ParsedQuery browse = QueryParser.Parse(string.Empty).Query!;
        SessionSearchPage first = await service.SearchAsync(
            new SessionSearchRequest(browse, PageSize: 100),
            TestContext.Current.CancellationToken);
        SessionSearchPage second = await service.SearchAsync(
            new SessionSearchRequest(browse, Page: 1),
            TestContext.Current.CancellationToken);
        SessionSearchPage overflow = await service.SearchAsync(
            new SessionSearchRequest(browse, Page: int.MaxValue),
            TestContext.Current.CancellationToken);

        Assert.Equal(55, first.TotalCount);
        Assert.Equal(50, first.Results.Count);
        Assert.Equal(5, second.Results.Count);
        Assert.Equal(55, overflow.TotalCount);
        Assert.Empty(overflow.Results);
    }

    private static SessionDocument Document(
        string id,
        string title,
        DateTimeOffset updated,
        string description = "Fixture description",
        string directory = @"C:\repos\fixture",
        string? branch = "main",
        string? model = "fixture-model") =>
        new(
            new SessionIdentity(SessionProvider.ClaudeCode, Guid.Parse(id)),
            $"source-{id}.jsonl",
            title,
            description,
            directory,
            branch,
            model,
            updated.AddMinutes(-5),
            updated,
            100,
            Archived: false,
            FormatSupported: true,
            SourcePresent: true,
            ParserVersion: 1);

    private static async ValueTask SeedAsync(
        SessionRepository repository,
        SessionDocument document,
        IReadOnlyList<string> segmentTexts,
        CancellationToken cancellationToken,
        bool child = false)
    {
        await repository.UpsertSessionAsync(document, cancellationToken);
        if (segmentTexts.Count == 0)
        {
            return;
        }

        ProviderSource source = new(
            document.Identity,
            Path.GetFullPath(document.SourcePath),
            document.SourcePath,
            child ? ProviderSourceKind.Child : ProviderSourceKind.TopLevel,
            child ? Guid.NewGuid() : null,
            Archived: false,
            Length: document.SourceBytes,
            LastWriteUtc: document.LastActivityUtc,
            ParserVersion: 1);
        SessionSegment[] segments = segmentTexts
            .Select((text, index) => new SessionSegment(
                index,
                ProviderRecordKind.UserText,
                document.LastActivityUtc,
                ProviderRecordKind.UserText,
                child,
                text))
            .ToArray();
        await repository.ReplaceSourceContentAsync(
            source,
            segments,
            completeOffset: source.Length,
            cancellationToken);
    }
}
