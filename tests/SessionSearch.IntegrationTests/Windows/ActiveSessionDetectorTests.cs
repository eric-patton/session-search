using SessionSearch.Core.Models;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

public sealed class ActiveSessionDetectorTests
{
    private static readonly SessionIdentity Session = new(
        SessionProvider.ClaudeCode,
        Guid.Parse("11111111-1111-1111-1111-111111111111"));

    private static readonly DateTimeOffset StartTime = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8MatchingMarkerPidExecutableAndStartFingerprintIsActive()
    {
        var marker = new ClaudeActivityMarker(Session, 42, @"C:\Tools\claude.exe", StartTime);
        ProcessSnapshot[] processes =
        [
            new(42, @"C:\Tools\claude.exe", StartTime, []),
        ];

        ClaudeActiveSessionResult result = ClaudeActiveSessionDetector.Detect(
            Session,
            @"C:\Tools\claude.exe",
            marker,
            processes);

        Assert.Equal(ActiveSessionState.Active, result.State);
        Assert.False(result.HasUnmappedClaudeActivity);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8MatchingMarkerWithoutStartFingerprintIsPossiblyActive()
    {
        var marker = new ClaudeActivityMarker(Session, 42, @"C:\Tools\claude.exe", null);
        ProcessSnapshot[] processes =
        [
            new(42, @"C:\Tools\claude.exe", StartTime, []),
        ];

        ClaudeActiveSessionResult result = ClaudeActiveSessionDetector.Detect(
            Session,
            @"C:\Tools\claude.exe",
            marker,
            processes);

        Assert.Equal(ActiveSessionState.PossiblyActive, result.State);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    // feat-001/AC-8
    public void Feat001Ac8StaleOrDifferentExecutableMarkerDoesNotBlock(
        bool staleStart,
        bool differentExecutable)
    {
        var marker = new ClaudeActivityMarker(Session, 42, @"C:\Tools\claude.exe", StartTime);
        ProcessSnapshot[] processes =
        [
            new(
                42,
                differentExecutable ? @"C:\Attacker\claude.exe" : @"C:\Tools\claude.exe",
                staleStart ? StartTime.AddMinutes(1) : StartTime,
                []),
        ];

        ClaudeActiveSessionResult result = ClaudeActiveSessionDetector.Detect(
            Session,
            @"C:\Tools\claude.exe",
            marker,
            processes);

        Assert.Equal(ActiveSessionState.Inactive, result.State);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8ExplicitResumeArgumentsMapLiveClaudeProcess()
    {
        ProcessSnapshot[] processes =
        [
            new(
                84,
                @"C:\Tools\claude.exe",
                StartTime,
                [
                    "--dangerously-skip-permissions",
                    "--resume",
                    Session.SessionId.ToString("D"),
                ]),
        ];

        ClaudeActiveSessionResult result = ClaudeActiveSessionDetector.Detect(
            Session,
            @"C:\Tools\claude.exe",
            marker: null,
            processes);

        Assert.Equal(ActiveSessionState.Active, result.State);
    }

    [Fact]
    // feat-001/AC-8
    public void Feat001Ac8UnmappedExpectedClaudeProcessProducesOnlyWarningState()
    {
        ProcessSnapshot[] processes =
        [
            new(84, @"C:\Tools\claude.exe", StartTime, ["--some-other-operation"]),
        ];

        ClaudeActiveSessionResult result = ClaudeActiveSessionDetector.Detect(
            Session,
            @"C:\Tools\claude.exe",
            marker: null,
            processes);

        Assert.Equal(ActiveSessionState.Inactive, result.State);
        Assert.True(result.HasUnmappedClaudeActivity);
    }
}
