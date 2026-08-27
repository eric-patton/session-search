using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Text;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.Infrastructure.Codex;

public sealed class CodexProviderAdapter : ISessionProviderAdapter
{
    private const int ParserVersion = 1;
    private const long PreferredMetadataScanBytes = 64 * 1024;
    private const long MaximumMetadataScanBytes = 4 * 1024 * 1024;
    private const int MaximumMetadataRecords = 512;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = ProviderLimits.MaxJsonDepth,
    };

    private readonly ConcurrentDictionary<string, string> threadNamesBySource =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly LocalPathPolicy pathPolicy;
    private readonly CodexStateDatabaseReader stateDatabaseReader;

    public CodexProviderAdapter()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Codex session discovery requires Windows.");
        }

        pathPolicy = new LocalPathPolicy(new PhysicalWindowsPathProbe());
        stateDatabaseReader = new CodexStateDatabaseReader(pathPolicy);
    }

    public CodexProviderAdapter(LocalPathPolicy pathPolicy)
    {
        this.pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
        stateDatabaseReader = new CodexStateDatabaseReader(this.pathPolicy);
    }

    public SessionProvider Provider => SessionProvider.Codex;

    public async ValueTask<ProviderDiscoveryResult> DiscoverAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        cancellationToken.ThrowIfCancellationRequested();

        LocalPathValidation rootValidation = pathPolicy.ValidateExistingDirectory(rootPath);
        if (!rootValidation.IsSafe)
        {
            return new ProviderDiscoveryResult(
                [],
                [Diagnostic(
                    ProviderDiagnosticSeverity.Error,
                    "codex.discovery.root-unavailable",
                    "codex-root",
                    "The configured Codex root is unavailable.")],
                IsPartial: true);
        }

        string canonicalRoot = rootValidation.CanonicalPath!;

        List<ProviderDiagnostic> diagnostics = [];
        List<RolloutCandidate> candidates = [];
        bool isPartial = false;

        foreach ((string directoryName, bool archived) in DiscoveryDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string discoveryRoot = Path.Combine(canonicalRoot, directoryName);
            LocalPathValidation discoveryValidation = pathPolicy.ValidateExistingDirectory(
                discoveryRoot,
                canonicalRoot);
            if (discoveryValidation.Failure == LocalPathFailure.Missing)
            {
                continue;
            }

            if (!discoveryValidation.IsSafe)
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "codex.discovery.directory-unsafe",
                    directoryName,
                    "A Codex rollout directory is unsafe."));
                isPartial = true;
                continue;
            }

            IReadOnlyList<string> paths;
            try
            {
                paths = EnumerateRollouts(
                    discoveryValidation.CanonicalPath!,
                    cancellationToken);
            }
            catch (Exception exception) when (IsRecoverableIo(exception))
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Error,
                    "codex.discovery.enumeration-failed",
                    directoryName,
                    "A Codex rollout directory could not be enumerated."));
                isPartial = true;
                continue;
            }

            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (candidates.Count >= ProviderLimits.MaxCandidatesPerProvider)
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "codex.discovery.candidate-limit",
                        directoryName,
                        "The Codex candidate limit was reached."));
                    isPartial = true;
                    break;
                }

                LocalPathValidation sourceValidation = pathPolicy.ValidateExistingFile(
                    path,
                    Path.GetFileName(path),
                    trustedRoot: canonicalRoot);
                if (!sourceValidation.IsSafe)
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "codex.discovery.source-unsafe",
                        directoryName,
                        "A Codex rollout source is unsafe."));
                    isPartial = true;
                    continue;
                }

                string canonicalPath = sourceValidation.CanonicalPath!;
                string relativePath = NormalizeRelativePath(canonicalRoot, canonicalPath);
                if (!TryParseRolloutId(canonicalPath, out Guid sessionId))
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "codex.discovery.invalid-filename",
                        relativePath,
                        "A rollout filename does not contain a trusted session identifier."));
                    isPartial = true;
                    continue;
                }

                CandidateReadResult candidateResult;
                try
                {
                    candidateResult = await ReadCandidateAsync(
                        canonicalPath,
                        relativePath,
                        sessionId,
                        archived,
                        cancellationToken);
                }
                catch (Exception exception) when (IsRecoverableIo(exception))
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "codex.discovery.read-failed",
                        relativePath,
                        "A Codex rollout could not be read."));
                    isPartial = true;
                    continue;
                }

                if (candidateResult.Candidate is not null)
                {
                    candidates.Add(candidateResult.Candidate);
                }
                diagnostics.AddRange(candidateResult.Diagnostics);
                isPartial |= candidateResult.IsPartial;
            }
        }

        SessionIndexReadResult sessionIndex = await ReadSessionIndexAsync(
            canonicalRoot,
            cancellationToken);
        diagnostics.AddRange(sessionIndex.Diagnostics);
        isPartial |= sessionIndex.IsPartial;

        Dictionary<Guid, RolloutCandidate> candidatesById = candidates
            .GroupBy(candidate => candidate.SessionId)
            .ToDictionary(group => group.Key, ChoosePrimaryCandidate);
        CodexStateDatabaseReadResult stateDatabase = await stateDatabaseReader.ReadAsync(
            canonicalRoot,
            candidatesById.Keys.ToArray(),
            cancellationToken);
        diagnostics.AddRange(stateDatabase.Diagnostics);
        isPartial |= stateDatabase.IsPartial;

        Dictionary<Guid, Guid> rootOwnerByChild = [];
        foreach (RolloutCandidate child in candidatesById.Values
                     .Where(candidate => candidate.IsChild)
                     .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid? resolvedRoot = ResolveRootOwner(
                child,
                candidatesById,
                diagnostics,
                ref isPartial);
            if (resolvedRoot is Guid rootId)
            {
                rootOwnerByChild[child.SessionId] = rootId;
            }
        }

        Dictionary<Guid, List<RolloutCandidate>> childrenByRoot = [];
        foreach ((Guid childId, Guid rootId) in rootOwnerByChild)
        {
            if (!candidatesById.TryGetValue(childId, out RolloutCandidate? child))
            {
                continue;
            }

            if (!childrenByRoot.TryGetValue(rootId, out List<RolloutCandidate>? children))
            {
                children = [];
                childrenByRoot.Add(rootId, children);
            }

            children.Add(child);
        }

        List<ProviderSessionSeed> sessions = [];
        foreach (RolloutCandidate root in candidatesById.Values
                     .Where(candidate => !candidate.IsChild)
                     .OrderByDescending(candidate => candidate.LastActivityUtc)
                     .ThenBy(candidate => candidate.SessionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stateDatabase.Threads.TryGetValue(
                root.SessionId,
                out CodexStateEnrichment? stateEnrichment);
            SessionIdentity owner = new(SessionProvider.Codex, root.SessionId);
            List<(ProviderSource Source, RolloutCandidate Candidate)> ownedSources =
            [
                (CreateSource(root, owner, ProviderSourceKind.TopLevel), root),
            ];

            foreach (RolloutCandidate child in childrenByRoot
                         .GetValueOrDefault(root.SessionId, [])
                         .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal))
            {
                ownedSources.Add((
                    CreateSource(child, owner, ProviderSourceKind.Child),
                    child));
            }

            ProviderSource[] sources = ownedSources
                .OrderBy(item => item.Source.Kind)
                .ThenBy(item => item.Source.RelativePath, StringComparer.Ordinal)
                .Select(item => item.Source)
                .ToArray();
            DateTimeOffset lastActivity = ownedSources.Max(item => item.Candidate.LastActivityUtc);

            sessions.Add(new ProviderSessionSeed(
                owner,
                root.Directory,
                root.Branch ?? stateEnrichment?.Branch,
                root.Model ?? stateEnrichment?.Model,
                root.CreatedUtc,
                lastActivity,
                root.Archived,
                root.FormatSupported,
                sources));

            string sourceKey = Path.GetFullPath(root.CanonicalPath);
            if (sessionIndex.ThreadNames.TryGetValue(root.SessionId, out string? threadName))
            {
                threadNamesBySource[sourceKey] = threadName;
            }
            else if ((stateEnrichment?.Name ?? stateEnrichment?.Title) is { } stateTitle)
            {
                threadNamesBySource[sourceKey] = stateTitle;
            }
            else
            {
                threadNamesBySource.TryRemove(sourceKey, out _);
            }
        }

        return new ProviderDiscoveryResult(
            sessions,
            diagnostics,
            isPartial);
    }

    public async ValueTask<ProviderReadResult> ReadAsync(
        ProviderSource source,
        long startOffset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Owner.Provider != SessionProvider.Codex)
        {
            throw new ArgumentException("The source owner is not a Codex session.", nameof(source));
        }

        LocalPathValidation sourceValidation = pathPolicy.ValidateExistingFile(
            source.CanonicalPath,
            Path.GetFileName(source.CanonicalPath));
        if (!sourceValidation.IsSafe ||
            !string.Equals(
                sourceValidation.CanonicalPath,
                source.CanonicalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return new ProviderReadResult(
                [],
                [Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "codex.read.source-unsafe",
                    source.RelativePath,
                    "The Codex source failed local path revalidation.")],
                startOffset,
                IsPartial: true);
        }

        List<ProviderDiagnostic> diagnostics = [];
        List<ProviderRecord> records = [];
        List<TextCandidate> textCandidates = [];
        bool isPartial = false;
        CodexJsonlReadOutcome lineResult;
        try
        {
            FileInfo file = new(sourceValidation.CanonicalPath!);
            file.Refresh();
            lineResult = await CodexJsonlReader.ReadAsync(
                sourceValidation.CanonicalPath!,
                startOffset,
                file.Length,
                (lineOffset, utf8Json, oversized) =>
                {
                    isPartial |= ProcessReadLine(
                        source,
                        lineOffset,
                        utf8Json,
                        oversized,
                        records,
                        textCandidates,
                        diagnostics);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableIo(exception))
        {
            return new ProviderReadResult(
                [],
                [Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "codex.read.source-unavailable",
                    source.RelativePath,
                    "The Codex source could not be read.")],
                startOffset,
                IsPartial: true);
        }

        if (startOffset == 0
            && source.Kind == ProviderSourceKind.TopLevel
            && threadNamesBySource.TryGetValue(
                Path.GetFullPath(source.CanonicalPath),
                out string? threadName))
        {
            records.Add(new ProviderRecord(
                source.Owner,
                "session_index.jsonl",
                long.MaxValue,
                null,
                ProviderRecordKind.AiTitle,
                threadName,
                null,
                IsChild: false));
        }

        HashSet<TextKey> responseRepresentations = textCandidates
            .Where(candidate => candidate.Representation == TextRepresentation.ResponseItem)
            .Select(candidate => new TextKey(candidate.Kind, candidate.Text))
            .ToHashSet();

        foreach (TextCandidate candidate in textCandidates.OrderBy(candidate => candidate.Sequence))
        {
            if (candidate.Representation == TextRepresentation.EventMessage
                && responseRepresentations.Contains(new TextKey(candidate.Kind, candidate.Text)))
            {
                continue;
            }

            records.Add(new ProviderRecord(
                source.Owner,
                source.RelativePath,
                candidate.Sequence,
                candidate.TimestampUtc,
                candidate.Kind,
                candidate.Text,
                candidate.UserTextKind,
                source.Kind == ProviderSourceKind.Child));
        }

        if (lineResult.HasTrailingPartialLine)
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Information,
                "codex.read.partial-line",
                source.RelativePath,
                "A trailing incomplete Codex record was deferred."));
            isPartial = true;
        }

        return new ProviderReadResult(
            records.OrderBy(record => record.Sequence).ToArray(),
            diagnostics,
            lineResult.LastCompleteOffset,
            isPartial);
    }

    private static bool ProcessReadLine(
        ProviderSource source,
        long lineOffset,
        ReadOnlyMemory<byte> utf8Json,
        bool oversized,
        List<ProviderRecord> records,
        List<TextCandidate> textCandidates,
        List<ProviderDiagnostic> diagnostics)
    {
        if (oversized)
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "codex.read.oversized-record",
                source.RelativePath,
                "A Codex record exceeded the configured size limit."));
            return true;
        }

        if (utf8Json.IsEmpty)
        {
            return false;
        }

        bool isPartial = false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8Json, DocumentOptions);
            JsonElement root = document.RootElement;
            string? type = GetString(root, "type");
            DateTimeOffset? timestamp = ParseTimestamp(GetString(root, "timestamp"));

            if (source.Kind == ProviderSourceKind.TopLevel
                && string.Equals(type, "session_meta", StringComparison.Ordinal)
                && TryGetObject(root, "payload", out JsonElement metadataPayload)
                && TryGetGuid(metadataPayload, "id", out Guid metadataId)
                && metadataId == source.Owner.SessionId)
            {
                AddTitleRecords(
                    records,
                    source,
                    metadataPayload,
                    lineOffset,
                    timestamp);
            }

            if (string.Equals(type, "response_item", StringComparison.Ordinal))
            {
                AddResponseItem(
                    textCandidates,
                    root,
                    lineOffset,
                    timestamp,
                    diagnostics,
                    source.RelativePath,
                    ref isPartial);
            }
            else if (string.Equals(type, "event_msg", StringComparison.Ordinal))
            {
                AddEventMessage(
                    textCandidates,
                    root,
                    lineOffset,
                    timestamp,
                    diagnostics,
                    source.RelativePath,
                    ref isPartial);
            }
        }
        catch (JsonException)
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "codex.read.invalid-json",
                source.RelativePath,
                "A Codex record could not be parsed."));
            isPartial = true;
        }

        return isPartial;
    }

    private static async ValueTask<CandidateReadResult> ReadCandidateAsync(
        string path,
        string relativePath,
        Guid filenameId,
        bool archived,
        CancellationToken cancellationToken)
    {
        List<ProviderDiagnostic> diagnostics = [];
        bool isPartial = false;
        SessionMetadata? metadata = null;
        string? contextModel = null;
        DateTimeOffset? lastTimestamp = null;
        int metadataRecordCount = 0;
        FileInfo file = new(path);
        file.Refresh();
        CodexJsonlReadOutcome read = await CodexJsonlReader.ReadAsync(
            path,
            startOffset: 0,
            file.Length,
            (lineOffset, utf8Json, oversized) =>
            {
                _ = lineOffset;
                if (oversized || !utf8Json.IsEmpty)
                {
                    metadataRecordCount++;
                }

                if (oversized)
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "codex.discovery.oversized-record",
                        relativePath,
                        "A Codex record exceeded the configured size limit."));
                    isPartial = true;
                    return;
                }

                if (utf8Json.IsEmpty)
                {
                    return;
                }

                try
                {
                    using JsonDocument document = JsonDocument.Parse(utf8Json, DocumentOptions);
                    JsonElement root = document.RootElement;
                    DateTimeOffset? timestamp = ParseTimestamp(GetString(root, "timestamp"));
                    if (timestamp is not null &&
                        (lastTimestamp is null || timestamp > lastTimestamp))
                    {
                        lastTimestamp = timestamp;
                    }

                    string? type = GetString(root, "type");
                    if (string.Equals(type, "session_meta", StringComparison.Ordinal)
                        && TryGetObject(root, "payload", out JsonElement payload)
                        && TryGetGuid(payload, "id", out Guid payloadId)
                        && payloadId == filenameId)
                    {
                        metadata = ParseSessionMetadata(payload, timestamp);
                    }
                    else if (string.Equals(type, "turn_context", StringComparison.Ordinal)
                             && TryGetObject(root, "payload", out JsonElement contextPayload))
                    {
                        contextModel = GetString(contextPayload, "model") ?? contextModel;
                    }
                }
                catch (JsonException)
                {
                    diagnostics.Add(Diagnostic(
                        ProviderDiagnosticSeverity.Warning,
                        "codex.discovery.invalid-json",
                        relativePath,
                        "A Codex rollout record could not be parsed."));
                    isPartial = true;
                }
            },
            cancellationToken,
            completeOffset => file.Length > PreferredMetadataScanBytes &&
                ((metadata is not null && completeOffset >= PreferredMetadataScanBytes) ||
                    completeOffset >= MaximumMetadataScanBytes ||
                    metadataRecordCount >= MaximumMetadataRecords)).ConfigureAwait(false);

        if (read.HasTrailingPartialLine)
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Information,
                "codex.discovery.partial-line",
                relativePath,
                "A trailing incomplete Codex record was deferred."));
            isPartial = true;
        }

        if (metadata is null)
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "codex.discovery.missing-session-meta",
                relativePath,
                "No filename-matching Codex session metadata was found."));
            isPartial = true;
            return new CandidateReadResult(null, diagnostics, isPartial);
        }

        DateTimeOffset fileActivity = new(file.LastWriteTimeUtc);
        DateTimeOffset lastActivity = read.StoppedEarly
            ? fileActivity
            : lastTimestamp ?? metadata.CreatedUtc ?? fileActivity;
        RolloutCandidate candidate = new(
            filenameId,
            Path.GetFullPath(path),
            relativePath,
            archived,
            metadata.Directory,
            metadata.Branch,
            metadata.Model ?? contextModel,
            metadata.CreatedUtc,
            lastActivity,
            metadata.IsChild,
            metadata.ParentSessionId,
            !string.IsNullOrWhiteSpace(metadata.Directory),
            file.Length,
            new DateTimeOffset(file.LastWriteTimeUtc));
        return new CandidateReadResult(candidate, diagnostics, isPartial);
    }

    private async ValueTask<SessionIndexReadResult> ReadSessionIndexAsync(
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(canonicalRoot, "session_index.jsonl");
        LocalPathValidation validation = pathPolicy.ValidateExistingFile(
            path,
            "session_index.jsonl",
            trustedRoot: canonicalRoot);
        if (validation.Failure == LocalPathFailure.Missing)
        {
            return new SessionIndexReadResult(
                new Dictionary<Guid, string>(),
                [],
                IsPartial: false);
        }

        if (!validation.IsSafe)
        {
            return new SessionIndexReadResult(
                new Dictionary<Guid, string>(),
                [Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "codex.index.source-unsafe",
                    "session_index.jsonl",
                    "The Codex session index path is unsafe.")],
                IsPartial: true);
        }

        Dictionary<Guid, string> names = [];
        List<ProviderDiagnostic> diagnostics = [];
        bool isPartial = false;
        CodexJsonlReadOutcome read;
        try
        {
            FileInfo file = new(validation.CanonicalPath!);
            file.Refresh();
            read = await CodexJsonlReader.ReadAsync(
                validation.CanonicalPath!,
                startOffset: 0,
                file.Length,
                (lineOffset, utf8Json, oversized) =>
                {
                    _ = lineOffset;
                    if (oversized)
                    {
                        diagnostics.Add(Diagnostic(
                            ProviderDiagnosticSeverity.Warning,
                            "codex.index.oversized-record",
                            "session_index.jsonl",
                            "A Codex session-index record exceeded the configured size limit."));
                        isPartial = true;
                        return;
                    }

                    if (utf8Json.IsEmpty)
                    {
                        return;
                    }

                    try
                    {
                        using JsonDocument document = JsonDocument.Parse(
                            utf8Json,
                            DocumentOptions);
                        JsonElement root = document.RootElement;
                        if (TryGetGuid(root, "id", out Guid id)
                            && GetString(root, "thread_name") is { } threadName
                            && !string.IsNullOrWhiteSpace(threadName))
                        {
                            names[id] = threadName;
                        }
                    }
                    catch (JsonException)
                    {
                        diagnostics.Add(Diagnostic(
                            ProviderDiagnosticSeverity.Warning,
                            "codex.index.invalid-json",
                            "session_index.jsonl",
                            "A Codex session-index record could not be parsed."));
                        isPartial = true;
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverableIo(exception))
        {
            return new SessionIndexReadResult(
                new Dictionary<Guid, string>(),
                [Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "codex.index.read-failed",
                    "session_index.jsonl",
                    "The Codex session index could not be read.")],
                IsPartial: true);
        }
        isPartial |= read.HasTrailingPartialLine;

        return new SessionIndexReadResult(names, diagnostics, isPartial);
    }

    private static List<string> EnumerateRollouts(
        string discoveryRoot,
        CancellationToken cancellationToken)
    {
        List<string> paths = [];
        Stack<string> pending = new();
        pending.Push(discoveryRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string path in Directory
                         .EnumerateFiles(directory, "rollout-*.jsonl", SearchOption.TopDirectoryOnly)
                         .Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                paths.Add(Path.GetFullPath(path));
            }

            foreach (string child in Directory
                         .EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(child);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push(child);
                }
            }
        }

        paths.Sort(StringComparer.Ordinal);
        return paths;
    }

    private static Guid? ResolveRootOwner(
        RolloutCandidate child,
        IReadOnlyDictionary<Guid, RolloutCandidate> candidatesById,
        List<ProviderDiagnostic> diagnostics,
        ref bool isPartial)
    {
        HashSet<Guid> visited = [];
        RolloutCandidate current = child;

        while (current.IsChild)
        {
            if (!visited.Add(current.SessionId))
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "codex.child.cycle",
                    child.RelativePath,
                    "A cycle prevented Codex child ownership resolution."));
                isPartial = true;
                return null;
            }

            if (current.ParentSessionId is not Guid parentId
                || !candidatesById.TryGetValue(parentId, out RolloutCandidate? parent))
            {
                diagnostics.Add(Diagnostic(
                    ProviderDiagnosticSeverity.Warning,
                    "codex.child.owner-missing",
                    child.RelativePath,
                    "A Codex child owner could not be resolved."));
                isPartial = true;
                return null;
            }

            current = parent;
        }

        return current.SessionId;
    }

    private static RolloutCandidate ChoosePrimaryCandidate(IEnumerable<RolloutCandidate> group) =>
        group
            .OrderBy(candidate => candidate.IsChild)
            .ThenBy(candidate => candidate.Archived)
            .ThenByDescending(candidate => candidate.FormatSupported)
            .ThenByDescending(candidate => candidate.LastActivityUtc)
            .ThenBy(candidate => candidate.RelativePath, StringComparer.Ordinal)
            .First();

    private static ProviderSource CreateSource(
        RolloutCandidate candidate,
        SessionIdentity owner,
        ProviderSourceKind kind) =>
        new(
            owner,
            candidate.CanonicalPath,
            candidate.RelativePath,
            kind,
            kind == ProviderSourceKind.Child ? candidate.SessionId : null,
            candidate.Archived,
            candidate.Length,
            candidate.LastWriteUtc,
            ParserVersion);

    private static SessionMetadata ParseSessionMetadata(
        JsonElement payload,
        DateTimeOffset? recordTimestamp)
    {
        DateTimeOffset? created = ParseTimestamp(GetString(payload, "timestamp"))
            ?? recordTimestamp;
        Guid? parentId = TryGetGuid(payload, "parent_thread_id", out Guid directParent)
            ? directParent
            : null;
        bool isChild = parentId is not null
            || !string.IsNullOrWhiteSpace(GetString(payload, "agent_path"));

        if (payload.TryGetProperty("source", out JsonElement source))
        {
            if (source.ValueKind == JsonValueKind.String
                && source.GetString()?.Contains("subagent", StringComparison.OrdinalIgnoreCase) == true)
            {
                isChild = true;
            }
            else if (source.ValueKind == JsonValueKind.Object
                     && source.TryGetProperty("subagent", out JsonElement subagent))
            {
                isChild = true;
                if (parentId is null
                    && TryFindGuidProperty(subagent, "parent_thread_id", out Guid nestedParent))
                {
                    parentId = nestedParent;
                }
            }
        }

        string? branch = null;
        if (TryGetObject(payload, "git", out JsonElement git))
        {
            branch = GetString(git, "branch");
        }

        return new SessionMetadata(
            GetString(payload, "cwd") ?? string.Empty,
            branch,
            GetString(payload, "model"),
            created,
            isChild,
            parentId);
    }

    private static void AddTitleRecords(
        List<ProviderRecord> records,
        ProviderSource source,
        JsonElement payload,
        long sequence,
        DateTimeOffset? timestamp)
    {
        if (GetString(payload, "name") is { } explicitName
            && !string.IsNullOrWhiteSpace(explicitName))
        {
            records.Add(new ProviderRecord(
                source.Owner,
                source.RelativePath,
                sequence,
                timestamp,
                ProviderRecordKind.ExplicitName,
                explicitName,
                null,
                IsChild: false));
        }

        string? title = GetString(payload, "thread_name") ?? GetString(payload, "title");
        if (!string.IsNullOrWhiteSpace(title))
        {
            records.Add(new ProviderRecord(
                source.Owner,
                source.RelativePath,
                sequence,
                timestamp,
                ProviderRecordKind.AiTitle,
                title,
                null,
                IsChild: false));
        }
    }

    private static void AddResponseItem(
        ICollection<TextCandidate> candidates,
        JsonElement root,
        long sequence,
        DateTimeOffset? timestamp,
        ICollection<ProviderDiagnostic> diagnostics,
        string sourceAlias,
        ref bool isPartial)
    {
        if (!TryGetObject(root, "payload", out JsonElement payload)
            || !string.Equals(GetString(payload, "type"), "message", StringComparison.Ordinal))
        {
            return;
        }

        string? role = GetString(payload, "role");
        ProviderRecordKind? kind = role switch
        {
            "user" => ProviderRecordKind.UserText,
            "assistant" => ProviderRecordKind.AssistantText,
            _ => null,
        };
        if (kind is null)
        {
            return;
        }

        string text = ExtractMessageContent(payload);
        AddTextCandidate(
            candidates,
            text,
            kind.Value,
            TextRepresentation.ResponseItem,
            sequence,
            timestamp,
            diagnostics,
            sourceAlias,
            ref isPartial);
    }

    private static void AddEventMessage(
        ICollection<TextCandidate> candidates,
        JsonElement root,
        long sequence,
        DateTimeOffset? timestamp,
        ICollection<ProviderDiagnostic> diagnostics,
        string sourceAlias,
        ref bool isPartial)
    {
        if (!TryGetObject(root, "payload", out JsonElement payload))
        {
            return;
        }

        ProviderRecordKind? kind = GetString(payload, "type") switch
        {
            "user_message" => ProviderRecordKind.UserText,
            "agent_message" => ProviderRecordKind.AssistantText,
            _ => null,
        };
        if (kind is null)
        {
            return;
        }

        AddTextCandidate(
            candidates,
            GetString(payload, "message") ?? string.Empty,
            kind.Value,
            TextRepresentation.EventMessage,
            sequence,
            timestamp,
            diagnostics,
            sourceAlias,
            ref isPartial);
    }

    private static void AddTextCandidate(
        ICollection<TextCandidate> candidates,
        string text,
        ProviderRecordKind kind,
        TextRepresentation representation,
        long sequence,
        DateTimeOffset? timestamp,
        ICollection<ProviderDiagnostic> diagnostics,
        string sourceAlias,
        ref bool isPartial)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(text) > ProviderLimits.MaxExtractedTextBytes)
        {
            diagnostics.Add(Diagnostic(
                ProviderDiagnosticSeverity.Warning,
                "codex.read.text-limit",
                sourceAlias,
                "A Codex text record exceeded the configured extraction limit."));
            isPartial = true;
            return;
        }

        candidates.Add(new TextCandidate(
            sequence,
            timestamp,
            kind,
            text,
            kind == ProviderRecordKind.UserText ? UserTextKind.Human : null,
            representation));
    }

    private static string ExtractMessageContent(JsonElement payload)
    {
        if (!payload.TryGetProperty("content", out JsonElement content))
        {
            return GetString(payload, "text") ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        List<string> parts = [];
        foreach (JsonElement item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? type = GetString(item, "type");
            if (type is not ("input_text" or "output_text" or "text"))
            {
                continue;
            }

            if (GetString(item, "text") is { } text && !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return string.Join('\n', parts);
    }

    private static bool TryParseRolloutId(string path, out Guid sessionId)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        if (fileName.Length < 36)
        {
            sessionId = default;
            return false;
        }

        return Guid.TryParseExact(fileName[^36..], "D", out sessionId);
    }

    private static bool TryFindGuidProperty(
        JsonElement element,
        string propertyName,
        out Guid value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.Ordinal)
                    && property.Value.ValueKind == JsonValueKind.String
                    && Guid.TryParse(property.Value.GetString(), out value))
                {
                    return true;
                }

                if (TryFindGuidProperty(property.Value, propertyName, out value))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (TryFindGuidProperty(item, propertyName, out value))
                {
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetGuid(JsonElement element, string propertyName, out Guid value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && Guid.TryParse(property.GetString(), out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetObject(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out value)
            && value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out DateTimeOffset timestamp)
            ? timestamp.ToUniversalTime()
            : null;

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static ProviderDiagnostic Diagnostic(
        ProviderDiagnosticSeverity severity,
        string code,
        string sourceAlias,
        string message) =>
        new(
            SessionProvider.Codex,
            severity,
            code,
            sourceAlias,
            message,
            ParserVersion: ParserVersion);

    private static bool IsRecoverableIo(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private static IEnumerable<(string DirectoryName, bool Archived)> DiscoveryDirectories()
    {
        yield return ("sessions", false);
        yield return ("archived_sessions", true);
    }

    private sealed record SessionMetadata(
        string Directory,
        string? Branch,
        string? Model,
        DateTimeOffset? CreatedUtc,
        bool IsChild,
        Guid? ParentSessionId);

    private sealed record RolloutCandidate(
        Guid SessionId,
        string CanonicalPath,
        string RelativePath,
        bool Archived,
        string Directory,
        string? Branch,
        string? Model,
        DateTimeOffset? CreatedUtc,
        DateTimeOffset LastActivityUtc,
        bool IsChild,
        Guid? ParentSessionId,
        bool FormatSupported,
        long Length,
        DateTimeOffset LastWriteUtc);

    private sealed record CandidateReadResult(
        RolloutCandidate? Candidate,
        IReadOnlyList<ProviderDiagnostic> Diagnostics,
        bool IsPartial);

    private sealed record SessionIndexReadResult(
        IReadOnlyDictionary<Guid, string> ThreadNames,
        IReadOnlyList<ProviderDiagnostic> Diagnostics,
        bool IsPartial);

    private sealed record TextCandidate(
        long Sequence,
        DateTimeOffset? TimestampUtc,
        ProviderRecordKind Kind,
        string Text,
        UserTextKind? UserTextKind,
        TextRepresentation Representation);

    private readonly record struct TextKey(ProviderRecordKind Kind, string Text);

    private enum TextRepresentation
    {
        EventMessage,
        ResponseItem,
    }
}
