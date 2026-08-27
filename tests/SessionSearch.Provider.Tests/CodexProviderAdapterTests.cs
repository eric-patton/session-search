using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Text;
using SessionSearch.Infrastructure.Codex;
using SessionSearch.Infrastructure.Storage;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.Provider.Tests;

public sealed class CodexProviderAdapterTests
{
    private static readonly Guid RootSessionId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly Guid ChildSessionId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static readonly Guid ArchivedSessionId =
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    // feat-001/AC-1 feat-001/AC-5 feat-001/AC-6
    [Fact]
    public async Task DiscoverAsyncFindsLiveAndArchivedSessionsAndRollsChildToFilenameMatchedOwner()
    {
        string fixtureRoot = FindCodexFixtureRoot();
        CodexProviderAdapter adapter = new();

        ProviderDiscoveryResult result = await adapter.DiscoverAsync(
            fixtureRoot,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsPartial);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Sessions.Count);

        ProviderSessionSeed live = result.Sessions.Single(
            session => session.Identity.SessionId == RootSessionId);
        Assert.Equal(SessionProvider.Codex, live.Identity.Provider);
        Assert.Equal(@"C:\repos\fixture", live.Directory);
        Assert.Equal("main", live.Branch);
        Assert.Null(live.Model);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-26T10:00:00.000Z", CultureInfo.InvariantCulture),
            live.CreatedUtc);
        Assert.Equal(
            DateTimeOffset.Parse("2026-08-26T10:05:03.000Z", CultureInfo.InvariantCulture),
            live.LastActivityUtc);
        Assert.False(live.Archived);
        Assert.True(live.FormatSupported);
        Assert.Equal(2, live.Sources.Count);

        ProviderSource topLevel = live.Sources.Single(
            source => source.Kind == ProviderSourceKind.TopLevel);
        Assert.Null(topLevel.ChildSessionId);
        Assert.Equal(RootSessionId, topLevel.Owner.SessionId);

        ProviderSource child = live.Sources.Single(
            source => source.Kind == ProviderSourceKind.Child);
        Assert.Equal(ChildSessionId, child.ChildSessionId);
        Assert.Equal(RootSessionId, child.Owner.SessionId);
        Assert.False(child.Archived);

        Assert.DoesNotContain(
            result.Sessions,
            session => session.Identity.SessionId == ChildSessionId);

        ProviderSessionSeed archived = result.Sessions.Single(
            session => session.Identity.SessionId == ArchivedSessionId);
        Assert.True(archived.Archived);
        Assert.Equal(@"C:\repos\archived-fixture", archived.Directory);
        Assert.Single(archived.Sources);
        Assert.True(archived.Sources[0].Archived);
    }

    // feat-001/AC-4 feat-001/AC-5 feat-001/AC-6
    [Fact]
    public async Task ReadAsyncUsesLatestIndexNameAndExcludesToolsAndDuplicateRepresentations()
    {
        string fixtureRoot = FindCodexFixtureRoot();
        CodexProviderAdapter adapter = new();
        ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
            fixtureRoot,
            TestContext.Current.CancellationToken);
        ProviderSessionSeed live = discovery.Sessions.Single(
            session => session.Identity.SessionId == RootSessionId);
        ProviderSource source = live.Sources.Single(
            item => item.Kind == ProviderSourceKind.TopLevel);

        ProviderReadResult read = await adapter.ReadAsync(
            source,
            0,
            TestContext.Current.CancellationToken);

        Assert.False(read.IsPartial);
        Assert.Empty(read.Diagnostics);
        Assert.Equal(source.Length, read.LastCompleteOffset);

        ProviderRecord title = Assert.Single(
            read.Records,
            record => record.Kind == ProviderRecordKind.AiTitle);
        Assert.Equal("Exact tile review", title.Text);

        Assert.Equal(
            ["Review the tile cache error", "Keep the command copy exact"],
            read.Records
                .Where(record => record.Kind == ProviderRecordKind.UserText)
                .Select(record => record.Text)
                .ToArray());
        Assert.All(
            read.Records.Where(record => record.Kind == ProviderRecordKind.UserText),
            record => Assert.Equal(UserTextKind.Human, record.UserTextKind));

        Assert.Equal(
            ["The cache error comes from a stale path."],
            read.Records
                .Where(record => record.Kind == ProviderRecordKind.AssistantText)
                .Select(record => record.Text)
                .ToArray());
        Assert.DoesNotContain(
            read.Records,
            record => record.Kind == ProviderRecordKind.ToolText);
    }

    // feat-001/AC-5
    [Fact]
    public async Task ReadAsyncLabelsChildTextWithTheRecursiveRootOwner()
    {
        string fixtureRoot = FindCodexFixtureRoot();
        CodexProviderAdapter adapter = new();
        ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
            fixtureRoot,
            TestContext.Current.CancellationToken);
        ProviderSource child = discovery.Sessions
            .Single(session => session.Identity.SessionId == RootSessionId)
            .Sources
            .Single(source => source.Kind == ProviderSourceKind.Child);

        ProviderReadResult read = await adapter.ReadAsync(
            child,
            0,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, read.Records.Count);
        Assert.All(read.Records, record => Assert.True(record.IsChild));
        Assert.All(read.Records, record => Assert.Equal(RootSessionId, record.Owner.SessionId));
        Assert.Contains(
            read.Records,
            record => record.Text.Contains("violet comet", StringComparison.Ordinal));
    }

    // feat-001/AC-1 feat-001/AC-5 feat-001/AC-17
    [Fact]
    public async Task DiscoverAsyncResolvesRecursiveOwnershipAndRejectsCyclesWithoutGuessing()
    {
        using TemporaryCodexRoot fixture = new();
        Guid rootId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid childId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid grandchildId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        Guid cycleOneId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        Guid cycleTwoId = Guid.Parse("55555555-5555-5555-5555-555555555555");

        fixture.WriteRollout(rootId, SessionMeta(rootId, null, "gpt-fixture"));
        fixture.WriteRollout(childId, SessionMeta(childId, rootId, null));
        fixture.WriteRollout(grandchildId, SessionMeta(grandchildId, childId, null));
        fixture.WriteRollout(cycleOneId, SessionMeta(cycleOneId, cycleTwoId, null));
        fixture.WriteRollout(cycleTwoId, SessionMeta(cycleTwoId, cycleOneId, null));

        CodexProviderAdapter adapter = new();
        ProviderDiscoveryResult result = await adapter.DiscoverAsync(
            fixture.Root,
            TestContext.Current.CancellationToken);

        ProviderSessionSeed root = Assert.Single(result.Sessions);
        Assert.Equal(rootId, root.Identity.SessionId);
        Assert.Equal("gpt-fixture", root.Model);
        Assert.Equal(3, root.Sources.Count);
        Assert.Equal(
            [childId, grandchildId],
            root.Sources
                .Where(source => source.Kind == ProviderSourceKind.Child)
                .Select(source => source.ChildSessionId!.Value)
                .Order()
                .ToArray());
        Assert.True(result.IsPartial);
        Assert.Equal(
            2,
            result.Diagnostics.Count(diagnostic => diagnostic.Code == "codex.child.cycle"));
        Assert.All(result.Diagnostics, diagnostic => Assert.DoesNotContain(fixture.Root, diagnostic.Message));
    }

    // feat-001/AC-14
    [Fact]
    public async Task PublicOperationsDoNotModifyCodexFixtureSources()
    {
        string fixtureRoot = FindCodexFixtureRoot();
        Dictionary<string, FileStamp> before = CaptureFileStamps(fixtureRoot);
        CodexProviderAdapter adapter = new();

        ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
            fixtureRoot,
            TestContext.Current.CancellationToken);
        foreach (ProviderSource source in discovery.Sessions.SelectMany(session => session.Sources))
        {
            await adapter.ReadAsync(source, 0, TestContext.Current.CancellationToken);
        }

        Dictionary<string, FileStamp> after = CaptureFileStamps(fixtureRoot);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach ((string path, FileStamp expected) in before)
        {
            Assert.Equal(expected, after[path]);
        }
    }

    // feat-001/AC-17
    [Fact]
    public async Task PublicOperationsPropagateCancellation()
    {
        string fixtureRoot = FindCodexFixtureRoot();
        CodexProviderAdapter adapter = new();
        using CancellationTokenSource canceled = new();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await adapter.DiscoverAsync(fixtureRoot, canceled.Token));

        ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
            fixtureRoot,
            TestContext.Current.CancellationToken);
        ProviderSource source = discovery.Sessions[0].Sources[0];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await adapter.ReadAsync(source, 0, canceled.Token));
    }

    // feat-001/AC-17
    [Fact]
    public async Task DiscoverAsyncReportsSanitizedDiagnosticsForMalformedRollout()
    {
        using TemporaryCodexRoot fixture = new();
        Guid sessionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        fixture.WriteRollout(sessionId, "private transcript body that must not leak");
        CodexProviderAdapter adapter = new();

        ProviderDiscoveryResult result = await adapter.DiscoverAsync(
            fixture.Root,
            TestContext.Current.CancellationToken);

        ProviderDiagnostic diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "codex.discovery.invalid-json");
        Assert.Empty(result.Sessions);
        Assert.True(result.IsPartial);
        Assert.Equal("codex.discovery.invalid-json", diagnostic.Code);
        Assert.False(Path.IsPathRooted(diagnostic.SourceAlias));
        Assert.DoesNotContain("private transcript body", diagnostic.Message);
        Assert.DoesNotContain(fixture.Root, diagnostic.Message);
    }

    // feat-001/AC-2 feat-001/AC-13 feat-001/AC-17
    [Fact]
    public async Task Feat001Ac13DiscoveryStopsAtBoundedMetadataPrefix()
    {
        using TemporaryCodexRoot fixture = new();
        Guid sessionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        List<string> lines = [SessionMeta(sessionId, null, "bounded-model")];
        for (int index = 0; index < 1_000; index++)
        {
            lines.Add(JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["timestamp"] = "2026-08-26T10:00:01.000Z",
                ["type"] = "event_msg",
                ["payload"] = new Dictionary<string, object?>
                {
                    ["type"] = "token_count",
                    ["sequence"] = index,
                },
            }));
        }

        lines.Add("private malformed tail that bounded metadata discovery must not read");
        fixture.WriteRollout(sessionId, lines.ToArray());
        CodexProviderAdapter adapter = new();

        ProviderDiscoveryResult result = await adapter.DiscoverAsync(
            fixture.Root,
            TestContext.Current.CancellationToken);

        ProviderSessionSeed session = Assert.Single(result.Sessions);
        Assert.False(result.IsPartial);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("bounded-model", session.Model);
    }

    // feat-001/AC-18
    [Theory]
    [InlineData(@"\\attacker.example\share\codex")]
    [InlineData(@"\\?\C:\Users\fixture\.codex")]
    [InlineData(@"relative\.codex")]
    public async Task Feat001Ac18RejectsUnsafeRootsBeforeFilesystemProbing(string root)
    {
        var probe = new CountingPathProbe();
        var adapter = new CodexProviderAdapter(new LocalPathPolicy(probe));

        ProviderDiscoveryResult result = await adapter.DiscoverAsync(
            root,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsPartial);
        Assert.Empty(result.Sessions);
        Assert.Equal(0, probe.Calls);
    }

    // feat-001/AC-1 feat-001/AC-14
    [Fact]
    public async Task Feat001Ac14ReadsStateEnrichmentWithoutChangingDatabaseOrSidecars()
    {
        using TemporaryCodexRoot fixture = new();
        Guid sessionId = Guid.Parse("99999999-9999-9999-9999-999999999999");
        fixture.WriteRollout(sessionId, SessionMeta(sessionId, null, null));
        string databasePath = Path.Combine(fixture.Root, "state_5.sqlite");
        SqliteBootstrap.Initialize();
        await using SqliteConnection writer = new(
            $"Data Source={databasePath};Pooling=False");
        await writer.OpenAsync(TestContext.Current.CancellationToken);
        await using (SqliteCommand command = writer.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                CREATE TABLE threads(
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    name TEXT,
                    git_branch TEXT,
                    model TEXT
                );
                INSERT INTO threads(id, title, name, git_branch, model)
                VALUES($id, 'Generated state title', 'Pinned state name', 'state-branch', 'state-model');
                """;
            command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        Dictionary<string, FileStamp> before = CaptureFileStamps(fixture.Root);
        var adapter = new CodexProviderAdapter();
        ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
            fixture.Root,
            TestContext.Current.CancellationToken);
        ProviderSessionSeed session = Assert.Single(discovery.Sessions);
        Assert.Equal("state-branch", session.Branch);
        Assert.Equal("state-model", session.Model);
        ProviderReadResult read = await adapter.ReadAsync(
            Assert.Single(session.Sources),
            0,
            TestContext.Current.CancellationToken);
        Assert.Contains(
            read.Records,
            record => record.Kind == ProviderRecordKind.AiTitle &&
                record.Text == "Pinned state name");

        Dictionary<string, FileStamp> after = CaptureFileStamps(fixture.Root);
        string[] databaseSuffixes = [string.Empty, "-wal", "-shm"];
        foreach (string suffix in databaseSuffixes)
        {
            string path = databasePath + suffix;
            Assert.True(before.ContainsKey(path));
            Assert.Equal(before[path], after[path]);
        }
    }

    private static string SessionMeta(Guid id, Guid? parentId, string? model)
    {
        Dictionary<string, object?> payload = new()
        {
            ["id"] = id,
            ["timestamp"] = "2026-08-26T10:00:00.000Z",
            ["cwd"] = @"C:\repos\fixture",
            ["source"] = parentId is null
                ? "cli"
                : new Dictionary<string, object?>
                {
                    ["subagent"] = new Dictionary<string, object?>
                    {
                        ["parent_thread_id"] = parentId,
                    },
                },
            ["parent_thread_id"] = parentId,
            ["agent_path"] = parentId is null ? null : "fixture-agent",
            ["model"] = model,
        };

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["timestamp"] = "2026-08-26T10:00:00.000Z",
            ["type"] = "session_meta",
            ["payload"] = payload,
        });
    }

    private static Dictionary<string, FileStamp> CaptureFileStamps(string root) =>
        Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => path,
                path => new FileStamp(
                    new FileInfo(path).Length,
                    File.GetLastWriteTimeUtc(path),
                    HashSharedFile(path)),
                StringComparer.OrdinalIgnoreCase);

    private static string HashSharedFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string FindCodexFixtureRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "Codex");
            if (File.Exists(Path.Combine(candidate, "expected.json")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate tests/Fixtures/Codex.");
    }

    private sealed class TemporaryCodexRoot : IDisposable
    {
        public TemporaryCodexRoot()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "SessionSearch-CodexTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void WriteRollout(Guid id, params string[] lines)
        {
            string sessions = Path.Combine(Root, "sessions", "2026", "08", "26");
            Directory.CreateDirectory(sessions);
            string path = Path.Combine(
                sessions,
                $"rollout-2026-08-26T10-00-00-{id:D}.jsonl");
            File.WriteAllLines(path, lines);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed record FileStamp(long Length, DateTime LastWriteUtc, string Sha256);

    private sealed class CountingPathProbe : IWindowsPathProbe
    {
        public int Calls { get; private set; }

        public DriveType GetDriveType(string driveRoot)
        {
            Calls++;
            return DriveType.Fixed;
        }

        public bool DirectoryExists(string path)
        {
            Calls++;
            return false;
        }

        public bool FileExists(string path)
        {
            Calls++;
            return false;
        }

        public bool HasReparsePoint(string path)
        {
            Calls++;
            return false;
        }

        public string GetFinalPath(string path, bool directory)
        {
            Calls++;
            return path;
        }
    }
}
