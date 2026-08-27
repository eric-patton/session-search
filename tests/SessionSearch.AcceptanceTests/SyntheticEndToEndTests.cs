using System.Security.Cryptography;
using System.Text.Json;
using SessionSearch.Core.Models;
using SessionSearch.Core.Search;
using SessionSearch.Core.Sessions;
using SessionSearch.Core.Text;
using SessionSearch.Infrastructure.Claude;
using SessionSearch.Infrastructure.Codex;
using SessionSearch.Infrastructure.Indexing;
using SessionSearch.Infrastructure.Search;
using SessionSearch.Infrastructure.Storage;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.AcceptanceTests;

public sealed class SyntheticEndToEndTests
{
    // feat-001/AC-1 feat-001/AC-5 feat-001/AC-7 feat-001/AC-9
    // feat-001/AC-11 feat-001/AC-13 feat-001/AC-14 feat-001/AC-19
    [Fact]
    public async Task Feat001SyntheticCorpusRemainsReadOnlyAcrossTheCompleteWorkflow()
    {
        string repositoryRoot = FindRepositoryRoot();
        string fixturesRoot = Path.Combine(repositoryRoot, "tests", "Fixtures");
        Dictionary<string, SourceFingerprint> before = FingerprintTree(fixturesRoot);
        VerifyIntegrityManifest(fixturesRoot, before);
        string workRoot = Path.Combine(
            Path.GetTempPath(),
            "SessionSearch.Acceptance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);

        try
        {
            string databasePath = Path.Combine(workRoot, "session-search.sqlite3");
            await using SessionDatabase database = await SessionDatabase.CreateAsync(
                databasePath,
                protectDirectory: false,
                TestContext.Current.CancellationToken);
            var coordinator = new IndexingCoordinator(
                database,
                [
                    new ProviderRegistration(
                        new ClaudeSessionProviderAdapter(),
                        Path.Combine(fixturesRoot, "Claude")),
                    new ProviderRegistration(
                        new CodexProviderAdapter(),
                        Path.Combine(fixturesRoot, "Codex")),
                ]);

            IndexingReport report = await coordinator.ReconcileAsync(
                progress: null,
                TestContext.Current.CancellationToken);
            Assert.False(report.IsPartial);
            Assert.True(report.CompletedSessions >= 4);

            var search = new SessionSearchService(database);
            SessionSearchResult claude = Assert.Single((await search.SearchAsync(
                new SessionSearchRequest(QueryParser.Parse("copper nebula").Query!),
                TestContext.Current.CancellationToken)).Results);
            SessionSearchResult codex = Assert.Single((await search.SearchAsync(
                new SessionSearchRequest(QueryParser.Parse("violet comet").Query!),
                TestContext.Current.CancellationToken)).Results);
            Assert.True(claude.SnippetFromChild);
            Assert.True(codex.SnippetFromChild);

            var favorites = new FavoritesRepository(database);
            await favorites.SetSessionFavoriteAsync(
                claude.Session.Identity,
                isFavorite: true,
                TestContext.Current.CancellationToken);
            await favorites.SetDirectoryFavoriteAsync(
                claude.Session.Directory,
                isFavorite: true,
                TestContext.Current.CancellationToken);
            Assert.True(await favorites.IsSessionFavoriteAsync(
                claude.Session.Identity,
                TestContext.Current.CancellationToken));
            Assert.True(await favorites.IsDirectoryFavoriteAsync(
                claude.Session.Directory.ToUpperInvariant(),
                TestContext.Current.CancellationToken));

            var revalidator = new AlwaysValidPlanRevalidator();
            var planner = new ResumePlanner(revalidator);
            var launcher = new RecordingProcessLauncher(revalidator);
            ResumePlan plan = planner.Create(new ResumeRequest(
                codex.Session.Identity,
                codex.Session.Directory,
                ProviderExecutable(codex.Session.Identity.Provider),
                TerminalExecutable()));
            ProcessLaunchResult launch = await launcher.LaunchAsync(
                plan,
                TestContext.Current.CancellationToken);
            Assert.True(launch.Started);
            Assert.Single(launcher.Starts);
            string command = PowerShellCommandFormatter.Format(plan);
            Assert.Contains(
                codex.Session.Identity.SessionId.ToString("D"),
                command,
                StringComparison.Ordinal);
            Assert.Contains("Set-Location -LiteralPath", command, StringComparison.Ordinal);

            await coordinator.ReconcileAsync(
                progress: null,
                TestContext.Current.CancellationToken);
            SessionDatabaseValidation validation = await database.ValidateAsync(
                includeFtsIntegrityCheck: true,
                TestContext.Current.CancellationToken);
            Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Errors));
        }
        finally
        {
            SqliteConnectionClearPools();
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }

