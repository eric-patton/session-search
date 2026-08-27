using System.Text.Json;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;

namespace SessionSearch.Infrastructure.Claude;

public sealed class ClaudeSessionProviderAdapter : ISessionProviderAdapter
{
    public const int CurrentParserVersion = 2;

    private const long PreferredMetadataScanBytes = 64 * 1024;
    private const long MaximumMetadataScanBytes = 4 * 1024 * 1024;
    private const int MaximumMetadataRecords = 512;

    private static readonly EnumerationOptions TopLevelEnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private static readonly EnumerationOptions ChildEnumerationOptions = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        RecurseSubdirectories = true,
        ReturnSpecialDirectories = false,
    };

    public SessionProvider Provider => SessionProvider.ClaudeCode;

    public static string GetConfiguredRootPath()
    {
        string? configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configured;
    }

    public async ValueTask<ProviderDiscoveryResult> DiscoverAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sessions = new List<ProviderSessionSeed>();
        var diagnostics = new List<ProviderDiagnostic>();

        if (!ClaudePathPolicy.TryResolveProjectsRoot(rootPath, out string projectsRoot))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Error,
                "claude.root-rejected",
                "claude-root",
                "The configured Claude root is unavailable or did not pass the local fixed-drive path policy."));
            return new ProviderDiscoveryResult(sessions, diagnostics, true);
        }

        string[] projectDirectories;
        try
        {
            projectDirectories = Directory.GetDirectories(
                projectsRoot,
                "*",
                TopLevelEnumerationOptions);
        }
        catch (Exception exception) when (IsSourceException(exception))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Error,
                "claude.root-enumeration-failed",
                "claude-root",
                "The Claude projects directory could not be enumerated."));
            return new ProviderDiscoveryResult(sessions, diagnostics, true);
        }

        Array.Sort(projectDirectories, StringComparer.OrdinalIgnoreCase);
        bool isPartial = false;
        int candidateCount = 0;

        foreach (string projectDirectory in projectDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] topLevelFiles;
            try
            {
                topLevelFiles = Directory.GetFiles(
                    projectDirectory,
                    "*.jsonl",
                    TopLevelEnumerationOptions);
            }
            catch (Exception exception) when (IsSourceException(exception))
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "claude.project-enumeration-failed",
                    GetAlias(projectsRoot, projectDirectory),
                    "An encoded Claude project directory could not be enumerated."));
                isPartial = true;
                continue;
            }

            Array.Sort(topLevelFiles, StringComparer.OrdinalIgnoreCase);
            foreach (string topLevelFile in topLevelFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(topLevelFile), "D", out Guid sessionId))
                {
                    continue;
                }

                if (candidateCount == ProviderLimits.MaxCandidatesPerProvider)
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "claude.candidate-limit",
                        "claude-root",
                        "The Claude candidate limit was reached, so this discovery pass is partial."));
                    isPartial = true;
                    return new ProviderDiscoveryResult(sessions, diagnostics, isPartial);
                }

                candidateCount++;
                TopLevelScanOutcome? topLevel = await ScanTopLevelAsync(
                    projectsRoot,
                    topLevelFile,
                    sessionId,
                    diagnostics,
                    cancellationToken);
                if (topLevel is null)
                {
                    isPartial = true;
                    continue;
                }

                isPartial |= topLevel.IsPartial;
                if (topLevel.IdentityMismatch || !topLevel.HasMatchingSessionId)
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "claude.identity-mismatch",
                        topLevel.Source.RelativePath,
                        "The filename identity does not match the JSONL session identity."));
                    isPartial = true;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(topLevel.Directory))
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "claude.missing-directory",
                        topLevel.Source.RelativePath,
                        "The candidate has no recorded working directory and cannot be trusted as resumable."));
                    continue;
                }

                var sources = new List<ProviderSource> { topLevel.Source };
                DateTimeOffset? lastChildTimestamp = null;
                ChildDiscoveryOutcome childDiscovery = await DiscoverChildrenAsync(
                    projectsRoot,
                    projectDirectory,
                    new SessionIdentity(Provider, sessionId),
                    candidateCount,
                    diagnostics,
                    cancellationToken);
                sources.AddRange(childDiscovery.Sources);
                candidateCount = childDiscovery.CandidateCount;
                isPartial |= childDiscovery.IsPartial;
                lastChildTimestamp = childDiscovery.LastTimestampUtc;

                bool formatSupported = topLevel.HasSubstantiveTopLevel;
                if (!formatSupported)
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Information,
                        "claude.unsupported-stub",
                        topLevel.Source.RelativePath,
                        "The trusted session is a metadata-only or control-only stub and is not resumable."));
                }

                DateTimeOffset? topLevelActivity = topLevel.LastSubstantiveTimestampUtc
                    ?? topLevel.LastTimestampUtc;
                if (topLevel.StoppedEarly)
                {
                    topLevelActivity = MaxTimestamp(
                        topLevelActivity,
                        topLevel.Source.LastWriteUtc);
                }

                DateTimeOffset lastActivity = MaxTimestamp(
                        topLevelActivity,
                        lastChildTimestamp)
                    ?? topLevel.Source.LastWriteUtc;
                DateTimeOffset? created = topLevel.FirstSubstantiveTimestampUtc
                    ?? topLevel.FirstTimestampUtc;

                sessions.Add(new ProviderSessionSeed(
                    new SessionIdentity(Provider, sessionId),
                    topLevel.Directory,
                    topLevel.Branch,
                    topLevel.Model,
                    created,
                    lastActivity,
                    Archived: false,
                    formatSupported,
                    sources));

                if (candidateCount >= ProviderLimits.MaxCandidatesPerProvider)
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "claude.candidate-limit",
                        "claude-root",
                        "The Claude candidate limit was reached, so this discovery pass is partial."));
                    isPartial = true;
                    return new ProviderDiscoveryResult(sessions, diagnostics, isPartial);
                }
            }
        }

        sessions.Sort(static (left, right) =>
        {
            int activity = right.LastActivityUtc.CompareTo(left.LastActivityUtc);
            return activity != 0
                ? activity
                : left.Identity.SessionId.CompareTo(right.Identity.SessionId);
        });
        return new ProviderDiscoveryResult(sessions, diagnostics, isPartial);
    }

    public async ValueTask<ProviderReadResult> ReadAsync(
        ProviderSource source,
        long startOffset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        cancellationToken.ThrowIfCancellationRequested();

        var records = new List<ProviderRecord>();
        var diagnostics = new List<ProviderDiagnostic>();
        string sourceAlias = GetSafeAlias(source.RelativePath);

        if (source.Owner.Provider != Provider
            || source.ParserVersion != CurrentParserVersion
            || !ClaudePathPolicy.TryRevalidateSource(
                source.CanonicalPath,
                source.RelativePath,
                out string validatedPath))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Error,
                "claude.source-rejected",
                sourceAlias,
                "The source identity, parser version, or local path validation failed."));
            return new ProviderReadResult(records, diagnostics, startOffset, true);
        }

        long snapshotLength;
        try
        {
            snapshotLength = new FileInfo(validatedPath).Length;
        }
        catch (Exception exception) when (IsSourceException(exception))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.source-unavailable",
                sourceAlias,
                "The source could not be opened for this read pass."));
            return new ProviderReadResult(records, diagnostics, startOffset, true);
        }

        if (startOffset > snapshotLength)
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.source-truncated",
                sourceAlias,
                "The saved offset is beyond the current source length and requires reconciliation."));
            return new ProviderReadResult(records, diagnostics, startOffset, true);
        }

        bool isPartial = false;
        try
        {
            ClaudeJsonlReadOutcome outcome = await ClaudeJsonlReader.ReadAsync(
                validatedPath,
                startOffset,
                snapshotLength,
                (lineOffset, utf8Json, isOversized) =>
                {
                    if (isOversized)
                    {
                        diagnostics.Add(Diagnostic(
                            ProviderDiagnosticSeverity.Warning,
                            "claude.record-oversized",
                            sourceAlias,
                            "A complete JSONL record exceeded the parser byte limit and was skipped."));
                        isPartial = true;
                        return;
                    }

                    if (utf8Json.IsEmpty)
                    {
                        return;
                    }

                    try
                    {
                        using JsonDocument document = ClaudeRecordParser.Parse(utf8Json);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            diagnostics.Add(Diagnostic(
                                ProviderDiagnosticSeverity.Warning,
                                "claude.record-unknown",
                                sourceAlias,
                                "A complete JSONL record has an unsupported root shape and was skipped."));
                            isPartial = true;
                            return;
                        }

                        if (!ClaudeRecordParser.IsKnownRecordType(document.RootElement))
                        {
                            diagnostics.Add(Diagnostic(
                                ProviderDiagnosticSeverity.Warning,
                                "claude.record-unknown",
                                sourceAlias,
                                $"A complete JSONL record type is unknown to Claude parser version {CurrentParserVersion} and was skipped."));
                            isPartial = true;
                            return;
                        }

                        (ClaudeSessionIdState state, Guid recordSessionId) =
                            ClaudeRecordParser.ReadSessionId(document.RootElement);
                        if (state == ClaudeSessionIdState.Invalid
                            || (state == ClaudeSessionIdState.Valid
                                && recordSessionId != source.Owner.SessionId))
                        {
                            diagnostics.Add(Diagnostic(
                                ProviderDiagnosticSeverity.Warning,
                                "claude.record-identity-mismatch",
                                sourceAlias,
                                "A JSONL record has a conflicting session identity and was skipped."));
                            isPartial = true;
                            return;
                        }

                        if (!ClaudeRecordParser.TryAppendRecords(
                            document.RootElement,
                            source,
                            lineOffset,
                            records))
                        {
                            diagnostics.Add(Diagnostic(
                                ProviderDiagnosticSeverity.Warning,
                                "claude.extracted-text-limit",
                                sourceAlias,
                                "A record exceeded the extracted text byte limit and was skipped."));
                            isPartial = true;
                        }
                    }
                    catch (JsonException)
                    {
                        diagnostics.Add(Diagnostic(
                            ProviderDiagnosticSeverity.Warning,
                            "claude.record-malformed",
                            sourceAlias,
                            $"A complete JSONL record could not be parsed by Claude parser version {CurrentParserVersion}."));
                        isPartial = true;
                    }
                },
                cancellationToken);

            if (!outcome.ReachedSnapshotLength && !outcome.StoppedEarly)
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "claude.source-changed",
                    sourceAlias,
                    "The source changed during the bounded read pass and will be retried."));
                isPartial = true;
            }

            if (outcome.HasPartialTail)
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Information,
                    "claude.partial-tail",
                    sourceAlias,
                    "An incomplete trailing JSONL record was deferred for the next read pass."));
            }

            isPartial |= outcome.HasPartialTail;
            return new ProviderReadResult(
                records,
                diagnostics,
                outcome.LastCompleteOffset,
                isPartial);
        }
        catch (Exception exception) when (IsSourceException(exception))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.source-read-failed",
                sourceAlias,
                "The source read failed without changing provider-owned data."));
            return new ProviderReadResult(records, diagnostics, startOffset, true);
        }
    }

    private async ValueTask<TopLevelScanOutcome?> ScanTopLevelAsync(
        string projectsRoot,
        string path,
        Guid expectedSessionId,
        List<ProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!ClaudePathPolicy.TryValidateSource(projectsRoot, path, out string canonicalPath))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.source-path-rejected",
                GetAlias(projectsRoot, path),
                "A Claude source did not pass final local path containment validation."));
            return null;
        }

        string alias = GetAlias(projectsRoot, canonicalPath);
        FileInfo info;
        try
        {
            info = new FileInfo(canonicalPath);
            info.Refresh();
        }
        catch (Exception exception) when (IsSourceException(exception))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.source-unavailable",
                alias,
                "A Claude source could not be opened for metadata discovery."));
            return null;
        }

        var state = new TopLevelState(expectedSessionId);
        bool isPartial = false;
        int metadataRecordCount = 0;
        bool stoppedEarly = false;
        try
        {
            ClaudeJsonlReadOutcome read = await ClaudeJsonlReader.ReadAsync(
                canonicalPath,
                0,
                info.Length,
                (lineOffset, utf8Json, isOversized) =>
                {
                    _ = lineOffset;
                    if (isOversized || !utf8Json.IsEmpty)
                    {
                        metadataRecordCount++;
                    }

                    if (isOversized)
                    {
                        diagnostics.Add(Diagnostic(
                            ProviderDiagnosticSeverity.Warning,
                            "claude.record-oversized",
                            alias,
                            "A metadata record exceeded the parser byte limit and was skipped."));
                        isPartial = true;
                        return;
                    }

                    if (utf8Json.IsEmpty)
                    {
                        return;
                    }

                    try
                    {
                        using JsonDocument document = ClaudeRecordParser.Parse(utf8Json);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            diagnostics.Add(Diagnostic(
                                ProviderDiagnosticSeverity.Warning,
                                "claude.record-unknown",
                                alias,
                                "A complete metadata record has an unsupported root shape."));
                            isPartial = true;
                            return;
                        }

                        if (!ClaudeRecordParser.IsKnownRecordType(document.RootElement))
                        {
                            diagnostics.Add(Diagnostic(
                                ProviderDiagnosticSeverity.Warning,
                                "claude.record-unknown",
                                alias,
                                $"A metadata record type is unknown to Claude parser version {CurrentParserVersion}."));
                            isPartial = true;
                        }

                        state.Observe(ClaudeRecordParser.Inspect(document.RootElement));
                    }
                    catch (JsonException)
                    {
                        diagnostics.Add(Diagnostic(
                            ProviderDiagnosticSeverity.Warning,
                            "claude.record-malformed",
                            alias,
                            $"A complete metadata record could not be parsed by Claude parser version {CurrentParserVersion}."));
                        isPartial = true;
                    }
                },
                cancellationToken,
                completeOffset => info.Length > PreferredMetadataScanBytes
                    && ((state.CanStopMetadataScan
                            && completeOffset >= PreferredMetadataScanBytes)
                        || completeOffset >= MaximumMetadataScanBytes
                        || metadataRecordCount >= MaximumMetadataRecords));
            stoppedEarly = read.StoppedEarly;
            isPartial |= read.HasPartialTail
                || (!read.ReachedSnapshotLength && !read.StoppedEarly);
            if (read.HasPartialTail)
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Information,
                    "claude.partial-tail",
                    alias,
                    "An incomplete trailing metadata record was deferred for the next discovery pass."));
            }
        }
        catch (Exception exception) when (IsSourceException(exception))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.source-read-failed",
                alias,
                "A Claude source could not be read for metadata discovery."));
            return null;
        }

        var source = new ProviderSource(
            new SessionIdentity(Provider, expectedSessionId),
            canonicalPath,
            alias,
            ProviderSourceKind.TopLevel,
            ChildSessionId: null,
            Archived: false,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc),
            CurrentParserVersion);

        return new TopLevelScanOutcome(
            source,
            state.HasMatchingSessionId,
            state.IdentityMismatch,
            state.HasSubstantiveTopLevel,
            state.Directory,
            state.Branch,
            state.Model,
            state.FirstTimestampUtc,
            state.LastTimestampUtc,
            state.FirstSubstantiveTimestampUtc,
            state.LastSubstantiveTimestampUtc,
            isPartial,
            stoppedEarly);
    }

    private static async ValueTask<ChildDiscoveryOutcome> DiscoverChildrenAsync(
        string projectsRoot,
        string projectDirectory,
        SessionIdentity owner,
        int initialCandidateCount,
        List<ProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        int candidateCount = initialCandidateCount;
        var sources = new List<ProviderSource>();
        string subagentsRoot = Path.Combine(
            projectDirectory,
            owner.SessionId.ToString("D"),
            "subagents");
        if (!ClaudePathPolicy.TryValidateDirectory(projectsRoot, subagentsRoot))
        {
            return new ChildDiscoveryOutcome(sources, candidateCount, null, false);
        }

        string[] childFiles;
        try
        {
            childFiles = Directory.GetFiles(
                subagentsRoot,
                "agent-*.jsonl",
                ChildEnumerationOptions);
        }
        catch (Exception exception) when (IsSourceException(exception))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.child-enumeration-failed",
                GetAlias(projectsRoot, subagentsRoot),
                "The Claude child source tree could not be enumerated."));
            return new ChildDiscoveryOutcome(sources, candidateCount, null, true);
        }

        Array.Sort(childFiles, StringComparer.OrdinalIgnoreCase);
        bool isPartial = false;
        DateTimeOffset? lastTimestamp = null;
        foreach (string childFile in childFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidateCount == ProviderLimits.MaxCandidatesPerProvider)
            {
                return new ChildDiscoveryOutcome(sources, candidateCount, lastTimestamp, true);
            }

            candidateCount++;
            ChildScanOutcome? child = await ScanChildAsync(
                projectsRoot,
                childFile,
                owner,
                diagnostics,
                cancellationToken);
            if (child is null)
            {
                isPartial = true;
                continue;
            }

            isPartial |= child.IsPartial;
            if (child.IdentityMismatch)
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "claude.child-identity-mismatch",
                    child.Source.RelativePath,
                    "The child source has a session identity that conflicts with its owner."));
                isPartial = true;
                continue;
            }

            if (!child.HasSubstantiveMessage)
            {
                continue;
            }

            sources.Add(child.Source);
            lastTimestamp = MaxTimestamp(lastTimestamp, child.LastTimestampUtc);
        }

        return new ChildDiscoveryOutcome(
            sources,
            candidateCount,
            lastTimestamp,
            isPartial);
    }

    private static async ValueTask<ChildScanOutcome?> ScanChildAsync(
        string projectsRoot,
        string path,
        SessionIdentity owner,
        List<ProviderDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        if (!ClaudePathPolicy.TryValidateSource(projectsRoot, path, out string canonicalPath))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.child-path-rejected",
                GetAlias(projectsRoot, path),
                "A Claude child source did not pass final local path containment validation."));
            return null;
        }

        string alias = GetAlias(projectsRoot, canonicalPath);
        FileInfo info;
        try
        {
            info = new FileInfo(canonicalPath);
            info.Refresh();
        }
        catch (Exception exception) when (IsSourceException(exception))
        {
            return null;
        }

        bool identityMismatch = false;
        bool hasSubstantive = false;
        bool isPartial = false;
        int metadataRecordCount = 0;
        DateTimeOffset? lastTimestamp = null;
        try
        {
            ClaudeJsonlReadOutcome read = await ClaudeJsonlReader.ReadAsync(
                canonicalPath,
                0,
                info.Length,
                (lineOffset, utf8Json, isOversized) =>
                {
                    _ = lineOffset;
                    if (isOversized || !utf8Json.IsEmpty)
                    {
                        metadataRecordCount++;
                    }

                    if (isOversized)
                    {
                        diagnostics.Add(Diagnostic(
                            ProviderDiagnosticSeverity.Warning,
                            "claude.record-oversized",
                            alias,
                            "A child metadata record exceeded the parser byte limit and was skipped."));
                        isPartial = true;
                        return;
                    }

                    if (utf8Json.IsEmpty)
                    {
                        return;
                    }

                    try
                    {
                        using JsonDocument document = ClaudeRecordParser.Parse(utf8Json);
                        if (document.RootElement.ValueKind != JsonValueKind.Object)
                        {
                            diagnostics.Add(Diagnostic(
                                ProviderDiagnosticSeverity.Warning,
                                "claude.record-unknown",
                                alias,
                                "A complete child metadata record has an unsupported root shape."));
                            isPartial = true;
                            return;
                        }

                        if (!ClaudeRecordParser.IsKnownRecordType(document.RootElement))
                        {
                            diagnostics.Add(Diagnostic(
                                ProviderDiagnosticSeverity.Warning,
                                "claude.record-unknown",
                                alias,
                                $"A child metadata record type is unknown to Claude parser version {CurrentParserVersion}."));
                            isPartial = true;
                        }

                        ClaudeRecordInspection inspection = ClaudeRecordParser.Inspect(document.RootElement);
                        if (inspection.SessionIdState == ClaudeSessionIdState.Invalid
                            || (inspection.SessionIdState == ClaudeSessionIdState.Valid
                                && inspection.SessionId != owner.SessionId))
                        {
                            identityMismatch = true;
                        }

                        hasSubstantive |= inspection.IsSubstantiveMessage;
                        if (inspection.IsSubstantiveMessage)
                        {
                            lastTimestamp = MaxTimestamp(lastTimestamp, inspection.TimestampUtc);
                        }
                    }
                    catch (JsonException)
                    {
                        diagnostics.Add(Diagnostic(
                            ProviderDiagnosticSeverity.Warning,
                            "claude.record-malformed",
                            alias,
                            $"A complete child metadata record could not be parsed by Claude parser version {CurrentParserVersion}."));
                        isPartial = true;
                    }
                },
                cancellationToken,
                completeOffset => info.Length > PreferredMetadataScanBytes
                    && ((hasSubstantive
                            && !identityMismatch
                            && completeOffset >= PreferredMetadataScanBytes)
                        || completeOffset >= MaximumMetadataScanBytes
                        || metadataRecordCount >= MaximumMetadataRecords));
            isPartial |= read.HasPartialTail
                || (!read.ReachedSnapshotLength && !read.StoppedEarly);
            if (read.StoppedEarly)
            {
                lastTimestamp = MaxTimestamp(
                    lastTimestamp,
                    new DateTimeOffset(info.LastWriteTimeUtc));
            }
            if (read.HasPartialTail)
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Information,
                    "claude.partial-tail",
                    alias,
                    "An incomplete trailing child record was deferred for the next discovery pass."));
            }
        }
        catch (Exception exception) when (IsSourceException(exception))
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "claude.child-read-failed",
                alias,
                "A Claude child source could not be read for discovery."));
            return null;
        }

        var source = new ProviderSource(
            owner,
            canonicalPath,
            alias,
            ProviderSourceKind.Child,
            ChildSessionId: null,
            Archived: false,
            info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc),
            CurrentParserVersion);
        return new ChildScanOutcome(
            source,
            identityMismatch,
            hasSubstantive,
            lastTimestamp,
            isPartial);
    }

    private static ProviderDiagnostic Diagnostic(
        ProviderDiagnosticSeverity severity,
        string code,
        string sourceAlias,
        string message) =>
        new(
            SessionProvider.ClaudeCode,
            severity,
            code,
            GetSafeAlias(sourceAlias),
            message,
            ParserVersion: CurrentParserVersion);

    private static string GetAlias(string projectsRoot, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(projectsRoot, path);
            return relative.StartsWith("..", StringComparison.Ordinal)
                || Path.IsPathRooted(relative)
                    ? "claude-source"
                    : GetSafeAlias(relative);
        }
        catch (ArgumentException)
        {
            return "claude-source";
        }
    }

    private static string GetSafeAlias(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment == ".."))
        {
            return value == "claude-root" ? value : "claude-source";
        }

        return value.Length <= 512 ? value : value[^512..];
    }

    private static bool IsSourceException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException;

    private static DateTimeOffset? MaxTimestamp(
        DateTimeOffset? left,
        DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left >= right ? left : right;
    }

    private sealed class TopLevelState(Guid expectedSessionId)
    {
        public bool HasMatchingSessionId { get; private set; }

        public bool IdentityMismatch { get; private set; }

        public bool HasSubstantiveTopLevel { get; private set; }

        public string? Directory { get; private set; }

        public string? Branch { get; private set; }

        public string? Model { get; private set; }

        public DateTimeOffset? FirstTimestampUtc { get; private set; }

        public DateTimeOffset? LastTimestampUtc { get; private set; }

        public DateTimeOffset? FirstSubstantiveTimestampUtc { get; private set; }

        public DateTimeOffset? LastSubstantiveTimestampUtc { get; private set; }

        public bool CanStopMetadataScan =>
            HasMatchingSessionId
            && HasSubstantiveTopLevel
            && !string.IsNullOrWhiteSpace(Directory)
            && !string.IsNullOrWhiteSpace(Model);

        public void Observe(ClaudeRecordInspection inspection)
        {
            if (inspection.SessionIdState == ClaudeSessionIdState.Invalid)
            {
                IdentityMismatch = true;
            }
            else if (inspection.SessionIdState == ClaudeSessionIdState.Valid)
            {
                if (inspection.SessionId == expectedSessionId)
                {
                    HasMatchingSessionId = true;
                }
                else
                {
                    IdentityMismatch = true;
                }
            }

            HasSubstantiveTopLevel |= inspection.IsSubstantiveTopLevel;
            Directory = LatestUsable(Directory, inspection.Directory);
            Branch = LatestUsable(Branch, inspection.Branch);
            Model = LatestUsable(Model, inspection.Model);
            FirstTimestampUtc = MinTimestamp(FirstTimestampUtc, inspection.TimestampUtc);
            LastTimestampUtc = MaxTimestamp(LastTimestampUtc, inspection.TimestampUtc);
            if (inspection.IsSubstantiveTopLevel)
            {
                FirstSubstantiveTimestampUtc = MinTimestamp(
                    FirstSubstantiveTimestampUtc,
                    inspection.TimestampUtc);
                LastSubstantiveTimestampUtc = MaxTimestamp(
                    LastSubstantiveTimestampUtc,
                    inspection.TimestampUtc);
            }
        }

        private static string? LatestUsable(string? current, string? candidate) =>
            string.IsNullOrWhiteSpace(candidate) ? current : candidate;

        private static DateTimeOffset? MinTimestamp(
            DateTimeOffset? left,
            DateTimeOffset? right)
        {
            if (left is null)
            {
                return right;
            }

            if (right is null)
            {
                return left;
            }

            return left <= right ? left : right;
        }
    }

    private sealed record TopLevelScanOutcome(
        ProviderSource Source,
        bool HasMatchingSessionId,
        bool IdentityMismatch,
        bool HasSubstantiveTopLevel,
        string? Directory,
        string? Branch,
        string? Model,
        DateTimeOffset? FirstTimestampUtc,
        DateTimeOffset? LastTimestampUtc,
        DateTimeOffset? FirstSubstantiveTimestampUtc,
        DateTimeOffset? LastSubstantiveTimestampUtc,
        bool IsPartial,
        bool StoppedEarly);

    private sealed record ChildScanOutcome(
        ProviderSource Source,
        bool IdentityMismatch,
        bool HasSubstantiveMessage,
        DateTimeOffset? LastTimestampUtc,
        bool IsPartial);

    private sealed record ChildDiscoveryOutcome(
        IReadOnlyList<ProviderSource> Sources,
        int CandidateCount,
        DateTimeOffset? LastTimestampUtc,
        bool IsPartial);
}
