using SessionSearch.Core.Models;
using SessionSearch.Core.Sessions;

namespace SessionSearch.Infrastructure.Windows;

public sealed record SessionActionCandidate(
    SessionIdentity Identity,
    string WorkingDirectory,
    AvailabilityDecision Availability,
    ResolvedExecutable? ProviderExecutable);

public sealed record SessionActionSelectionSummary(
    int Ready,
    int ActiveOrPossiblyActive,
    int Duplicate,
    int OtherUnavailable)
{
    public int Total => Ready + ActiveOrPossiblyActive + Duplicate + OtherUnavailable;
}

public sealed record SessionBatchActionResult(
    SessionActionSelectionSummary Selection,
    int Succeeded,
    int Failed,
    bool TerminalMissing,
    string Message);

public sealed class SessionActionRouter(
    IResumeCommandRevalidator commandRevalidator,
    ResumePlanner planner,
    IProcessLauncher processLauncher,
    IPrivateClipboard clipboard)
{
    public static SessionActionSelectionSummary Summarize(
        IEnumerable<SessionActionCandidate> candidates) =>
        Classify(candidates).Summary;

    public async ValueTask<SessionBatchActionResult> CopyAsync(
        IEnumerable<SessionActionCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ClassifiedSelection classified = Classify(candidates);
        List<string> commands = [];
        int failed = 0;
        foreach (SessionActionCandidate candidate in classified.Ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResumeCommand command = new(
                candidate.Identity,
                candidate.WorkingDirectory,
                candidate.ProviderExecutable!);
            if (!commandRevalidator.Revalidate(command, out _))
            {
                failed++;
                continue;
            }

            commands.Add(PowerShellCommandFormatter.Format(command));
        }

        if (commands.Count == 0)
        {
            return new SessionBatchActionResult(
                classified.Summary,
                0,
                failed,
                false,
                BuildCopyMessage(0, failed, classified.Summary));
        }

        PrivateClipboardResult clipboardResult = await clipboard.WriteTextAsync(
            string.Join(Environment.NewLine, commands),
            cancellationToken).ConfigureAwait(false);
        if (!clipboardResult.Success)
        {
            return new SessionBatchActionResult(
                classified.Summary,
                0,
                failed + commands.Count,
                false,
                $"No commands were copied. {clipboardResult.Message}");
        }

        return new SessionBatchActionResult(
            classified.Summary,
            commands.Count,
            failed,
            false,
            BuildCopyMessage(commands.Count, failed, classified.Summary));
    }

    public async ValueTask<SessionBatchActionResult> OpenAsync(
        IEnumerable<SessionActionCandidate> candidates,
        ResolvedExecutable? windowsTerminal,
        CancellationToken cancellationToken)
    {
        ClassifiedSelection classified = Classify(candidates);
        if (windowsTerminal is null)
        {
            return new SessionBatchActionResult(
                classified.Summary,
                0,
                0,
                true,
                "Windows Terminal is unavailable. Ready commands can still be copied.");
        }

        int opened = 0;
        int failed = 0;
        foreach (SessionActionCandidate candidate in classified.Ready)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                ResumePlan plan = planner.Create(new ResumeRequest(
                    candidate.Identity,
                    candidate.WorkingDirectory,
                    candidate.ProviderExecutable!,
                    windowsTerminal));
                ProcessLaunchResult result = await processLauncher.LaunchAsync(
                    plan,
                    cancellationToken).ConfigureAwait(false);
                if (result.Started)
                {
                    opened++;
                }
                else
                {
                    failed++;
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or InvalidOperationException)
            {
                failed++;
            }
        }

        return new SessionBatchActionResult(
            classified.Summary,
            opened,
            failed,
            false,
            BuildOpenMessage(opened, failed, classified.Summary));
    }

    private static ClassifiedSelection Classify(
        IEnumerable<SessionActionCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        HashSet<SessionIdentity> seen = [];
        List<SessionActionCandidate> ready = [];
        int active = 0;
        int duplicate = 0;
        int unavailable = 0;
        foreach (SessionActionCandidate candidate in candidates)
        {
            if (!seen.Add(candidate.Identity))
            {
                duplicate++;
                continue;
            }

            if (candidate.Availability.Status is
                AvailabilityStatus.Active or AvailabilityStatus.PossiblyActive)
            {
                active++;
            }
            else if (candidate.Availability.Status == AvailabilityStatus.Ready &&
                candidate.ProviderExecutable is not null)
            {
                ready.Add(candidate);
            }
            else
            {
                unavailable++;
            }
        }

        return new ClassifiedSelection(
            ready,
            new SessionActionSelectionSummary(
                ready.Count,
                active,
                duplicate,
                unavailable));
    }

    private static string BuildCopyMessage(
        int copied,
        int failed,
        SessionActionSelectionSummary summary) =>
        $"Copied {copied} command(s). Skipped {summary.ActiveOrPossiblyActive} active, {summary.Duplicate} duplicate, and {summary.OtherUnavailable} unavailable. Failed {failed}.";

    private static string BuildOpenMessage(
        int opened,
        int failed,
        SessionActionSelectionSummary summary) =>
        $"Opened {opened} tab(s). Skipped {summary.ActiveOrPossiblyActive} active, {summary.Duplicate} duplicate, and {summary.OtherUnavailable} unavailable. Failed {failed}.";

    private sealed record ClassifiedSelection(
        IReadOnlyList<SessionActionCandidate> Ready,
        SessionActionSelectionSummary Summary);
}
