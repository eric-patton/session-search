using SessionSearch.Core.Models;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

public sealed class ResumePlannerTests
{
    private static readonly TrustedExecutableProfile ClaudeProfile = new(
        TrustedExecutableKind.ClaudeCode,
        "claude.exe",
        ["Anthropic, PBC"]);

    private static readonly TrustedExecutableProfile CodexProfile = new(
        TrustedExecutableKind.Codex,
        "codex.exe",
        ["OpenAI, L.L.C."]);

    private static readonly TrustedExecutableProfile TerminalProfile = new(
        TrustedExecutableKind.WindowsTerminal,
        "wt.exe",
        ["Microsoft Corporation"]);

    [Theory]
    [InlineData(SessionProvider.ClaudeCode, "--resume")]
    [InlineData(SessionProvider.Codex, "resume")]
    // feat-001/AC-9
    public void Feat001Ac9PlanUsesExactStructuredWindowsTerminalArguments(
        SessionProvider provider,
        string resumeVerb)
    {
        var revalidator = new FakeResumePlanRevalidator();
        var planner = new ResumePlanner(revalidator);
        Guid sessionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        ResumeRequest request = CreateRequest(provider, sessionId, @"C:\Repos\Project");

        ResumePlan plan = planner.Create(request);
        System.Diagnostics.ProcessStartInfo startInfo = plan.CreateStartInfo();

        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(@"C:\Tools\wt.exe", startInfo.FileName);
        Assert.Equal(
            [
                "-w",
                "0",
                "new-tab",
                "--startingDirectory",
                @"C:\Repos\Project",
                provider == SessionProvider.ClaudeCode
                    ? @"C:\Tools\claude.exe"
                    : @"C:\Tools\codex.exe",
                resumeVerb,
                sessionId.ToString("D"),
            ],
            startInfo.ArgumentList);
    }

    [Fact]
    // feat-001/AC-9
    public void Feat001Ac9EmptyUuidIsRejected()
    {
        var planner = new ResumePlanner(new FakeResumePlanRevalidator());
        ResumeRequest request = CreateRequest(SessionProvider.ClaudeCode, Guid.Empty, @"C:\Repos\Project");

        Assert.Throws<ArgumentException>(() => planner.Create(request));
    }

    [Fact]
    // feat-001/AC-10
    public void Feat001Ac10PowerShellFormatterDoublesApostrophesInEveryLiteral()
    {
        var planner = new ResumePlanner(new FakeResumePlanRevalidator());
        ResumePlan plan = planner.Create(CreateRequest(
            SessionProvider.ClaudeCode,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            @"C:\Eric's Repo"));

        string command = PowerShellCommandFormatter.Format(plan);

        Assert.Equal(
            "Set-Location -LiteralPath 'C:\\Eric''s Repo'; & 'C:\\Tools\\claude.exe' '--resume' '11111111-1111-1111-1111-111111111111'",
            command);
    }

    [Fact]
    // feat-001/AC-11
    public void Feat001Ac11BatchPlanningDeduplicatesProviderAndUuidInVisibleOrder()
    {
        var planner = new ResumePlanner(new FakeResumePlanRevalidator());
        Guid sharedId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid secondId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        ResumeRequest[] requests =
        [
            CreateRequest(SessionProvider.ClaudeCode, sharedId, @"C:\First"),
            CreateRequest(SessionProvider.ClaudeCode, sharedId, @"C:\Duplicate"),
            CreateRequest(SessionProvider.Codex, sharedId, @"C:\OtherProvider"),
            CreateRequest(SessionProvider.ClaudeCode, secondId, @"C:\Second"),
        ];

        IReadOnlyList<ResumePlan> plans = planner.CreateBatch(requests);

        Assert.Equal(3, plans.Count);
        Assert.Equal(
            [
                new SessionIdentity(SessionProvider.ClaudeCode, sharedId),
                new SessionIdentity(SessionProvider.Codex, sharedId),
                new SessionIdentity(SessionProvider.ClaudeCode, secondId),
            ],
            plans.Select(plan => plan.Identity));
    }

    [Fact]
    // feat-001/AC-9
    public async Task Feat001Ac9RecordingLauncherRevalidatesAndDoesNotStartAProcess()
    {
        var revalidator = new FakeResumePlanRevalidator(true, true);
        var planner = new ResumePlanner(revalidator);
        ResumePlan plan = planner.Create(CreateRequest(
            SessionProvider.ClaudeCode,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            @"C:\Repos\Project"));
        var launcher = new RecordingProcessLauncher(revalidator);

        ProcessLaunchResult result = await launcher.LaunchAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.True(result.Started);
        Assert.Null(result.ProcessId);
        Assert.Single(launcher.Starts);
        Assert.Equal(2, revalidator.Calls);
    }

    [Fact]
    // feat-001/AC-18
    public async Task Feat001Ac18RecordingLauncherBlocksFailedDispatchRevalidation()
    {
        var revalidator = new FakeResumePlanRevalidator(true, false);
        var planner = new ResumePlanner(revalidator);
        ResumePlan plan = planner.Create(CreateRequest(
            SessionProvider.ClaudeCode,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            @"C:\Repos\Project"));
        var launcher = new RecordingProcessLauncher(revalidator);

        ProcessLaunchResult result = await launcher.LaunchAsync(
            plan,
            TestContext.Current.CancellationToken);

        Assert.False(result.Started);
        Assert.Empty(launcher.Starts);
    }

    private static ResumeRequest CreateRequest(
        SessionProvider provider,
        Guid sessionId,
        string directory)
    {
        TrustedExecutableProfile providerProfile = provider == SessionProvider.ClaudeCode
            ? ClaudeProfile
            : CodexProfile;
        string providerPath = provider == SessionProvider.ClaudeCode
            ? @"C:\Tools\claude.exe"
            : @"C:\Tools\codex.exe";

        return new ResumeRequest(
            new SessionIdentity(provider, sessionId),
            directory,
            new ResolvedExecutable(
                providerProfile,
                providerPath,
                "provider-file-id",
                providerProfile.ExpectedPublishers[0],
                false),
            new ResolvedExecutable(
                TerminalProfile,
                @"C:\Tools\wt.exe",
                "terminal-file-id",
                "Microsoft Corporation",
                false));
    }
}
