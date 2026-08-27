using SessionSearch.Core.Models;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

public sealed class CodexWriterLockDetectorTests
{
    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8HeldChildWriterLockRollsUpToRootOwner()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var lockProbe = new FakeExclusiveFileProbe(ExclusiveFileState.SharingViolation);
        var detector = new CodexWriterLockDetector(new LocalPathPolicy(pathProbe), lockProbe);
        var root = new SessionIdentity(
            SessionProvider.Codex,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var child = new SessionIdentity(
            SessionProvider.Codex,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

        CodexWriterLockResult result = detector.Detect(
            new CodexWriterLockCandidate(@"C:\Codex\locks\child.lock", child, root),
            @"C:\Codex");

        Assert.Equal(ActiveSessionState.Active, result.State);
        Assert.Equal(root, result.ActiveOwner);
        Assert.Equal(1, lockProbe.Calls);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8OpenableWriterLockDoesNotBlock()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var lockProbe = new FakeExclusiveFileProbe(ExclusiveFileState.Openable);
        var detector = new CodexWriterLockDetector(new LocalPathPolicy(pathProbe), lockProbe);
        var root = new SessionIdentity(
            SessionProvider.Codex,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        CodexWriterLockResult result = detector.Detect(
            new CodexWriterLockCandidate(@"C:\Codex\locks\root.lock", root, root),
            @"C:\Codex");

        Assert.Equal(ActiveSessionState.Inactive, result.State);
        Assert.Null(result.ActiveOwner);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18UnsafeLockPathIsRejectedBeforeExclusiveProbe()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var lockProbe = new FakeExclusiveFileProbe(ExclusiveFileState.SharingViolation);
        var detector = new CodexWriterLockDetector(new LocalPathPolicy(pathProbe), lockProbe);
        var root = new SessionIdentity(
            SessionProvider.Codex,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

        CodexWriterLockResult result = detector.Detect(
            new CodexWriterLockCandidate(@"\\attacker\share\root.lock", root, root),
            @"C:\Codex");

        Assert.Equal(ActiveSessionState.Inactive, result.State);
        Assert.Equal(0, pathProbe.Calls);
        Assert.Equal(0, lockProbe.Calls);
    }

    private sealed class FakeExclusiveFileProbe(ExclusiveFileState state) : IExclusiveFileProbe
    {
        public int Calls { get; private set; }

        public ExclusiveFileProbeResult Probe(string path)
        {
            Calls++;
            return new ExclusiveFileProbeResult(state);
        }
    }
}
