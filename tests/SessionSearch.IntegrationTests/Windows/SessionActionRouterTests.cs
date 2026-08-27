using SessionSearch.Core.Models;
using SessionSearch.Core.Sessions;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

public sealed class SessionActionRouterTests
{
    private static readonly TrustedExecutableProfile ClaudeProfile = new(
        TrustedExecutableKind.ClaudeCode,
        "claude.exe",
        ["Anthropic, PBC"]);

    private static readonly TrustedExecutableProfile CodexProfile = new(
        TrustedExecutableKind.Codex,
        "codex.exe",
        ["OpenAI OpCo, LLC"]);

    private static readonly TrustedExecutableProfile TerminalProfile = new(
        TrustedExecutableKind.WindowsTerminal,
        "wt.exe",
        ["Microsoft Corporation"]);

    // feat-001/AC-10 feat-001/AC-11 feat-001/AC-12
    [Fact]
    public async Task Feat001Ac11BatchCopyKeepsVisibleOrderAndReportsEverySkipCategory()
    {
        var clipboard = new FakePrivateClipboard();
        var launcher = new FakeProcessLauncher();
        SessionActionRouter router = CreateRouter(clipboard, launcher);
        SessionActionCandidate[] candidates = MixedCandidates();

        SessionBatchActionResult result = await router.CopyAsync(
            candidates,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Succeeded);
        Assert.Equal(1, result.Selection.ActiveOrPossiblyActive);
        Assert.Equal(1, result.Selection.Duplicate);
        Assert.Equal(1, result.Selection.OtherUnavailable);
        string[] lines = clipboard.Text!.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal(
            "Set-Location -LiteralPath 'C:\\First'; & 'C:\\Tools\\claude.exe' '--dangerously-skip-permissions' '--resume' '11111111-1111-1111-1111-111111111111'",
            lines[0]);
        Assert.Equal(
            "Set-Location -LiteralPath 'C:\\Second'; & 'C:\\Tools\\codex.exe' '--yolo' 'resume' '22222222-2222-2222-2222-222222222222'",
            lines[1]);
        Assert.DoesNotContain("#", clipboard.Text, StringComparison.Ordinal);
    }

    // feat-001/AC-9 feat-001/AC-11
    [Fact]
    public async Task Feat001Ac11BatchOpenContinuesAfterOneTabFailure()
    {
        var clipboard = new FakePrivateClipboard();
        var launcher = new FakeProcessLauncher(false, true);
        SessionActionRouter router = CreateRouter(clipboard, launcher);

        SessionBatchActionResult result = await router.OpenAsync(
            MixedCandidates(),
            Terminal(),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, launcher.Plans.Count);
        Assert.Equal(SessionProvider.ClaudeCode, launcher.Plans[0].Identity.Provider);
        Assert.Equal(SessionProvider.Codex, launcher.Plans[1].Identity.Provider);
    }

    // feat-001/AC-10
    [Fact]
    public async Task Feat001Ac10MissingTerminalBlocksOpenButLeavesCopyUsable()
    {
        var clipboard = new FakePrivateClipboard();
        var launcher = new FakeProcessLauncher();
        SessionActionRouter router = CreateRouter(clipboard, launcher);
        SessionActionCandidate ready = MixedCandidates()[0];

        SessionBatchActionResult open = await router.OpenAsync(
            [ready],
            windowsTerminal: null,
            TestContext.Current.CancellationToken);
        SessionBatchActionResult copy = await router.CopyAsync(
            [ready],
            TestContext.Current.CancellationToken);

        Assert.True(open.TerminalMissing);
        Assert.Empty(launcher.Plans);
        Assert.Equal(1, copy.Succeeded);
        Assert.NotNull(clipboard.Text);
    }

    private static SessionActionRouter CreateRouter(
        IPrivateClipboard clipboard,
        IProcessLauncher launcher)
    {
        var planRevalidator = new FakeResumePlanRevalidator();
        return new SessionActionRouter(
            new FakeResumeCommandRevalidator(),
            new ResumePlanner(planRevalidator),
            launcher,
            clipboard);
    }

    private static SessionActionCandidate[] MixedCandidates()
    {
        Guid first = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid second = Guid.Parse("22222222-2222-2222-2222-222222222222");
        SessionActionCandidate claude = Candidate(
            SessionProvider.ClaudeCode,
            first,
            AvailabilityEvaluator.Evaluate(new AvailabilityInputs()),
            ClaudeProfile,
            @"C:\Tools\claude.exe",
            @"C:\First");
        return
        [
            claude,
            claude with { WorkingDirectory = @"C:\Duplicate" },
            Candidate(
                SessionProvider.ClaudeCode,
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                AvailabilityEvaluator.Evaluate(new AvailabilityInputs(Active: true)),
                ClaudeProfile,
                @"C:\Tools\claude.exe",
                @"C:\Active"),
            Candidate(
                SessionProvider.Codex,
                Guid.Parse("44444444-4444-4444-4444-444444444444"),
                AvailabilityEvaluator.Evaluate(new AvailabilityInputs(DirectoryExists: false)),
                CodexProfile,
                @"C:\Tools\codex.exe",
                @"C:\Missing"),
            Candidate(
                SessionProvider.Codex,
                second,
                AvailabilityEvaluator.Evaluate(new AvailabilityInputs()),
                CodexProfile,
                @"C:\Tools\codex.exe",
                @"C:\Second"),
        ];
    }

    private static SessionActionCandidate Candidate(
        SessionProvider provider,
        Guid sessionId,
        AvailabilityDecision availability,
        TrustedExecutableProfile profile,
        string executablePath,
        string workingDirectory) =>
        new(
            new SessionIdentity(provider, sessionId),
            workingDirectory,
            availability,
            new ResolvedExecutable(
                profile,
                executablePath,
                "file-id",
                profile.ExpectedPublishers[0],
                false));

    private static ResolvedExecutable Terminal() => new(
        TerminalProfile,
        @"C:\Tools\wt.exe",
        "terminal-id",
        "Microsoft Corporation",
        false);

    private sealed class FakeResumeCommandRevalidator : IResumeCommandRevalidator
    {
        public bool Revalidate(ResumeCommand command, out string reason)
        {
            reason = string.Empty;
            return true;
        }
    }

    private sealed class FakePrivateClipboard : IPrivateClipboard
    {
        public string? Text { get; private set; }

        public Task<PrivateClipboardResult> WriteTextAsync(
            string text,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Text = text;
            return Task.FromResult(new PrivateClipboardResult(
                true,
                PrivateClipboardFailure.None,
                1,
                "Copied."));
        }
    }

    private sealed class FakeProcessLauncher(params bool[] outcomes) : IProcessLauncher
    {
        private readonly Queue<bool> outcomes = new(outcomes.Length == 0 ? [true] : outcomes);

        public List<ResumePlan> Plans { get; } = [];

        public ValueTask<ProcessLaunchResult> LaunchAsync(
            ResumePlan plan,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plans.Add(plan);
            bool outcome = outcomes.Count > 1 ? outcomes.Dequeue() : outcomes.Peek();
            return ValueTask.FromResult(new ProcessLaunchResult(
                outcome,
                outcome ? 42 : null,
                outcome ? null : "Injected failure"));
        }
    }
}
