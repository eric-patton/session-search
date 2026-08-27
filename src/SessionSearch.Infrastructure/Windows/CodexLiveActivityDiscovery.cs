using SessionSearch.Core.Models;

namespace SessionSearch.Infrastructure.Windows;

public sealed record CodexActivityDiscoveryOptions(int MaxChildSessionCount = 512)
{
    public CodexActivityDiscoveryOptions Validate()
    {
        if (MaxChildSessionCount is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxChildSessionCount));
        }

        return this;
    }
}

public sealed record CodexLiveActivityResult(
    ActiveSessionState State,
    SessionIdentity? ActiveOwner,
    bool IsComplete,
    int InspectedLockCount,
    string Reason);

public sealed class CodexLiveActivityDiscovery(
    LocalPathPolicy pathPolicy,
    CodexWriterLockDetector lockDetector,
    CodexActivityDiscoveryOptions? options = null)
{
    private readonly CodexActivityDiscoveryOptions options =
        (options ?? new CodexActivityDiscoveryOptions()).Validate();

    public CodexLiveActivityResult Detect(
        string codexRoot,
        SessionIdentity rootSession,
        IReadOnlyCollection<Guid> childSessionIds)
    {
        if (rootSession.Provider != SessionProvider.Codex || rootSession.SessionId == Guid.Empty)
        {
            throw new ArgumentException("The root session must be a valid Codex session.", nameof(rootSession));
        }

        ArgumentNullException.ThrowIfNull(childSessionIds);

        LocalPathValidation rootValidation = pathPolicy.ValidateExistingDirectory(codexRoot);
        if (!rootValidation.IsSafe)
        {
            return new CodexLiveActivityResult(
                ActiveSessionState.Inactive,
                null,
                rootValidation.Failure == LocalPathFailure.Missing,
                0,
                $"The Codex activity root is unavailable or unsafe: {rootValidation.Failure}.");
        }

        string trustedRoot = rootValidation.CanonicalPath!;
        string lockDirectory = Path.Combine(trustedRoot, "thread-writer-locks");
        LocalPathValidation lockDirectoryValidation = pathPolicy.ValidateExistingDirectory(
            lockDirectory,
            trustedRoot);
        if (!lockDirectoryValidation.IsSafe)
        {
            return new CodexLiveActivityResult(
                ActiveSessionState.Inactive,
                null,
                lockDirectoryValidation.Failure == LocalPathFailure.Missing,
                0,
                lockDirectoryValidation.Failure == LocalPathFailure.Missing
                    ? "No Codex writer-lock directory exists."
                    : $"The Codex writer-lock directory is unsafe: {lockDirectoryValidation.Failure}.");
        }

        var distinctChildren = new HashSet<Guid>();
        bool overflow = false;
        foreach (Guid childSessionId in childSessionIds)
        {
            if (childSessionId == Guid.Empty || childSessionId == rootSession.SessionId)
            {
                continue;
            }

            if (distinctChildren.Count >= options.MaxChildSessionCount &&
                !distinctChildren.Contains(childSessionId))
            {
                overflow = true;
                break;
            }

            distinctChildren.Add(childSessionId);
        }

        var owners = new List<SessionIdentity>(distinctChildren.Count + 1)
        {
            rootSession,
        };
        owners.AddRange(distinctChildren.Select(
            id => new SessionIdentity(SessionProvider.Codex, id)));

        int inspected = 0;
        foreach (SessionIdentity lockOwner in owners)
        {
            string lockPath = Path.Combine(
                lockDirectoryValidation.CanonicalPath!,
                $"{lockOwner.SessionId:D}.lock");
            CodexWriterLockResult result = lockDetector.Detect(
                new CodexWriterLockCandidate(lockPath, lockOwner, rootSession),
                trustedRoot);
            inspected++;

            if (result.State == ActiveSessionState.Active)
            {
                return new CodexLiveActivityResult(
                    result.State,
                    result.ActiveOwner,
                    !overflow,
                    inspected,
                    result.Reason);
            }
        }

        return overflow
            ? new CodexLiveActivityResult(
                ActiveSessionState.PossiblyActive,
                null,
                false,
                inspected,
                "The child writer-lock count exceeded the bounded scan limit.")
            : new CodexLiveActivityResult(
                ActiveSessionState.Inactive,
                null,
                true,
                inspected,
                "No supplied Codex root or child writer lock is held.");
    }
}
