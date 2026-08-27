using SessionSearch.Core.Models;

namespace SessionSearch.Infrastructure.Windows;

public enum ActiveSessionState
{
    Inactive,
    Active,
    PossiblyActive,
}

public sealed record ProcessSnapshot(
    int ProcessId,
    string ExecutablePath,
    DateTimeOffset StartTimeUtc,
    IReadOnlyList<string> Arguments);

public sealed record ClaudeActivityMarker(
    SessionIdentity Session,
    int ProcessId,
    string ExpectedExecutablePath,
    DateTimeOffset? ProcessStartUtc);

public sealed record ClaudeActiveSessionResult(
    ActiveSessionState State,
    bool HasUnmappedClaudeActivity,
    string Reason);

public static class ClaudeActiveSessionDetector
{
    public static ClaudeActiveSessionResult Detect(
        SessionIdentity session,
        string expectedExecutablePath,
        ClaudeActivityMarker? marker,
        IReadOnlyCollection<ProcessSnapshot> processes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedExecutablePath);

        if (marker is not null && marker.Session == session)
        {
            ProcessSnapshot? markedProcess = processes.FirstOrDefault(
                process => process.ProcessId == marker.ProcessId);

            if (markedProcess is not null &&
                PathsEqual(markedProcess.ExecutablePath, marker.ExpectedExecutablePath) &&
                PathsEqual(markedProcess.ExecutablePath, expectedExecutablePath))
            {
                if (marker.ProcessStartUtc is null)
                {
                    return new ClaudeActiveSessionResult(
                        ActiveSessionState.PossiblyActive,
                        false,
                        "The live provider process matches the marker, but the marker has no start fingerprint.");
                }

                if (markedProcess.StartTimeUtc == marker.ProcessStartUtc.Value)
                {
                    return new ClaudeActiveSessionResult(
                        ActiveSessionState.Active,
                        false,
                        "The marker PID, executable, and process start fingerprint match.");
                }
            }
        }

        bool hasUnmappedClaudeActivity = false;
        foreach (ProcessSnapshot process in processes)
        {
            if (!PathsEqual(process.ExecutablePath, expectedExecutablePath))
            {
                continue;
            }

            if (ContainsClaudeResumeArguments(process.Arguments, session.SessionId))
            {
                return new ClaudeActiveSessionResult(
                    ActiveSessionState.Active,
                    false,
                    "A live expected provider process has the immutable session ID in its resume arguments.");
            }

            hasUnmappedClaudeActivity = true;
        }

        return new ClaudeActiveSessionResult(
            ActiveSessionState.Inactive,
            hasUnmappedClaudeActivity,
            "No live expected provider process maps to the session.");
    }

    private static bool ContainsClaudeResumeArguments(IReadOnlyList<string> arguments, Guid sessionId)
    {
        string expected = sessionId.ToString("D");
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--resume", StringComparison.Ordinal) &&
                index + 1 < arguments.Count &&
                string.Equals(arguments[index + 1], expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (argument.StartsWith("--resume=", StringComparison.Ordinal) &&
                string.Equals(argument[9..], expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

public enum ExclusiveFileState
{
    Missing,
    Openable,
    SharingViolation,
    Error,
}

public sealed record ExclusiveFileProbeResult(ExclusiveFileState State, string? ErrorCode = null);

public interface IExclusiveFileProbe
{
    ExclusiveFileProbeResult Probe(string path);
}

public sealed class ExclusiveFileProbe : IExclusiveFileProbe
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public ExclusiveFileProbeResult Probe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return new ExclusiveFileProbeResult(ExclusiveFileState.Openable);
        }
        catch (FileNotFoundException)
        {
            return new ExclusiveFileProbeResult(ExclusiveFileState.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            return new ExclusiveFileProbeResult(ExclusiveFileState.Missing);
        }
        catch (IOException exception) when (
            (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation)
        {
            return new ExclusiveFileProbeResult(ExclusiveFileState.SharingViolation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ExclusiveFileProbeResult(ExclusiveFileState.Error, exception.GetType().Name);
        }
    }
}

public sealed record CodexWriterLockCandidate(
    string LockPath,
    SessionIdentity LockOwner,
    SessionIdentity RootOwner);

public sealed record CodexWriterLockResult(
    ActiveSessionState State,
    SessionIdentity? ActiveOwner,
    string Reason);

public sealed class CodexWriterLockDetector(
    LocalPathPolicy pathPolicy,
    IExclusiveFileProbe fileProbe)
{
    public CodexWriterLockResult Detect(CodexWriterLockCandidate candidate, string trustedRoot)
    {
        LocalPathValidation validation = pathPolicy.ValidateExistingFile(
            candidate.LockPath,
            Path.GetFileName(candidate.LockPath),
            trustedRoot: trustedRoot);

        if (!validation.IsSafe)
        {
            return new CodexWriterLockResult(
                ActiveSessionState.Inactive,
                null,
                $"The writer lock path is unavailable or unsafe: {validation.Failure}.");
        }

        ExclusiveFileProbeResult probeResult = fileProbe.Probe(validation.CanonicalPath!);
        return probeResult.State == ExclusiveFileState.SharingViolation
            ? new CodexWriterLockResult(
                ActiveSessionState.Active,
                candidate.RootOwner,
                candidate.LockOwner == candidate.RootOwner
                    ? "The root writer lock is held."
                    : "A child writer lock is held and rolls up to its root owner.")
            : new CodexWriterLockResult(
                ActiveSessionState.Inactive,
                null,
                $"The writer lock is not held: {probeResult.State}.");
    }
}
