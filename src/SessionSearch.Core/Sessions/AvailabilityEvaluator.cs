using SessionSearch.Core.Models;

namespace SessionSearch.Core.Sessions;

public sealed record AvailabilityInputs(
    bool FormatSupported = true,
    bool SourcePresent = true,
    bool Archived = false,
    bool Active = false,
    bool PossiblyActive = false,
    bool DirectorySafe = true,
    bool DirectoryExists = true,
    bool CliExists = true);

public sealed record AvailabilityDecision(
    AvailabilityStatus Status,
    bool CanOpen,
    bool CanCopy,
    string Reason,
    string SafeAction);

public static class AvailabilityEvaluator
{
    public static AvailabilityDecision Evaluate(AvailabilityInputs inputs)
    {
        if (!inputs.FormatSupported)
        {
            return Blocked(
                AvailabilityStatus.UnsupportedFormat,
                "The provider record format is not supported.",
                "Update the app or provider adapter, then Rescan.");
        }

        if (!inputs.SourcePresent)
        {
            return Blocked(
                AvailabilityStatus.SourceRemoved,
                "The provider source has been removed.",
                "Remove the stale favorite or restore provider storage.");
        }

        if (inputs.Archived)
        {
            return Blocked(
                AvailabilityStatus.Archived,
                "The provider has archived this session.",
                "Unarchive with the provider, then Rescan.");
        }

        if (inputs.Active)
        {
            return Blocked(
                AvailabilityStatus.Active,
                "The session is already active.",
                "Return to the already active provider session.");
        }

        if (inputs.PossiblyActive)
        {
            return Blocked(
                AvailabilityStatus.PossiblyActive,
                "The session may still be active.",
                "Check the provider process, then close it or wait for its marker to clear.");
        }

        if (!inputs.DirectorySafe)
        {
            return Blocked(
                AvailabilityStatus.UnsafeDirectory,
                "The recorded directory is not a canonical local fixed-drive path.",
                "Move or restore the project to a canonical local fixed-drive path.");
        }

        if (!inputs.DirectoryExists)
        {
            return Blocked(
                AvailabilityStatus.MissingDirectory,
                "The recorded directory does not exist.",
                "Restore the recorded directory, then Rescan.");
        }

        if (!inputs.CliExists)
        {
            return Blocked(
                AvailabilityStatus.MissingCli,
                "The provider command-line tool is not available.",
                "Install or expose the provider CLI, then Rescan.");
        }

        return new AvailabilityDecision(
            AvailabilityStatus.Ready,
            true,
            true,
            "The session is ready to resume.",
            "Open it or paste the exact command.");
    }

    private static AvailabilityDecision Blocked(
        AvailabilityStatus status,
        string reason,
        string safeAction) =>
        new(status, false, false, reason, safeAction);
}
