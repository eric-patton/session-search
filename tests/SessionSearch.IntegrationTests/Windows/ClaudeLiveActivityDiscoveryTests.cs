using System.Diagnostics;
using System.Runtime.Versioning;
using SessionSearch.Core.Models;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

[SupportedOSPlatform("windows")]
public sealed class ClaudeLiveActivityDiscoveryTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static readonly DateTimeOffset ProcessStart =
        new(2026, 8, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8ProductionMarkerDiscoveryMapsOnlyResolvedClaudeExecutable()
    {
        using var workspace = new ActivityTestWorkspace();
        string sessions = Directory.CreateDirectory(
            Path.Combine(workspace.Root, "sessions")).FullName;
        File.WriteAllText(
            Path.Combine(sessions, "4242.json"),
            """
            {"pid":4242,"sessionId":"11111111-1111-1111-1111-111111111111","procStart":"2026-08-26T10:00:00.000Z"}
            """);

        var discovery = new ClaudeActivityMarkerDiscovery(
            new LocalPathPolicy(new PhysicalWindowsPathProbe()),
            new PhysicalReadOnlyActivityFileSystem());

        ClaudeActivityMarkerDiscoveryResult result = discovery.Discover(
            workspace.Root,
            ClaudeExecutable());

        ClaudeActivityMarker marker = Assert.Single(result.Markers);
        Assert.True(result.IsComplete);
        Assert.Equal(new SessionIdentity(SessionProvider.ClaudeCode, SessionId), marker.Session);
        Assert.Equal(4242, marker.ProcessId);
        Assert.Equal(@"C:\Tools\claude.exe", marker.ExpectedExecutablePath);
        Assert.Equal(ProcessStart, marker.ProcessStartUtc);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8ScannerReusesPidExecutableAndStartFingerprintDetection()
    {
        using var workspace = new ActivityTestWorkspace();
        string sessions = Directory.CreateDirectory(
            Path.Combine(workspace.Root, "sessions")).FullName;
        File.WriteAllText(
            Path.Combine(sessions, "4242.json"),
            """
            {"pid":4242,"sessionId":"11111111-1111-1111-1111-111111111111","procStart":"2026-08-26T10:00:00.000Z"}
            """);
        var processSource = new FakeProcessSnapshotSource(
            new ProcessSnapshot(
                4242,
                @"C:\Tools\claude.exe",
                ProcessStart,
                []));
        var scanner = new ClaudeLiveActivityScanner(
            new ClaudeActivityMarkerDiscovery(
                new LocalPathPolicy(new PhysicalWindowsPathProbe()),
                new PhysicalReadOnlyActivityFileSystem()),
            processSource);

        ClaudeLiveActivitySnapshot snapshot = scanner.Scan(
            workspace.Root,
            ClaudeExecutable());
        ClaudeActiveSessionResult result = snapshot.Detect(
            new SessionIdentity(SessionProvider.ClaudeCode, SessionId));

        Assert.True(snapshot.IsComplete);
        Assert.Equal(ActiveSessionState.Active, result.State);
        Assert.Equal(1, processSource.Calls);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8SnapshotReportsOnlyTrulyUnmappedClaudeProcessesGlobally()
    {
        ProcessSnapshot unmapped = new(
            4242,
            @"C:\Tools\claude.exe",
            ProcessStart,
            []);
        ClaudeLiveActivitySnapshot warning = new(
            [],
            [unmapped],
            @"C:\Tools\claude.exe",
            isComplete: true,
            "Synthetic complete snapshot.");
        ClaudeLiveActivitySnapshot mappedByArguments = new(
            [],
            [
                unmapped with
                {
                    Arguments =
                    [
                        "--dangerously-skip-permissions",
                        "--resume",
                        SessionId.ToString("D"),
                    ],
                },
            ],
            @"C:\Tools\claude.exe",
            isComplete: true,
            "Synthetic complete snapshot.");

        Assert.True(warning.HasUnmappedClaudeActivity);
        Assert.False(mappedByArguments.HasUnmappedClaudeActivity);
    }

    [Fact]
    // feat-001/AC-17
    public void Feat001Ac17MarkerCountLimitStopsEnumerationConsumptionAndReads()
    {
        var fileSystem = new FakeActivityFileSystem(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Claude\sessions\1.json"] = MarkerJson(1),
                [@"C:\Claude\sessions\2.json"] = MarkerJson(2),
                [@"C:\Claude\sessions\3.json"] = MarkerJson(3),
            });
        var discovery = new ClaudeActivityMarkerDiscovery(
            new LocalPathPolicy(new FakeWindowsPathProbe()),
            fileSystem,
            new ClaudeActivityDiscoveryOptions(MaxMarkerCount: 2));

        ClaudeActivityMarkerDiscoveryResult result = discovery.Discover(
            @"C:\Claude",
            ClaudeExecutable());

        Assert.False(result.IsComplete);
        Assert.Equal(2, result.Markers.Count);
        Assert.Equal(2, fileSystem.OpenCalls);
        Assert.Equal(3, fileSystem.EnumerationMoves);
    }

    [Fact]
    // feat-001/AC-17
    public void Feat001Ac17OversizeAndOverdepthMarkersAreRejectedWithinBounds()
    {
        string oversized = MarkerJson(1) + new string(' ', 256);
        string overdepth =
            """
            {"pid":2,"sessionId":"11111111-1111-1111-1111-111111111111","extra":{"one":{"two":true}}}
            """;
        var fileSystem = new FakeActivityFileSystem(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [@"C:\Claude\sessions\1.json"] = oversized,
                [@"C:\Claude\sessions\2.json"] = overdepth,
            });
        var discovery = new ClaudeActivityMarkerDiscovery(
            new LocalPathPolicy(new FakeWindowsPathProbe()),
            fileSystem,
            new ClaudeActivityDiscoveryOptions(MaxMarkerBytes: 128, MaxJsonDepth: 2));

        ClaudeActivityMarkerDiscoveryResult result = discovery.Discover(
            @"C:\Claude",
            ClaudeExecutable());

        Assert.True(result.IsComplete);
        Assert.Empty(result.Markers);
        Assert.Equal(2, result.RejectedMarkerCount);
        Assert.All(fileSystem.BytesRead, bytesRead => Assert.InRange(bytesRead, 0, 129));
    }

    [Theory]
    [InlineData("wrong-file-name.json", 42, "2026-08-26T10:00:00Z")]
    [InlineData("42.json", 43, "2026-08-26T10:00:00Z")]
    [InlineData("42.json", 42, "not-a-time")]
    // feat-001/AC-8
    public void Feat001Ac8InconsistentOrMalformedMarkersAreRejected(
        string fileName,
        int processId,
        string processStart)
    {
        string path = Path.Combine(@"C:\Claude\sessions", fileName);
        var fileSystem = new FakeActivityFileSystem(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = $$"""
                {"pid":{{processId}},"sessionId":"11111111-1111-1111-1111-111111111111","procStart":"{{processStart}}"}
                """,
            });
        var discovery = new ClaudeActivityMarkerDiscovery(
            new LocalPathPolicy(new FakeWindowsPathProbe()),
            fileSystem);

        ClaudeActivityMarkerDiscoveryResult result = discovery.Discover(
            @"C:\Claude",
            ClaudeExecutable());

        Assert.Empty(result.Markers);
        Assert.Equal(1, result.RejectedMarkerCount);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18RemoteRootIsRejectedBeforeAnyFileSystemProbe()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var fileSystem = new FakeActivityFileSystem(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var discovery = new ClaudeActivityMarkerDiscovery(
            new LocalPathPolicy(pathProbe),
            fileSystem);

        ClaudeActivityMarkerDiscoveryResult result = discovery.Discover(
            @"\\attacker\share",
            ClaudeExecutable());

        Assert.False(result.IsComplete);
        Assert.Empty(result.Markers);
        Assert.Equal(0, pathProbe.Calls);
        Assert.Equal(0, fileSystem.EnumerationMoves);
        Assert.Equal(0, fileSystem.OpenCalls);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8ProductionProcessCaptureDoesNotCollectCommandArguments()
    {
        string executablePath = Environment.ProcessPath!;
        var executable = new ResolvedExecutable(
            new TrustedExecutableProfile(
                TrustedExecutableKind.ClaudeCode,
                Path.GetFileName(executablePath),
                ["Test Publisher"]),
            executablePath,
            "test-identity",
            "Test Publisher",
            false);

        ProcessSnapshotCaptureResult result = new WindowsProcessSnapshotSource().Capture(executable);

        ProcessSnapshot current = Assert.Single(
            result.Processes,
            process => process.ProcessId == Environment.ProcessId);
        Assert.Empty(current.Arguments);
    }

    private static ResolvedExecutable ClaudeExecutable() => new(
        new TrustedExecutableProfile(
            TrustedExecutableKind.ClaudeCode,
            "claude.exe",
            ["Anthropic, PBC"]),
        @"C:\Tools\claude.exe",
        "claude-test-identity",
        "Anthropic, PBC",
        false);

    private static string MarkerJson(int processId) => $$"""
        {"pid":{{processId}},"sessionId":"11111111-1111-1111-1111-111111111111","procStart":"2026-08-26T10:00:00.000Z"}
        """;

    private sealed class FakeActivityFileSystem(
        IReadOnlyDictionary<string, string> files) : IReadOnlyActivityFileSystem
    {
        public int EnumerationMoves { get; private set; }

        public int OpenCalls { get; private set; }

        public List<int> BytesRead { get; } = [];

        public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern)
        {
            foreach (string path in files.Keys)
            {
                EnumerationMoves++;
                yield return path;
            }
        }

        public Stream OpenRead(string filePath)
        {
            OpenCalls++;
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(files[filePath]);
            return new TrackingReadStream(bytes, BytesRead);
        }
    }

    private sealed class TrackingReadStream(byte[] bytes, ICollection<int> reads) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = base.Read(buffer, offset, count);
            reads.Add(read);
            return read;
        }
    }

    private sealed class FakeProcessSnapshotSource(params ProcessSnapshot[] processes)
        : IProcessSnapshotSource
    {
        public int Calls { get; private set; }

        public ProcessSnapshotCaptureResult Capture(ResolvedExecutable expectedExecutable)
        {
            Calls++;
            return new ProcessSnapshotCaptureResult(
                processes,
                true,
                "Injected process snapshots were captured.");
        }
    }

    private sealed class ActivityTestWorkspace : IDisposable
    {
        public ActivityTestWorkspace()
        {
            Root = Directory.CreateDirectory(
                Path.Combine(
                    Path.GetTempPath(),
                    "session-search-activity-tests",
                    Guid.NewGuid().ToString("N"))).FullName;
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
