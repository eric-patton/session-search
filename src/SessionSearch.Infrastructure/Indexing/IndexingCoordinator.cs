using System.Text;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Sessions;
using SessionSearch.Core.Text;
using SessionSearch.Infrastructure.Storage;

namespace SessionSearch.Infrastructure.Indexing;

public sealed record ProviderRegistration(
    ISessionProviderAdapter Adapter,
    string RootPath);

public sealed record IndexingProgress(
    string Stage,
    int DiscoveredSessions,
    int CompletedSessions,
    int ChangedSources,
    long ProcessedBytes);

public sealed record IndexingReport(
    int DiscoveredSessions,
    int CompletedSessions,
    int ChangedSources,
    long ProcessedBytes,
    bool IsPartial,
    TimeSpan Elapsed)
{
    public TimeSpan MetadataElapsed { get; init; }
}

public sealed class IndexingCoordinator
{
    private readonly SessionDatabase database;
    private readonly ProviderRegistration[] providers;
    private readonly IIndexStorageGuard storageGuard;
    private int isRunning;
    private volatile bool isPartial;

    public IndexingCoordinator(
        SessionDatabase database,
        IEnumerable<ProviderRegistration> providers,
        IIndexStorageGuard? storageGuard = null)
    {
        this.database = database;
        this.providers = providers.ToArray();
        this.storageGuard = storageGuard ?? new PhysicalIndexStorageGuard();
    }

    public bool IsPartial => isPartial;

    public async ValueTask<IndexingReport> ReconcileAsync(
        IProgress<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref isRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("An indexing pass is already running.");
        }

        long started = Environment.TickCount64;
        int discoveredCount = 0;
        int completedCount = 0;
        int changedSourceCount = 0;
        long processedBytes = 0;
        bool partial = false;
        SessionRepository repository = new(database);
        DiagnosticsRepository diagnosticsRepository = new(database);
        List<ProviderWorkItem> workItems = [];

