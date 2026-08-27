using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using SessionSearch.Core.Models;

namespace SessionSearch.Infrastructure.Windows;

public sealed record ClaudeActivityDiscoveryOptions(
    int MaxMarkerCount = 512,
    int MaxMarkerBytes = 64 * 1024,
    int MaxJsonDepth = 8)
{
    public ClaudeActivityDiscoveryOptions Validate()
    {
        if (MaxMarkerCount is < 1 or > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMarkerCount));
        }

        if (MaxMarkerBytes is < 128 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxMarkerBytes));
        }

        if (MaxJsonDepth is < 2 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxJsonDepth));
        }

        return this;
    }
}

public sealed record ClaudeActivityMarkerDiscoveryResult(
    IReadOnlyList<ClaudeActivityMarker> Markers,
    bool IsComplete,
    int RejectedMarkerCount,
    string Reason);

public interface IReadOnlyActivityFileSystem
{
    IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern);

    Stream OpenRead(string filePath);
}

public sealed class PhysicalReadOnlyActivityFileSystem : IReadOnlyActivityFileSystem
{
    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        MatchCasing = MatchCasing.CaseInsensitive,
    };

    public IEnumerable<string> EnumerateFiles(string directoryPath, string searchPattern) =>
        Directory.EnumerateFiles(directoryPath, searchPattern, EnumerationOptions);

    public Stream OpenRead(string filePath) => new FileStream(
        filePath,
        new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
            BufferSize = 4 * 1024,
        });
}