        Dictionary<string, SourceFingerprint> after = FingerprintTree(fixturesRoot);
        Assert.Equal(before.OrderBy(pair => pair.Key), after.OrderBy(pair => pair.Key));
    }

    // feat-001/AC-19
    [Fact]
    public async Task Feat001Ac19MigrationFailureLeavesNoPartialSchema()
    {
        string workRoot = Path.Combine(
            Path.GetTempPath(),
            "SessionSearch.Acceptance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);
        string databasePath = Path.Combine(workRoot, "migration.sqlite3");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await SessionDatabase.CreateAsync(
                    databasePath,
                    protectDirectory: false,
                    TestContext.Current.CancellationToken,
                    static (_, _, _) => throw new InvalidOperationException(
                        "Injected migration failure.")));

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name='sessions';";
            Assert.Equal(
                0L,
                Convert.ToInt64(
                    await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnectionClearPools();
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    private static ResolvedExecutable ProviderExecutable(SessionProvider provider)
    {
        TrustedExecutableProfile profile = provider == SessionProvider.ClaudeCode
            ? new TrustedExecutableProfile(
                TrustedExecutableKind.ClaudeCode,
                "claude.exe",
                ["Anthropic, PBC"])
            : new TrustedExecutableProfile(
                TrustedExecutableKind.Codex,
                "codex.exe",
                ["OpenAI OpCo, LLC"]);
        string path = provider == SessionProvider.ClaudeCode
            ? @"C:\Synthetic\claude.exe"
            : @"C:\Synthetic\codex.exe";
        return new ResolvedExecutable(
            profile,
            path,
            "synthetic-provider-id",
            profile.ExpectedPublishers[0],
            false);
    }

    private static ResolvedExecutable TerminalExecutable()
    {
        var profile = new TrustedExecutableProfile(
            TrustedExecutableKind.WindowsTerminal,
            "wt.exe",
            ["Microsoft Corporation"]);
        return new ResolvedExecutable(
            profile,
            @"C:\Synthetic\wt.exe",
            "synthetic-terminal-id",
            "Microsoft Corporation",
            false);
    }

    private static Dictionary<string, SourceFingerprint> FingerprintTree(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace('\\', '/'),
                path =>
                {
                    FileInfo file = new(path);
                    using FileStream stream = new(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    return new SourceFingerprint(
                        file.Length,
                        file.LastWriteTimeUtc.Ticks,
                        Convert.ToHexString(SHA256.HashData(stream)));
                },
                StringComparer.Ordinal);

    private static void VerifyIntegrityManifest(
        string fixturesRoot,
        IReadOnlyDictionary<string, SourceFingerprint> actual)
    {
        using JsonDocument manifest = JsonDocument.Parse(
            File.ReadAllBytes(Path.Combine(fixturesRoot, "integrity-manifest.json")));
        foreach (JsonElement file in manifest.RootElement.GetProperty("files").EnumerateArray())
        {
            string path = file.GetProperty("path").GetString()!;
            SourceFingerprint fingerprint = actual[path];
            Assert.Equal(file.GetProperty("bytes").GetInt64(), fingerprint.Length);
            Assert.Equal(
                file.GetProperty("sha256").GetString(),
                fingerprint.Sha256,
                ignoreCase: true);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "SessionSearch.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("The repository root could not be located.");
    }

    private static void SqliteConnectionClearPools() =>
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    private sealed class AlwaysValidPlanRevalidator : IResumePlanRevalidator
    {
        public bool Revalidate(ResumePlan plan, out string reason)
        {
            reason = string.Empty;
            return true;
        }
    }

    private sealed record SourceFingerprint(long Length, long LastWriteTicks, string Sha256);
}
