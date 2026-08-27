using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Text;
using SessionSearch.Infrastructure.Claude;

namespace SessionSearch.Provider.Tests;

public sealed class ClaudeProviderAdapterTests
{
    private static readonly Guid MainSessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StubSessionId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact(DisplayName = "feat-001/AC-1, AC-5, AC-6: Claude fixture metadata and title precedence")]
    public async Task DiscoverAndReadAsyncUsesFixtureMetadataAndTitlePrecedence()
    {
        string fixtureRoot = ClaudeFixtureRoot.Find();
        IReadOnlyDictionary<string, ClaudeFileFingerprint> before = ClaudeFixtureRoot.Fingerprint(fixtureRoot);
        var adapter = new ClaudeSessionProviderAdapter();

        ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(fixtureRoot, TestContext.Current.CancellationToken);

        Assert.False(discovery.IsPartial);
        Assert.Equal(2, discovery.Sessions.Count);

        ProviderSessionSeed main = Assert.Single(discovery.Sessions, session => session.Identity.SessionId == MainSessionId);
        Assert.Equal(SessionProvider.ClaudeCode, main.Identity.Provider);
        Assert.Equal(@"C:\repos\fixture", main.Directory);
        Assert.Equal("main", main.Branch);
        Assert.Equal("claude-sonnet-4-5", main.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-08-26T10:00:00Z", CultureInfo.InvariantCulture), main.CreatedUtc);
        Assert.Equal(DateTimeOffset.Parse("2026-08-26T10:02:00Z", CultureInfo.InvariantCulture), main.LastActivityUtc);
        Assert.True(main.FormatSupported);
        Assert.False(main.Archived);
        Assert.Equal(3, main.Sources.Count);
        Assert.Single(main.Sources, source => source.Kind == ProviderSourceKind.TopLevel);
        Assert.Equal(2, main.Sources.Count(source => source.Kind == ProviderSourceKind.Child));
        Assert.DoesNotContain(main.Sources, source => source.RelativePath.EndsWith("journal.jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(main.Sources, source => source.RelativePath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase));

        ProviderSessionSeed stub = Assert.Single(discovery.Sessions, session => session.Identity.SessionId == StubSessionId);
        Assert.False(stub.FormatSupported);
        Assert.Single(stub.Sources);

        ProviderSource topLevel = Assert.Single(main.Sources, source => source.Kind == ProviderSourceKind.TopLevel);
        ProviderReadResult read = await adapter.ReadAsync(topLevel, 0, TestContext.Current.CancellationToken);

        Assert.False(read.IsPartial);
        Assert.Contains(read.Records, record => record.Kind == ProviderRecordKind.AssistantText && record.Text.Contains("tile cache key", StringComparison.Ordinal));
        Assert.DoesNotContain(read.Records, record => record.Text.Contains("system-reminder", StringComparison.Ordinal));

        var evidence = new SessionTextEvidence(
            read.Records
                .Where(record => record.Kind == ProviderRecordKind.ExplicitName)
                .Select(record => new TimestampedText(record.Text, record.Sequence))
                .ToArray(),
            read.Records
                .Where(record => record.Kind == ProviderRecordKind.AiTitle)
                .Select(record => new TimestampedText(record.Text, record.Sequence))
                .ToArray(),
            read.Records
                .Where(record => record.Kind == ProviderRecordKind.UserText && record.UserTextKind is not null)
                .Select(record => new UserTextEvidence(record.Text, record.Sequence, record.UserTextKind!.Value))
                .ToArray());

        ResolvedSessionText resolved = SessionTextResolver.Resolve(MainSessionId.ToString("D"), evidence);
        Assert.Equal("Pinned tile repair", resolved.Title);
        Assert.Equal("Make it fast and preserve <user-tag>literal markup</user-tag>", resolved.Description);

        IReadOnlyDictionary<string, ClaudeFileFingerprint> after = ClaudeFixtureRoot.Fingerprint(fixtureRoot);
        Assert.Equal(before, after);
    }

    [Fact(DisplayName = "feat-001/AC-5: Claude direct and workflow children roll up")]
    public async Task ReadAsyncRollsUpDirectAndWorkflowChildren()
    {
        var adapter = new ClaudeSessionProviderAdapter();
        ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
            ClaudeFixtureRoot.Find(),
            TestContext.Current.CancellationToken);
        ProviderSessionSeed main = Assert.Single(discovery.Sessions, session => session.Identity.SessionId == MainSessionId);
        ProviderSource[] children = main.Sources.Where(source => source.Kind == ProviderSourceKind.Child).ToArray();

        Assert.Equal(2, children.Length);
        Assert.Contains(children, source => source.RelativePath.EndsWith("agent-alpha.jsonl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(children, source => source.RelativePath.EndsWith("agent-beta.jsonl", StringComparison.OrdinalIgnoreCase));

        var texts = new List<string>();
        foreach (ProviderSource child in children)
        {
            ProviderReadResult result = await adapter.ReadAsync(child, 0, TestContext.Current.CancellationToken);
            Assert.False(result.IsPartial);
            Assert.All(result.Records, record =>
            {
                Assert.True(record.IsChild);
                Assert.Equal(main.Identity, record.Owner);
            });
            texts.AddRange(result.Records.Select(record => record.Text));
        }

        Assert.Contains(texts, text => text.Contains("copper nebula", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("workflow child", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(texts, text => text.Contains("not be indexed", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "feat-001/AC-1, AC-17: Claude identity mismatch diagnostics are sanitized")]
    public async Task DiscoverAsyncRejectsIdentityMismatchWithSanitizedDiagnostic()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            string projectDirectory = Directory.GetDirectories(Path.Combine(tempRoot, "projects"), "*", SearchOption.TopDirectoryOnly).Single();
            string mismatchedPath = Path.Combine(projectDirectory, "33333333-3333-3333-3333-333333333333.jsonl");
            const string record = """
                {"type":"user","sessionId":"44444444-4444-4444-4444-444444444444","cwd":"C:\\private\\secret","message":{"role":"user","content":"sensitive transcript text"}}
                """;
            await File.WriteAllTextAsync(mismatchedPath, record + Environment.NewLine, TestContext.Current.CancellationToken);

            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult result = await adapter.DiscoverAsync(tempRoot, TestContext.Current.CancellationToken);

            Assert.DoesNotContain(result.Sessions, session => session.Identity.SessionId == Guid.Parse("33333333-3333-3333-3333-333333333333"));
            ProviderDiagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "claude.identity-mismatch");
            Assert.Contains("33333333-3333-3333-3333-333333333333.jsonl", diagnostic.SourceAlias, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(tempRoot, diagnostic.SourceAlias, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("sensitive", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }

    [Fact(DisplayName = "feat-001/AC-6: Claude missing-directory candidates do not make complete discovery partial")]
    public async Task DiscoverAsyncSkipsMissingDirectoryWithoutMarkingOtherResultsPartial()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            string projectDirectory = Directory.GetDirectories(
                Path.Combine(tempRoot, "projects"),
                "*",
                SearchOption.TopDirectoryOnly).Single();
            Guid sessionId = Guid.Parse("99999999-9999-9999-9999-999999999999");
            string candidatePath = Path.Combine(
                projectDirectory,
                sessionId.ToString("D") + ".jsonl");
            const string record = """
                {"type":"user","sessionId":"99999999-9999-9999-9999-999999999999","message":{"role":"user","content":"No working directory"}}
                """;
            await File.WriteAllTextAsync(
                candidatePath,
                record + Environment.NewLine,
                TestContext.Current.CancellationToken);

            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult result = await adapter.DiscoverAsync(
                tempRoot,
                TestContext.Current.CancellationToken);

            Assert.False(result.IsPartial);
            Assert.DoesNotContain(
                result.Sessions,
                session => session.Identity.SessionId == sessionId);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "claude.missing-directory");
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }

    [Fact(DisplayName = "feat-001/AC-5: Claude child ownership validates session identity")]
    public async Task DiscoverAsyncSkipsChildWithConflictingSessionIdentity()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            string projectDirectory = Directory.GetDirectories(Path.Combine(tempRoot, "projects"), "*", SearchOption.TopDirectoryOnly).Single();
            string subagentsDirectory = Path.Combine(projectDirectory, MainSessionId.ToString("D"), "subagents");
            string childPath = Path.Combine(subagentsDirectory, "agent-invalid.jsonl");
            const string record = """
                {"type":"assistant","sessionId":"55555555-5555-5555-5555-555555555555","isSidechain":true,"message":{"role":"assistant","content":[{"type":"text","text":"private child text"}]}}
                """;
            await File.WriteAllTextAsync(childPath, record + Environment.NewLine, TestContext.Current.CancellationToken);

            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult result = await adapter.DiscoverAsync(tempRoot, TestContext.Current.CancellationToken);
            ProviderSessionSeed main = Assert.Single(result.Sessions, session => session.Identity.SessionId == MainSessionId);

            Assert.Equal(3, main.Sources.Count);
            ProviderDiagnostic diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "claude.child-identity-mismatch");
            Assert.EndsWith("agent-invalid.jsonl", diagnostic.SourceAlias, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private child text", diagnostic.Message, StringComparison.Ordinal);
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }

    [Fact(DisplayName = "feat-001/AC-17: Claude reader consumes complete JSONL records from an offset")]
    public async Task ReadAsyncReadsOnlyCompleteJsonlRecordsFromOffset()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(tempRoot, TestContext.Current.CancellationToken);
            ProviderSessionSeed main = Assert.Single(discovery.Sessions, session => session.Identity.SessionId == MainSessionId);
            ProviderSource source = Assert.Single(main.Sources, candidate => candidate.Kind == ProviderSourceKind.TopLevel);
            const string firstFragment = "{\"type\":\"ai-title\",\"sessionId\":\"11111111-1111-1111-1111-111111111111\",\"aiTitle\":";
            await File.AppendAllTextAsync(source.CanonicalPath, firstFragment, TestContext.Current.CancellationToken);
            long completeOffset = new FileInfo(source.CanonicalPath).Length - Encoding.UTF8.GetByteCount(firstFragment);

            ProviderReadResult partial = await adapter.ReadAsync(source, completeOffset, TestContext.Current.CancellationToken);

            Assert.True(partial.IsPartial);
            Assert.Empty(partial.Records);
            Assert.Equal(completeOffset, partial.LastCompleteOffset);

            const string finalFragment = "\"Fresh title\"}";
            await File.AppendAllTextAsync(source.CanonicalPath, finalFragment + Environment.NewLine, TestContext.Current.CancellationToken);
            ProviderReadResult resumed = await adapter.ReadAsync(source, completeOffset, TestContext.Current.CancellationToken);

            ProviderRecord title = Assert.Single(resumed.Records);
            Assert.Equal(ProviderRecordKind.AiTitle, title.Kind);
            Assert.Equal("Fresh title", title.Text);
            Assert.False(resumed.IsPartial);
            Assert.Equal(new FileInfo(source.CanonicalPath).Length, resumed.LastCompleteOffset);
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }

    [Fact(DisplayName = "feat-001/AC-17: Claude provider operations honor cancellation")]
    public async Task ProviderOperationsHonorPreCanceledTokens()
    {
        string fixtureRoot = ClaudeFixtureRoot.Find();
        var adapter = new ClaudeSessionProviderAdapter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await adapter.DiscoverAsync(fixtureRoot, cancellation.Token));

        ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(fixtureRoot, TestContext.Current.CancellationToken);
        ProviderSource source = discovery.Sessions.SelectMany(session => session.Sources).First();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await adapter.ReadAsync(source, 0, cancellation.Token));
    }

    [Theory(DisplayName = "feat-001/AC-18: Claude discovery rejects unsafe root classes before enumeration")]
    [InlineData("relative-claude-root")]
    [InlineData(@"\\fixture-server\private-share\claude")]
    [InlineData(@"\\?\C:\private\claude")]
    [InlineData(@"\\.\C:\private\claude")]
    public async Task DiscoverAsyncRejectsUnsafeRootClasses(string unsafeRoot)
    {
        var adapter = new ClaudeSessionProviderAdapter();

        ProviderDiscoveryResult result = await adapter.DiscoverAsync(
            unsafeRoot,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsPartial);
        Assert.Empty(result.Sessions);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "claude.root-rejected");
    }

    [Fact(DisplayName = "feat-001/AC-6: Claude typed content keeps human and tool text but excludes controls")]
    public async Task ReadAsyncClassifiesHumanToolAndControlContent()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            Guid sessionId = Guid.Parse("66666666-6666-6666-6666-666666666666");
            string projectDirectory = Directory.GetDirectories(Path.Combine(tempRoot, "projects"), "*", SearchOption.TopDirectoryOnly).Single();
            string sourcePath = Path.Combine(projectDirectory, sessionId.ToString("D") + ".jsonl");
            const string transcript = """
                {"type":"user","sessionId":"66666666-6666-6666-6666-666666666666","cwd":"C:\\repos\\fixture","userType":"external","message":{"role":"user","content":[{"type":"text","text":"Visible human prompt"},{"type":"tool_result","content":"Visible tool result"}]},"timestamp":"2026-08-26T12:00:00Z"}
                {"type":"assistant","sessionId":"66666666-6666-6666-6666-666666666666","cwd":"C:\\repos\\fixture","message":{"role":"assistant","content":[{"type":"text","text":"Visible assistant answer"},{"type":"thinking","thinking":"private reasoning"},{"type":"tool_use","name":"Read","input":{"file_path":"C:\\repos\\fixture\\README.md"}}]},"timestamp":"2026-08-26T12:01:00Z"}
                {"type":"user","sessionId":"66666666-6666-6666-6666-666666666666","cwd":"C:\\repos\\fixture","userType":"internal","message":{"role":"user","content":"internal control text"},"timestamp":"2026-08-26T12:02:00Z"}
                {"type":"user","sessionId":"66666666-6666-6666-6666-666666666666","cwd":"C:\\repos\\fixture","userType":"external","isSynthetic":true,"message":{"role":"user","content":"synthetic control text"},"timestamp":"2026-08-26T12:03:00Z"}
                {"type":"assistant","sessionId":"66666666-6666-6666-6666-666666666666","cwd":"C:\\repos\\fixture","isMeta":true,"message":{"role":"assistant","content":"assistant control text"},"timestamp":"2026-08-26T12:04:00Z"}
                """;
            await File.WriteAllTextAsync(
                sourcePath,
                transcript + Environment.NewLine,
                TestContext.Current.CancellationToken);

            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(tempRoot, TestContext.Current.CancellationToken);
            ProviderSessionSeed session = Assert.Single(discovery.Sessions, item => item.Identity.SessionId == sessionId);
            ProviderReadResult read = await adapter.ReadAsync(
                Assert.Single(session.Sources),
                0,
                TestContext.Current.CancellationToken);

            Assert.Contains(read.Records, record => record.Kind == ProviderRecordKind.UserText && record.Text == "Visible human prompt");
            Assert.Contains(read.Records, record => record.Kind == ProviderRecordKind.AssistantText && record.Text == "Visible assistant answer");
            Assert.Contains(read.Records, record => record.Kind == ProviderRecordKind.ToolText && record.Text.Contains("Visible tool result", StringComparison.Ordinal));
            Assert.Contains(read.Records, record => record.Kind == ProviderRecordKind.ToolText && record.Text.Contains("Read", StringComparison.Ordinal));
            Assert.Contains(read.Records, record => record.Kind == ProviderRecordKind.ToolText && record.Text.Contains("README.md", StringComparison.Ordinal));
            Assert.DoesNotContain(read.Records, record => record.Text.Contains("private reasoning", StringComparison.Ordinal));
            Assert.DoesNotContain(read.Records, record => record.Text.Contains("internal control", StringComparison.Ordinal));
            Assert.DoesNotContain(read.Records, record => record.Text.Contains("synthetic control", StringComparison.Ordinal));
            Assert.DoesNotContain(read.Records, record => record.Text.Contains("assistant control", StringComparison.Ordinal));
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }

    [Fact(DisplayName = "feat-001/AC-13, AC-17: Claude parser v2 ignores current provider envelopes without dropping messages")]
    public async Task ReadAsyncRecognizesCurrentProviderEnvelopesWithoutBecomingPartial()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            string projectDirectory = Directory.GetDirectories(
                Path.Combine(tempRoot, "projects"),
                "*",
                SearchOption.TopDirectoryOnly).Single();
            string sourcePath = Path.Combine(
                projectDirectory,
                MainSessionId.ToString("D") + ".jsonl");
            const string envelopes = """
                {"type":"attachment","sessionId":"11111111-1111-1111-1111-111111111111","attachment":{"type":"task_reminder","text":"IGNORED_ATTACHMENT_MARKER"}}
                {"type":"permission-mode","sessionId":"11111111-1111-1111-1111-111111111111","permissionMode":"fixture"}
                {"type":"mode","sessionId":"11111111-1111-1111-1111-111111111111","mode":"fixture"}
                {"type":"file-history-delta","sessionId":"11111111-1111-1111-1111-111111111111","trackingPath":"IGNORED_HISTORY_MARKER"}
                {"type":"atis-latch","sessionId":"11111111-1111-1111-1111-111111111111","atis":{}}
                {"type":"bridge-session","sessionId":"11111111-1111-1111-1111-111111111111","bridgeSessionId":"fixture"}
                {"type":"frame-link","sessionId":"11111111-1111-1111-1111-111111111111","title":"IGNORED_FRAME_MARKER"}
                {"type":"artifact-autoreact-ledger","sessionId":"11111111-1111-1111-1111-111111111111","artifacts":[]}
                {"type":"artifact-comment-monitor","sessionId":"11111111-1111-1111-1111-111111111111","artifacts":[]}
                {"type":"cost-state","sessionId":"11111111-1111-1111-1111-111111111111","totalCostUSD":0}
                {"type":"fork-context-ref","sessionId":"11111111-1111-1111-1111-111111111111","contextLength":0}
                """;
            await File.AppendAllTextAsync(
                sourcePath,
                envelopes + Environment.NewLine,
                TestContext.Current.CancellationToken);

            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
                tempRoot,
                TestContext.Current.CancellationToken);
            ProviderSessionSeed session = Assert.Single(
                discovery.Sessions,
                candidate => candidate.Identity.SessionId == MainSessionId);
            ProviderSource source = Assert.Single(
                session.Sources,
                candidate => candidate.Kind == ProviderSourceKind.TopLevel);
            ProviderReadResult read = await adapter.ReadAsync(
                source,
                0,
                TestContext.Current.CancellationToken);

            Assert.Equal(ClaudeSessionProviderAdapter.CurrentParserVersion, source.ParserVersion);
            Assert.False(discovery.IsPartial);
            Assert.False(read.IsPartial);
            Assert.Contains(
                read.Records,
                record => record.Text.Contains("tile cache key", StringComparison.Ordinal));
            Assert.DoesNotContain(
                read.Records,
                record => record.Text.Contains("IGNORED_", StringComparison.Ordinal));
            Assert.DoesNotContain(
                read.Diagnostics,
                diagnostic => diagnostic.Code == "claude.record-unknown");
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }

    [Fact(DisplayName = "feat-001/AC-17: Claude parser v2 keeps unseen root types partial")]
    public async Task ReadAsyncKeepsUnseenRootRecordTypesPartial()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
                tempRoot,
                TestContext.Current.CancellationToken);
            ProviderSessionSeed session = Assert.Single(
                discovery.Sessions,
                candidate => candidate.Identity.SessionId == MainSessionId);
            ProviderSource source = Assert.Single(
                session.Sources,
                candidate => candidate.Kind == ProviderSourceKind.TopLevel);
            await File.AppendAllTextAsync(
                source.CanonicalPath,
                "{\"type\":\"future-provider-shape\",\"sessionId\":\"11111111-1111-1111-1111-111111111111\"}" + Environment.NewLine,
                TestContext.Current.CancellationToken);

            ProviderReadResult read = await adapter.ReadAsync(
                source,
                0,
                TestContext.Current.CancellationToken);

            Assert.True(read.IsPartial);
            Assert.Contains(
                read.Diagnostics,
                diagnostic => diagnostic.Code == "claude.record-unknown");
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }

    [Fact(DisplayName = "feat-001/AC-17, AC-18: Claude read retries malformed data and rejects relative escape aliases")]
    public async Task ReadAsyncReportsMalformedRecordsAndRejectsEscapeAliases()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(tempRoot, TestContext.Current.CancellationToken);
            ProviderSessionSeed main = Assert.Single(discovery.Sessions, session => session.Identity.SessionId == MainSessionId);
            ProviderSource source = Assert.Single(main.Sources, item => item.Kind == ProviderSourceKind.TopLevel);
            long originalLength = new FileInfo(source.CanonicalPath).Length;
            await File.AppendAllTextAsync(
                source.CanonicalPath,
                "{not-valid-json}" + Environment.NewLine,
                TestContext.Current.CancellationToken);

            ProviderReadResult malformed = await adapter.ReadAsync(
                source,
                originalLength,
                TestContext.Current.CancellationToken);

            Assert.True(malformed.IsPartial);
            Assert.Empty(malformed.Records);
            Assert.Equal(new FileInfo(source.CanonicalPath).Length, malformed.LastCompleteOffset);
            Assert.Contains(malformed.Diagnostics, diagnostic => diagnostic.Code == "claude.record-malformed");

            ProviderReadResult escaped = await adapter.ReadAsync(
                source with { RelativePath = Path.Combine("..", "escaped.jsonl") },
                0,
                TestContext.Current.CancellationToken);

            Assert.True(escaped.IsPartial);
            Assert.Empty(escaped.Records);
            Assert.Contains(escaped.Diagnostics, diagnostic => diagnostic.Code == "claude.source-rejected");
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }

    [Fact(DisplayName = "feat-001/AC-1, AC-17: Claude metadata discovery is bounded before full transcript parsing")]
    public async Task DiscoverAsyncUsesBoundedMetadataScanBeforeTranscriptRead()
    {
        string tempRoot = ClaudeFixtureRoot.CopyToTemporaryDirectory();
        try
        {
            Guid sessionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
            string projectDirectory = Directory.GetDirectories(Path.Combine(tempRoot, "projects"), "*", SearchOption.TopDirectoryOnly).Single();
            string sourcePath = Path.Combine(projectDirectory, sessionId.ToString("D") + ".jsonl");
            const string user = """{"type":"user","sessionId":"77777777-7777-7777-7777-777777777777","cwd":"C:\\repos\\fixture","userType":"external","message":{"role":"user","content":"Bounded metadata prompt"},"timestamp":"2026-08-26T13:00:00Z"}""";
            const string assistant = """{"type":"assistant","sessionId":"77777777-7777-7777-7777-777777777777","cwd":"C:\\repos\\fixture","message":{"role":"assistant","model":"claude-sonnet-4-5","content":"Bounded metadata answer"},"timestamp":"2026-08-26T13:01:00Z"}""";
            string padding = "{\"type\":\"progress\",\"sessionId\":\"77777777-7777-7777-7777-777777777777\",\"payload\":\""
                + new string('p', 70 * 1024)
                + "\"}";
            string content = string.Join(
                Environment.NewLine,
                user,
                assistant,
                padding,
                "{malformed-tail}")
                + Environment.NewLine;
            await File.WriteAllTextAsync(
                sourcePath,
                content,
                TestContext.Current.CancellationToken);

            var adapter = new ClaudeSessionProviderAdapter();
            ProviderDiscoveryResult discovery = await adapter.DiscoverAsync(
                tempRoot,
                TestContext.Current.CancellationToken);
            ProviderSessionSeed session = Assert.Single(
                discovery.Sessions,
                item => item.Identity.SessionId == sessionId);

            Assert.True(session.FormatSupported);
            Assert.DoesNotContain(
                discovery.Diagnostics,
                diagnostic => diagnostic.SourceAlias.EndsWith(
                    sessionId.ToString("D") + ".jsonl",
                    StringComparison.OrdinalIgnoreCase)
                    && diagnostic.Code == "claude.record-malformed");

            ProviderReadResult read = await adapter.ReadAsync(
                Assert.Single(session.Sources),
                0,
                TestContext.Current.CancellationToken);

            Assert.True(read.IsPartial);
            Assert.Contains(read.Records, record => record.Text == "Bounded metadata prompt");
            Assert.Contains(read.Records, record => record.Text == "Bounded metadata answer");
            Assert.Contains(read.Diagnostics, diagnostic => diagnostic.Code == "claude.record-malformed");
        }
        finally
        {
            ClaudeFixtureRoot.DeleteTemporaryDirectory(tempRoot);
        }
    }
}