        try
        {
            foreach (ProviderRegistration registration in providers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProviderDiscoveryResult discovery;
                try
                {
                    discovery = await registration.Adapter.DiscoverAsync(
                        registration.RootPath,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsSourceException(exception))
                {
                    partial = true;
                    await diagnosticsRepository.AddRangeAsync(
                        [ProviderFailure(registration, "provider-discovery", exception)],
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                bool discoveryComplete = !discovery.IsPartial;
                partial |= discovery.IsPartial;
                discoveredCount += discovery.Sessions.Count;
                await diagnosticsRepository.AddRangeAsync(
                    discovery.Diagnostics,
                    cancellationToken).ConfigureAwait(false);

                ProviderSessionSeed[] seeds = discovery.Sessions
                    .Select(AddFileIdentities)
                    .ToArray();
                workItems.Add(new ProviderWorkItem(
                    registration,
                    seeds,
                    discoveryComplete));
            }

            ProviderSessionSeed[] allSeeds = workItems
                .SelectMany(workItem => workItem.Seeds)
                .ToArray();
            IReadOnlyDictionary<SessionIdentity, SessionDocument> existingSessions =
                await repository.FindSessionsAsync(
                    allSeeds.Select(seed => seed.Identity).ToArray(),
                    cancellationToken).ConfigureAwait(false);
            Dictionary<SessionIdentity, SessionDocument> metadataDocuments = [];
            foreach (ProviderSessionSeed seed in allSeeds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                existingSessions.TryGetValue(seed.Identity, out SessionDocument? existing);
                metadataDocuments[seed.Identity] = BuildSeedDocument(seed, existing);
            }

            // Publish all trusted metadata in one transaction before any transcript is read.
            await repository.UpsertSessionsAsync(
                metadataDocuments.Values.ToArray(),
                cancellationToken).ConfigureAwait(false);

            TimeSpan metadataElapsed = TimeSpan.FromMilliseconds(
                Environment.TickCount64 - started);
            progress?.Report(new IndexingProgress(
                "Metadata ready",
                discoveredCount,
                completedCount,
                changedSourceCount,
                processedBytes));

            bool mayIndexContent = true;
            bool storageDiagnosticWritten = false;
            foreach (ProviderWorkItem workItem in workItems)
            {
                ProviderRegistration registration = workItem.Registration;
                foreach (ProviderSessionSeed seed in workItem.Seeds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new IndexingProgress(
                        "Indexing",
                        discoveredCount,
                        completedCount,
                        changedSourceCount,
                        processedBytes));

                    SessionDocument document = metadataDocuments[seed.Identity];

                    if (seed.FormatSupported)
                    {
                        foreach (ProviderSource source in seed.Sources)
                        {
                            if (mayIndexContent)
                            {
                                IndexStorageCheck storage = storageGuard.Check(database.DatabasePath);
                                mayIndexContent = storage.MayIndexContent;
                                if (!mayIndexContent && !storageDiagnosticWritten)
                                {
                                    storageDiagnosticWritten = true;
                                    partial = true;
                                    await diagnosticsRepository.AddRangeAsync(
                                        [StorageFailure(registration, storage)],
                                        cancellationToken).ConfigureAwait(false);
                                }
                            }

                            if (!mayIndexContent)
                            {
                                break;
                            }

                            SourceIndexState? state = await repository.FindSourceStateAsync(
                                source.CanonicalPath,
                                cancellationToken).ConfigureAwait(false);
                            SourceReadMode mode = DetermineReadMode(source, state);
                            if (mode == SourceReadMode.Unchanged)
                            {
                                continue;
                            }

                            long startOffset = mode == SourceReadMode.Append
                                ? state!.CompleteOffset
                                : 0;
                            ProviderReadResult read;
                            try
                            {
                                read = await registration.Adapter.ReadAsync(
                                    source,
                                    startOffset,
                                    cancellationToken).ConfigureAwait(false);
                                changedSourceCount++;
                                processedBytes += Math.Max(
                                    0,
                                    read.LastCompleteOffset - startOffset);
                                await diagnosticsRepository.AddRangeAsync(
                                    read.Diagnostics,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception exception) when (IsSourceException(exception))
                            {
                                partial = true;
                                await repository.MarkSourceFailedAsync(
                                    source,
                                    "source-read",
                                    cancellationToken).ConfigureAwait(false);
                                await diagnosticsRepository.AddRangeAsync(
                                    [SourceFailure(registration, source, exception)],
                                    cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            if (read.IsPartial)
                            {
                                partial = true;
                                await repository.MarkSourceFailedAsync(
                                    source,
                                    "partial-read",
                                    cancellationToken).ConfigureAwait(false);
                                continue;
                            }

                            if (mode == SourceReadMode.Append
                                && source.Kind == ProviderSourceKind.TopLevel
                                && read.Records.Any(IsTitleRecord))
                            {
                                read = await registration.Adapter.ReadAsync(
                                    source,
                                    startOffset: 0,
                                    cancellationToken).ConfigureAwait(false);
                                processedBytes += read.LastCompleteOffset;
                                await diagnosticsRepository.AddRangeAsync(
                                    read.Diagnostics,
                                    cancellationToken).ConfigureAwait(false);
                                if (read.IsPartial)
                                {
                                    partial = true;
                                    await repository.MarkSourceFailedAsync(
                                        source,
                                        "partial-title-reread",
                                        cancellationToken).ConfigureAwait(false);
                                    continue;
                                }

                                mode = SourceReadMode.Replace;
                            }

                            document = BuildSourceDocument(
                                seed,
                                document,
                                source,
                                read.Records,
                                mode);
                            SessionSegment[] segments = ToSegments(read.Records);
                            if (mode == SourceReadMode.Append)
                            {
                                await repository.AppendSourceContentAsync(
                                    document,
                                    source,
                                    segments,
                                    read.LastCompleteOffset,
                                    cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                await repository.ReplaceSourceContentAsync(
                                    document,
                                    source,
                                    segments,
                                    read.LastCompleteOffset,
                                    cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }

                    if (workItem.DiscoveryComplete)
                    {
                        bool purgeComplete = await repository.ReconcileSessionSourcesAsync(
                            seed.Identity,
                            seed.Sources.Select(source => source.CanonicalPath).ToArray(),
                            cancellationToken).ConfigureAwait(false);
                        partial |= !purgeComplete;
                    }

                    completedCount++;
                }

                if (workItem.DiscoveryComplete)
                {
                    bool purgeComplete = await repository.ReconcileProviderGenerationAsync(
                        registration.Adapter.Provider,
                        workItem.Seeds.Select(seed => seed.Identity.SessionId).ToArray(),
                        cancellationToken).ConfigureAwait(false);
                    partial |= !purgeComplete;
                }
            }

            isPartial = partial;
            progress?.Report(new IndexingProgress(
                "Ready",
                discoveredCount,
                completedCount,
                changedSourceCount,
                processedBytes));
            return new IndexingReport(
                discoveredCount,
                completedCount,
                changedSourceCount,
                processedBytes,
                partial,
                TimeSpan.FromMilliseconds(Environment.TickCount64 - started))
            {
                MetadataElapsed = metadataElapsed,
            };
        }
        finally
        {
            Volatile.Write(ref isRunning, 0);
        }
    }

    private static ProviderSessionSeed AddFileIdentities(ProviderSessionSeed seed) =>
        seed with
        {
            Sources = seed.Sources
                .Select(source => source.FileIdentity is null
                    ? source with
                    {
                        FileIdentity = SourceFileIdentity.TryRead(source.CanonicalPath),
                    }
                    : source)
                .ToArray(),
        };

    private static SessionDocument BuildSeedDocument(
        ProviderSessionSeed seed,
        SessionDocument? existing)
    {
        string sourcePath = seed.Sources
            .FirstOrDefault(source => source.Kind == ProviderSourceKind.TopLevel)
            ?.CanonicalPath
            ?? existing?.SourcePath
            ?? string.Empty;
        return new SessionDocument(
            seed.Identity,
            sourcePath,
            existing?.Title ?? seed.Identity.SessionId.ToString("D"),
            existing?.Description ?? string.Empty,
            seed.Directory,
            seed.Branch ?? existing?.Branch,
            seed.Model ?? existing?.Model,
            seed.CreatedUtc ?? existing?.CreatedUtc,
            Max(seed.LastActivityUtc, existing?.LastActivityUtc),
            seed.Sources.Sum(source => source.Length),
            seed.Archived,
            seed.FormatSupported,
            SourcePresent: true,
            seed.Sources.Select(source => source.ParserVersion).DefaultIfEmpty(1).Max());
    }

    private static SessionDocument BuildSourceDocument(
        ProviderSessionSeed seed,
        SessionDocument current,
        ProviderSource source,
        IReadOnlyList<ProviderRecord> records,
        SourceReadMode mode)
    {
        ProviderRecord[] topLevelRecords = source.Kind == ProviderSourceKind.TopLevel
            ? records.Where(record => !record.IsChild).ToArray()
            : [];
        ResolvedSessionText resolved = new(current.Title, current.Description);

        if (topLevelRecords.Length > 0 && mode == SourceReadMode.Replace)
        {
            resolved = ResolveText(seed, topLevelRecords);
        }
        else if (topLevelRecords.Length > 0)
        {
            string? latestHumanText = topLevelRecords
                .Where(record => record.Kind == ProviderRecordKind.UserText
                    && (record.UserTextKind ?? UserTextKind.Human) == UserTextKind.Human)
                .OrderBy(record => record.Sequence)
                .Select(record => DisplayTextSanitizer.Sanitize(record.Text))
                .Where(text => text.Length > 0)
                .LastOrDefault();
            if (latestHumanText is not null)
            {
                resolved = resolved with
                {
                    Description = TextNormalization.TruncateDescription(latestHumanText),
                };
            }
        }

        DateTimeOffset lastActivity = records
            .Where(record => record.TimestampUtc.HasValue)
            .Select(record => record.TimestampUtc!.Value)
            .Append(current.LastActivityUtc)
            .Append(seed.LastActivityUtc)
            .Max();
        return current with
        {
            Title = resolved.Title,
            Description = resolved.Description,
            LastActivityUtc = lastActivity,
            SourceBytes = seed.Sources.Sum(item => item.Length),
        };
    }

    private static ResolvedSessionText ResolveText(
        ProviderSessionSeed seed,
        IReadOnlyList<ProviderRecord> records)
    {
        SessionTextEvidence evidence = new(
            ExplicitNames: records
                .Where(record => record.Kind == ProviderRecordKind.ExplicitName)
                .Select(record => new TimestampedText(record.Text, record.Sequence))
                .ToArray(),
            AiTitles: records
                .Where(record => record.Kind == ProviderRecordKind.AiTitle)
                .Select(record => new TimestampedText(record.Text, record.Sequence))
                .ToArray(),
            UserTexts: records
                .Where(record => record.Kind == ProviderRecordKind.UserText)
                .Select(record => new UserTextEvidence(
                    record.Text,
                    record.Sequence,
                    record.UserTextKind ?? UserTextKind.Human))
                .ToArray());
        return SessionTextResolver.Resolve(
            seed.Identity.SessionId.ToString("D"),
            evidence);
    }

    private static SessionSegment[] ToSegments(IEnumerable<ProviderRecord> records)
    {
        List<SessionSegment> segments = [];
        StringBuilder text = new();
        ProviderRecord? firstRecord = null;
        int textBytes = 0;
        int boundaryBytes = Encoding.UTF8.GetByteCount(ProviderLimits.SearchRecordBoundary);

        foreach (ProviderRecord record in records.Where(IsSearchableRecord))
        {
            int recordBytes = Encoding.UTF8.GetByteCount(record.Text);
            if (recordBytes > ProviderLimits.MaxStoredSegmentBytes)
            {
                FlushSegment();
                segments.Add(CreateSegment(record, record.Text));
                continue;
            }

            int requiredBytes = recordBytes;
            if (firstRecord is not null)
            {
                requiredBytes += boundaryBytes;
            }

            if (firstRecord is not null
                && textBytes + requiredBytes > ProviderLimits.MaxStoredSegmentBytes)
            {
                FlushSegment();
            }

            if (firstRecord is null)
            {
                firstRecord = record;
            }
            else
            {
                text.Append(ProviderLimits.SearchRecordBoundary);
                textBytes += boundaryBytes;
            }

            text.Append(record.Text);
            textBytes += recordBytes;
        }

        FlushSegment();
        return [.. segments];

        void FlushSegment()
        {
            if (firstRecord is null)
            {
                return;
            }

            segments.Add(CreateSegment(firstRecord, text.ToString()));
            text.Clear();
            firstRecord = null;
            textBytes = 0;
        }

        static SessionSegment CreateSegment(ProviderRecord record, string value) =>
            new(
                record.Sequence,
                record.Kind,
                record.TimestampUtc,
                record.Kind,
                record.IsChild,
                value);
    }

    private static SourceReadMode DetermineReadMode(
        ProviderSource source,
        SourceIndexState? state)
    {
        if (state is null
            || state.ParserVersion != source.ParserVersion
            || state.Status != 0
            || !FileIdentityMatches(source.FileIdentity, state.FileIdentity)
            || source.Length < state.Length
            || state.CompleteOffset > source.Length)
        {
            return SourceReadMode.Replace;
        }

        if (state.Length == source.Length
            && state.LastWriteUtc == source.LastWriteUtc
            && state.CompleteOffset == source.Length)
        {
            return SourceReadMode.Unchanged;
        }

        return source.Length > state.Length && state.CompleteOffset == state.Length
            ? SourceReadMode.Append
            : SourceReadMode.Replace;
    }

    private static bool FileIdentityMatches(string? current, string? stored) =>
        current is null || stored is null
            ? current is null && stored is null
            : string.Equals(current, stored, StringComparison.Ordinal);

    private static bool IsTitleRecord(ProviderRecord record) =>
        record.Kind is ProviderRecordKind.ExplicitName or ProviderRecordKind.AiTitle;

    private static bool IsSearchableRecord(ProviderRecord record) =>
        record.Kind is ProviderRecordKind.UserText
            or ProviderRecordKind.AssistantText
            or ProviderRecordKind.ToolText;

    private static bool IsSourceException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException;

    private static DateTimeOffset Max(
        DateTimeOffset value,
        DateTimeOffset? other) =>
        other.HasValue && other.Value > value ? other.Value : value;

    private static ProviderDiagnostic ProviderFailure(
        ProviderRegistration registration,
        string code,
        Exception exception) =>
        new(
            registration.Adapter.Provider,
            ProviderDiagnosticSeverity.Error,
            code,
            registration.Adapter.Provider.ToString(),
            SanitizeException(exception),
            ExceptionType: exception.GetType().Name);

    private static ProviderDiagnostic SourceFailure(
        ProviderRegistration registration,
        ProviderSource source,
        Exception exception) =>
        new(
            registration.Adapter.Provider,
            ProviderDiagnosticSeverity.Warning,
            "source-read",
            source.RelativePath,
            SanitizeException(exception),
            ParserVersion: source.ParserVersion,
            RetryState: 1,
            ExceptionType: exception.GetType().Name);

    private static ProviderDiagnostic StorageFailure(
        ProviderRegistration registration,
        IndexStorageCheck storage) =>
        new(
            registration.Adapter.Provider,
            ProviderDiagnosticSeverity.Warning,
            storage.DiagnosticCode ?? "index-storage-limit",
            "app-index",
            storage.Message ?? "Transcript indexing paused because app-index storage is unavailable.");

    private static string SanitizeException(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "The source could not be read because access was denied.",
            InvalidDataException => "The source contained an unsupported or invalid record.",
            _ => "The source could not be read due to an I/O error.",
        };

    private enum SourceReadMode
    {
        Unchanged,
        Append,
        Replace,
    }

    private sealed record ProviderWorkItem(
        ProviderRegistration Registration,
        ProviderSessionSeed[] Seeds,
        bool DiscoveryComplete);
}
