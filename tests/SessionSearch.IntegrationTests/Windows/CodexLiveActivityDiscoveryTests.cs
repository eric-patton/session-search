using System.Runtime.Versioning;
using SessionSearch.Core.Models;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

[SupportedOSPlatform("windows")]
public sealed class CodexLiveActivityDiscoveryTests
{
    private static readonly SessionIdentity RootSession = new(
        SessionProvider.Codex,
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private static readonly Guid ChildSessionId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8HeldRootLockIsDiscoveredReadOnly()
    {
        using var workspace = new CodexLockTestWorkspace();
        string rootLock = workspace.CreateLock(RootSession.SessionId);
        using FileStream heldLock = HoldLock(rootLock);
        CodexLiveActivityDiscovery discovery = CreatePhysicalDiscovery();

        CodexLiveActivityResult result = discovery.Detect(
            workspace.Root,
            RootSession,
            []);

        Assert.Equal(ActiveSessionState.Active, result.State);
        Assert.Equal(RootSession, result.ActiveOwner);
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.InspectedLockCount);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8HeldChildLockRollsUpToSuppliedRoot()
    {
        using var workspace = new CodexLockTestWorkspace();
        string childLock = workspace.CreateLock(ChildSessionId);
        using FileStream heldLock = HoldLock(childLock);
        CodexLiveActivityDiscovery discovery = CreatePhysicalDiscovery();

        CodexLiveActivityResult result = discovery.Detect(
            workspace.Root,
            RootSession,
            [ChildSessionId]);

        Assert.Equal(ActiveSessionState.Active, result.State);
        Assert.Equal(RootSession, result.ActiveOwner);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.InspectedLockCount);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8OpenableStaleLockDoesNotBlock()
    {
        using var workspace = new CodexLockTestWorkspace();
        workspace.CreateLock(RootSession.SessionId);
        CodexLiveActivityDiscovery discovery = CreatePhysicalDiscovery();

        CodexLiveActivityResult result = discovery.Detect(
            workspace.Root,
            RootSession,
            [ChildSessionId]);

        Assert.Equal(ActiveSessionState.Inactive, result.State);
        Assert.Null(result.ActiveOwner);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.InspectedLockCount);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8MissingLockDirectoryMeansNoActiveWriter()
    {
        string root = Directory.CreateDirectory(
            Path.Combine(
                Path.GetTempPath(),
                "session-search-codex-lock-tests",
                Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            CodexLiveActivityResult result = CreatePhysicalDiscovery().Detect(
                root,
                RootSession,
                []);

            Assert.Equal(ActiveSessionState.Inactive, result.State);
            Assert.True(result.IsComplete);
            Assert.Equal(0, result.InspectedLockCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    // feat-001/AC-17
    public void Feat001Ac17ChildLimitProducesConservativePossiblyActiveResult()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var lockProbe = new CountingExclusiveFileProbe(ExclusiveFileState.Openable);
        var pathPolicy = new LocalPathPolicy(pathProbe);
        var discovery = new CodexLiveActivityDiscovery(
            pathPolicy,
            new CodexWriterLockDetector(pathPolicy, lockProbe),
            new CodexActivityDiscoveryOptions(MaxChildSessionCount: 1));

        CodexLiveActivityResult result = discovery.Detect(
            @"C:\Codex",
            RootSession,
            [
                ChildSessionId,
                Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            ]);

        Assert.Equal(ActiveSessionState.PossiblyActive, result.State);
        Assert.False(result.IsComplete);
        Assert.Equal(2, result.InspectedLockCount);
        Assert.Equal(2, lockProbe.Calls);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18RemoteCodexRootIsRejectedBeforePathOrLockProbe()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var lockProbe = new CountingExclusiveFileProbe(ExclusiveFileState.SharingViolation);
        var pathPolicy = new LocalPathPolicy(pathProbe);
        var discovery = new CodexLiveActivityDiscovery(
            pathPolicy,
            new CodexWriterLockDetector(pathPolicy, lockProbe));

        CodexLiveActivityResult result = discovery.Detect(
            @"\\attacker\share",
            RootSession,
            [ChildSessionId]);

        Assert.Equal(ActiveSessionState.Inactive, result.State);
        Assert.False(result.IsComplete);
        Assert.Equal(0, pathProbe.Calls);
        Assert.Equal(0, lockProbe.Calls);
    }

    private static CodexLiveActivityDiscovery CreatePhysicalDiscovery()
    {
        var pathPolicy = new LocalPathPolicy(new PhysicalWindowsPathProbe());
        return new CodexLiveActivityDiscovery(
            pathPolicy,
            new CodexWriterLockDetector(pathPolicy, new ExclusiveFileProbe()));
    }

    private static FileStream HoldLock(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read);

    private sealed class CountingExclusiveFileProbe(ExclusiveFileState state)
        : IExclusiveFileProbe
    {
        public int Calls { get; private set; }

        public ExclusiveFileProbeResult Probe(string path)
        {
            Calls++;
            return new ExclusiveFileProbeResult(state);
        }
    }

    private sealed class CodexLockTestWorkspace : IDisposable
    {
        public CodexLockTestWorkspace()
        {
            Root = Directory.CreateDirectory(
                Path.Combine(
                    Path.GetTempPath(),
                    "session-search-codex-lock-tests",
                    Guid.NewGuid().ToString("N"))).FullName;
            LockDirectory = Directory.CreateDirectory(
                Path.Combine(Root, "thread-writer-locks")).FullName;
        }

        public string Root { get; }

        private string LockDirectory { get; }

        public string CreateLock(Guid sessionId)
        {
            string path = Path.Combine(LockDirectory, $"{sessionId:D}.lock");
            using (File.Create(path))
            {
            }

            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