internal sealed record ClaudeFileFingerprint(long Length, DateTime LastWriteUtc, string Sha256);

internal static class ClaudeFixtureRoot
{
    private const string TemporaryDirectoryName = "SessionSearch-ClaudeProviderTests";

    public static string Find()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "tests", "Fixtures", "Claude");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The Claude fixture root could not be located.");
    }

    public static IReadOnlyDictionary<string, ClaudeFileFingerprint> Fingerprint(string rootPath) =>
        Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(rootPath, path),
                path =>
                {
                    var info = new FileInfo(path);
                    return new ClaudeFileFingerprint(
                        info.Length,
                        info.LastWriteTimeUtc,
                        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
                },
                StringComparer.OrdinalIgnoreCase);

    public static string CopyToTemporaryDirectory()
    {
        string basePath = Path.Combine(Path.GetTempPath(), TemporaryDirectoryName);
        string destination = Path.Combine(basePath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);

        foreach (string directory in Directory.EnumerateDirectories(Find(), "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(Find(), directory)));
        }

        foreach (string file in Directory.EnumerateFiles(Find(), "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(Find(), file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return destination;
    }

    public static void DeleteTemporaryDirectory(string path)
    {
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), TemporaryDirectoryName));
        string target = Path.GetFullPath(path);
        if (!target.StartsWith(expectedParent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a directory outside the Claude provider test root.");
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