public sealed class ClaudeActivityMarkerDiscovery(
    LocalPathPolicy pathPolicy,
    IReadOnlyActivityFileSystem fileSystem,
    ClaudeActivityDiscoveryOptions? options = null)
{
    private readonly ClaudeActivityDiscoveryOptions options =
        (options ?? new ClaudeActivityDiscoveryOptions()).Validate();

    public ClaudeActivityMarkerDiscoveryResult Discover(
        string claudeRoot,
        ResolvedExecutable expectedClaudeExecutable)
    {
        ArgumentNullException.ThrowIfNull(expectedClaudeExecutable);
        if (expectedClaudeExecutable.Profile.Kind != TrustedExecutableKind.ClaudeCode)
        {
            throw new ArgumentException(
                "The expected executable must be a resolved Claude Code executable.",
                nameof(expectedClaudeExecutable));
        }

        LocalPathValidation rootValidation = pathPolicy.ValidateExistingDirectory(claudeRoot);
        if (!rootValidation.IsSafe)
        {
            return EmptyResult(
                rootValidation.Failure == LocalPathFailure.Missing,
                $"The Claude activity root is unavailable or unsafe: {rootValidation.Failure}.");
        }

        string trustedRoot = rootValidation.CanonicalPath!;
        string sessionsPath = Path.Combine(trustedRoot, "sessions");
        LocalPathValidation sessionsValidation = pathPolicy.ValidateExistingDirectory(
            sessionsPath,
            trustedRoot);
        if (!sessionsValidation.IsSafe)
        {
            return EmptyResult(
                sessionsValidation.Failure == LocalPathFailure.Missing,
                sessionsValidation.Failure == LocalPathFailure.Missing
                    ? "No Claude activity marker directory exists."
                    : $"The Claude activity marker directory is unsafe: {sessionsValidation.Failure}.");
        }

        var markers = new List<ClaudeActivityMarker>();
        int rejected = 0;
        bool complete = true;
        string reason = string.Empty;
        int inspected = 0;

        try
        {
            foreach (string candidatePath in fileSystem.EnumerateFiles(
                sessionsValidation.CanonicalPath!,
                "*.json"))
            {
                if (inspected >= options.MaxMarkerCount)
                {
                    complete = false;
                    reason = "The Claude activity marker count exceeded the bounded scan limit.";
                    break;
                }

                inspected++;
                if (TryReadMarker(
                    candidatePath,
                    trustedRoot,
                    expectedClaudeExecutable.CanonicalPath,
                    out ClaudeActivityMarker? marker))
                {
                    markers.Add(marker!);
                }
                else
                {
                    rejected++;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            complete = false;
            reason = "Claude activity marker enumeration could not be completed.";
        }

        if (string.IsNullOrEmpty(reason))
        {
            reason = rejected == 0
                ? "Claude activity markers were scanned successfully."
                : "Claude activity markers were scanned with rejected entries.";
        }

        return new ClaudeActivityMarkerDiscoveryResult(markers, complete, rejected, reason);
    }

    private bool TryReadMarker(
        string candidatePath,
        string trustedRoot,
        string expectedExecutablePath,
        out ClaudeActivityMarker? marker)
    {
        marker = null;
        string fileName = Path.GetFileName(candidatePath);
        if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(
                Path.GetFileNameWithoutExtension(fileName),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int fileProcessId) ||
            fileProcessId <= 0)
        {
            return false;
        }

        LocalPathValidation validation = pathPolicy.ValidateExistingFile(
            candidatePath,
            fileName,
            trustedRoot: trustedRoot);
        if (!validation.IsSafe)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            using Stream stream = fileSystem.OpenRead(validation.CanonicalPath!);
            bytes = ReadBounded(stream, options.MaxMarkerBytes);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = options.MaxJsonDepth,
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetPositiveProcessId(root, out int processId) ||
                processId != fileProcessId ||
                !TryGetSessionId(root, out Guid sessionId) ||
                !TryGetProcessStart(root, out DateTimeOffset? processStartUtc))
            {
                return false;
            }

            marker = new ClaudeActivityMarker(
                new SessionIdentity(SessionProvider.ClaudeCode, sessionId),
                processId,
                expectedExecutablePath,
                processStartUtc);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] ReadBounded(Stream stream, int maxBytes)
    {
        var bytes = new byte[maxBytes + 1];
        int offset = 0;
        while (offset < bytes.Length)
        {
            int read = stream.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        if (offset > maxBytes)
        {
            throw new InvalidDataException("The activity marker exceeds the bounded size limit.");
        }

        return bytes[..offset];
    }

    private static bool TryGetPositiveProcessId(JsonElement root, out int processId)
    {
        processId = 0;
        return root.TryGetProperty("pid", out JsonElement value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out processId) &&
            processId > 0;
    }

    private static bool TryGetSessionId(JsonElement root, out Guid sessionId)
    {
        sessionId = Guid.Empty;
        return root.TryGetProperty("sessionId", out JsonElement value) &&
            value.ValueKind == JsonValueKind.String &&
            Guid.TryParseExact(value.GetString(), "D", out sessionId) &&
            sessionId != Guid.Empty;
    }

    private static bool TryGetProcessStart(JsonElement root, out DateTimeOffset? processStartUtc)
    {
        processStartUtc = null;
        if (!root.TryGetProperty("procStart", out JsonElement value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed))
        {
            return false;
        }

        processStartUtc = parsed.ToUniversalTime();
        return true;
    }

    private static ClaudeActivityMarkerDiscoveryResult EmptyResult(bool complete, string reason) =>
        new([], complete, 0, reason);
}

public sealed record ProcessSnapshotCaptureResult(
    IReadOnlyList<ProcessSnapshot> Processes,
    bool IsComplete,
    string Reason);

public interface IProcessSnapshotSource
{
    ProcessSnapshotCaptureResult Capture(ResolvedExecutable expectedExecutable);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessSnapshotSource : IProcessSnapshotSource
{
    private readonly int maxInspectedProcesses;
    private readonly int maxMatchingProcesses;
    private readonly IProcessCommandLineReader commandLineReader;

    public WindowsProcessSnapshotSource(
        int maxInspectedProcesses = 4_096,
        int maxMatchingProcesses = 256,
        IProcessCommandLineReader? commandLineReader = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxInspectedProcesses, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxMatchingProcesses, 1);
        this.maxInspectedProcesses = maxInspectedProcesses;
        this.maxMatchingProcesses = maxMatchingProcesses;
        this.commandLineReader = commandLineReader ?? new WindowsProcessCommandLineReader();
    }

    public ProcessSnapshotCaptureResult Capture(ResolvedExecutable expectedExecutable)
    {
        ArgumentNullException.ThrowIfNull(expectedExecutable);
        var snapshots = new List<ProcessSnapshot>();
        bool complete = true;
        int inspected = 0;
        string expectedProcessName = Path.GetFileNameWithoutExtension(
            expectedExecutable.CanonicalPath);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessSnapshotCaptureResult(
                [],
                false,
                "Live process enumeration was unavailable.");
        }

        try
        {
            foreach (Process process in processes)
            {
                if (inspected >= maxInspectedProcesses)
                {
                    complete = false;
                    break;
                }

                inspected++;
                try
                {
                    if (!string.Equals(
                        process.ProcessName,
                        expectedProcessName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string? executablePath = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(executablePath) ||
                        !PathsEqual(executablePath, expectedExecutable.CanonicalPath))
                    {
                        continue;
                    }

                    if (snapshots.Count >= maxMatchingProcesses)
                    {
                        complete = false;
                        break;
                    }

                    ProcessCommandLineReadResult commandLine = commandLineReader.Read(process.Id);
                    if (!commandLine.IsComplete)
                    {
                        complete = false;
                    }

                    snapshots.Add(new ProcessSnapshot(
                        process.Id,
                        executablePath,
                        process.StartTime.ToUniversalTime(),
                        commandLine.ResumeArguments));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    NotSupportedException or
                    System.ComponentModel.Win32Exception)
                {
                    complete = false;
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        return new ProcessSnapshotCaptureResult(
            snapshots,
            complete,
            complete
                ? "Expected provider processes were captured successfully."
                : "Live process capture was incomplete.");
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

public sealed class ClaudeLiveActivitySnapshot(
    IReadOnlyList<ClaudeActivityMarker> markers,
    IReadOnlyList<ProcessSnapshot> processes,
    string expectedExecutablePath,
    bool isComplete,
    string reason)
{
    public bool IsComplete { get; } = isComplete;

    public string Reason { get; } = reason;

    public bool HasUnmappedClaudeActivity => processes.Any(process =>
        PathsEqual(process.ExecutablePath, expectedExecutablePath) &&
        !HasResumeIdentity(process.Arguments) &&
        !markers.Any(marker => MarkerMapsProcess(marker, process)));

    public ClaudeActiveSessionResult Detect(SessionIdentity session)
    {
        if (session.Provider != SessionProvider.ClaudeCode)
        {
            throw new ArgumentException("The session must be a Claude Code session.", nameof(session));
        }

        ClaudeActiveSessionResult? possible = null;
        foreach (ClaudeActivityMarker marker in markers)
        {
            if (marker.Session != session)
            {
                continue;
            }

            ClaudeActiveSessionResult result = ClaudeActiveSessionDetector.Detect(
                session,
                marker.ExpectedExecutablePath,
                marker,
                processes);
            if (result.State == ActiveSessionState.Active)
            {
                return result;
            }

            if (result.State == ActiveSessionState.PossiblyActive)
            {
                possible = result;
            }
        }

        return possible ?? ClaudeActiveSessionDetector.Detect(
            session,
            expectedExecutablePath,
            marker: null,
            processes);
    }

    private static bool MarkerMapsProcess(
        ClaudeActivityMarker marker,
        ProcessSnapshot process) =>
        marker.ProcessId == process.ProcessId &&
        PathsEqual(marker.ExpectedExecutablePath, process.ExecutablePath) &&
        (!marker.ProcessStartUtc.HasValue || marker.ProcessStartUtc == process.StartTimeUtc);

    private static bool HasResumeIdentity(IReadOnlyList<string> arguments)
    {
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--resume", StringComparison.Ordinal) &&
                index + 1 < arguments.Count &&
                Guid.TryParseExact(arguments[index + 1], "D", out _))
            {
                return true;
            }

            if (argument.StartsWith("--resume=", StringComparison.Ordinal) &&
                Guid.TryParseExact(argument[9..], "D", out _))
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

public sealed class ClaudeLiveActivityScanner(
    ClaudeActivityMarkerDiscovery markerDiscovery,
    IProcessSnapshotSource processSnapshotSource)
{
    public ClaudeLiveActivitySnapshot Scan(
        string claudeRoot,
        ResolvedExecutable expectedClaudeExecutable)
    {
        ClaudeActivityMarkerDiscoveryResult markerResult = markerDiscovery.Discover(
            claudeRoot,
            expectedClaudeExecutable);
        ProcessSnapshotCaptureResult processResult = processSnapshotSource.Capture(
            expectedClaudeExecutable);

        return new ClaudeLiveActivitySnapshot(
            markerResult.Markers,
            processResult.Processes,
            expectedClaudeExecutable.CanonicalPath,
            markerResult.IsComplete && processResult.IsComplete,
            markerResult.IsComplete && processResult.IsComplete
                ? "Claude live activity was scanned successfully."
                : "Claude live activity was scanned incompletely.");
    }
}
