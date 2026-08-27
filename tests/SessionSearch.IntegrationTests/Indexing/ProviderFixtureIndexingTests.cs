using SessionSearch.Core.Models;
using SessionSearch.Core.Search;
using SessionSearch.Core.Sessions;
using SessionSearch.Infrastructure.Claude;
using SessionSearch.Infrastructure.Codex;
using SessionSearch.Infrastructure.Indexing;
using SessionSearch.Infrastructure.Search;
using SessionSearch.Infrastructure.Storage;
using SessionSearch.IntegrationTests.Storage;

namespace SessionSearch.IntegrationTests.Indexing;

public sealed class ProviderFixtureIndexingTests
{
    // feat-001/AC-1 feat-001/AC-5 feat-001/AC-13 feat-001/AC-17
    [Fact]
    public async Task Feat001Ac1IndexesBothSanitizedProviderFixturesEndToEnd()
    {
        using TestWorkspace workspace = new();
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        string fixtures = FindFixturesRoot();
        IndexingCoordinator coordinator = new(
            database,
            [
                new ProviderRegistration(
                    new ClaudeSessionProviderAdapter(),
                    Path.Combine(fixtures, "Claude")),
                new ProviderRegistration(
                    new CodexProviderAdapter(),
                    Path.Combine(fixtures, "Codex")),
            ]);

        IndexingReport report = await coordinator.ReconcileAsync(
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(4, report.DiscoveredSessions);
        Assert.Equal(4, report.CompletedSessions);
        Assert.Equal(6, report.ChangedSources);
        Assert.False(report.IsPartial);

        SessionSearchService search = new(database);
        SessionSearchPage claude = await search.SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("copper nebula").Query!),
            TestContext.Current.CancellationToken);
        SessionSearchPage codex = await search.SearchAsync(
            new SessionSearchRequest(QueryParser.Parse("violet comet").Query!),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            SessionProvider.ClaudeCode,
            Assert.Single(claude.Results).Session.Identity.Provider);
        Assert.Equal(
            SessionProvider.Codex,
            Assert.Single(codex.Results).Session.Identity.Provider);
    }

    private static string FindFixturesRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "Fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The sanitized fixture root was not found.");
    }
}
