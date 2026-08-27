using System.Diagnostics;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Infrastructure.Indexing;

namespace SessionSearch.Benchmarks;

internal sealed class IndexPipelineProfiler
{
    private readonly Dictionary<SessionProvider, OperationAccumulator> discovery = [];
    private readonly Dictionary<SessionProvider, OperationAccumulator> contentReads = [];
    private ProfileSnapshot? indexStart;
    private ProfileSnapshot? metadataReady;
    private ProfileSnapshot? indexComplete;
    private ProfileSnapshot? searchStart;
    private ProfileSnapshot? searchComplete;
    private IndexingReport? indexingReport;

    public void StartIndex() => indexStart = ProfileSnapshot.Capture();

    public void MetadataReady() => metadataReady ??= ProfileSnapshot.Capture();

    public void CompleteIndex(IndexingReport report)
    {
        indexingReport = report;
        metadataReady ??= ProfileSnapshot.Capture();
        indexComplete = ProfileSnapshot.Capture();
    }

    public void StartSearch() => searchStart = ProfileSnapshot.Capture();

    public void CompleteSearch() => searchComplete = ProfileSnapshot.Capture();

    public static OperationToken StartProviderOperation(
        SessionProvider provider,
        ProviderOperationKind kind) =>
        new(
            provider,
            kind,
            ProfileSnapshot.Capture(
                captureWorkingSet: kind == ProviderOperationKind.Discovery));

    public void CompleteProviderOperation(OperationToken token)
    {
        ProfileSnapshot completed = ProfileSnapshot.Capture(
            captureWorkingSet: token.Kind == ProviderOperationKind.Discovery);
        Dictionary<SessionProvider, OperationAccumulator> target =
            token.Kind == ProviderOperationKind.Discovery
                ? discovery
                : contentReads;
        if (!target.TryGetValue(token.Provider, out OperationAccumulator? accumulator))
        {
            accumulator = new OperationAccumulator();
            target.Add(token.Provider, accumulator);
        }

        accumulator.Add(token.Start, completed);
    }

    public StageProfileReport CreateReport()
    {
        if (indexStart is null
            || metadataReady is null
            || indexComplete is null
            || searchStart is null
            || searchComplete is null
            || indexingReport is null)
        {
            throw new InvalidOperationException("Stage profiling did not capture every boundary.");
        }

        OperationAccumulator discoveryTotal = OperationAccumulator.Sum(discovery.Values);
        OperationAccumulator readsTotal = OperationAccumulator.Sum(contentReads.Values);
        ProfileStageMetrics metadataCombined = ProfileSnapshot.Difference(
            indexStart.Value,
            metadataReady.Value,
            indexingReport.MetadataElapsed.TotalMilliseconds);
        ProfileStageMetrics contentIndexing = ProfileSnapshot.Difference(
            metadataReady.Value,
            indexComplete.Value,
            Math.Max(
                0,
                indexingReport.Elapsed.TotalMilliseconds
                    - indexingReport.MetadataElapsed.TotalMilliseconds));
        ProfileStageMetrics search = ProfileSnapshot.Difference(
            searchStart.Value,
            searchComplete.Value);

        ProfileStageMetrics providerDiscovery = discoveryTotal.ToStageMetrics();
        ProfileStageMetrics metadataPersistence = Subtract(
            metadataCombined,
            providerDiscovery);
        ProfileStageMetrics contentPersistence = Subtract(
            contentIndexing,
            readsTotal.ToStageMetrics(includeWorkingSet: false));

        return new StageProfileReport(
            providerDiscovery,
            ToProviderMetrics(discovery),
            metadataPersistence,
            contentIndexing,
            ToProviderMetrics(contentReads, includeWorkingSet: false),
            contentPersistence,
            search);
    }

    private static ProviderOperationMetrics[] ToProviderMetrics(
        IReadOnlyDictionary<SessionProvider, OperationAccumulator> values,
        bool includeWorkingSet = true) =>
        values
            .OrderBy(pair => pair.Key)
            .Select(pair => pair.Value.ToProviderMetrics(
                ProviderLabel(pair.Key),
                includeWorkingSet))
            .ToArray();

    private static ProfileStageMetrics Subtract(
        ProfileStageMetrics total,
        ProfileStageMetrics excluded) =>
        new(
            Round(Math.Max(0, total.ElapsedMilliseconds - excluded.ElapsedMilliseconds)),
            Math.Max(0, total.AllocatedBytes - excluded.AllocatedBytes),
            total.WorkingSetGrowthBytes - excluded.WorkingSetGrowthBytes);

    private static string ProviderLabel(SessionProvider provider) =>
        provider switch
        {
            SessionProvider.ClaudeCode => "claude-code",
            SessionProvider.Codex => "codex",
            _ => "unknown",
        };

    private static double Round(double value) => Math.Round(value, 3);

    internal readonly record struct OperationToken(
        SessionProvider Provider,
        ProviderOperationKind Kind,
        ProfileSnapshot Start);

    internal enum ProviderOperationKind
    {
        Discovery,
        ContentRead,
    }

    internal readonly record struct ProfileSnapshot(
        long Timestamp,
        long AllocatedBytes,
        long WorkingSetBytes)
    {
        public static ProfileSnapshot Capture(bool captureWorkingSet = true)
        {
            long workingSetBytes = 0;
            if (captureWorkingSet)
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                workingSetBytes = process.WorkingSet64;
            }

            return new ProfileSnapshot(
                Stopwatch.GetTimestamp(),
                GC.GetTotalAllocatedBytes(precise: false),
                workingSetBytes);
        }

        public static ProfileStageMetrics Difference(
            ProfileSnapshot start,
            ProfileSnapshot end,
            double? elapsedMilliseconds = null) =>
            new(
                Round(elapsedMilliseconds
                    ?? Stopwatch.GetElapsedTime(start.Timestamp, end.Timestamp)
                        .TotalMilliseconds),
                Math.Max(0, end.AllocatedBytes - start.AllocatedBytes),
                end.WorkingSetBytes - start.WorkingSetBytes);
    }

    private sealed class OperationAccumulator
    {
        private int calls;
        private long timestampTicks;
        private long allocatedBytes;
        private long workingSetGrowthBytes;

        public void Add(ProfileSnapshot start, ProfileSnapshot end)
        {
            calls++;
            timestampTicks += end.Timestamp - start.Timestamp;
            allocatedBytes += Math.Max(0, end.AllocatedBytes - start.AllocatedBytes);
            workingSetGrowthBytes += end.WorkingSetBytes - start.WorkingSetBytes;
        }

        public ProfileStageMetrics ToStageMetrics(bool includeWorkingSet = true) =>
            new(
                Round(TimeSpan.FromSeconds(
                    (double)timestampTicks / Stopwatch.Frequency).TotalMilliseconds),
                allocatedBytes,
                includeWorkingSet ? workingSetGrowthBytes : 0);

        public ProviderOperationMetrics ToProviderMetrics(
            string provider,
            bool includeWorkingSet) =>
            new(
                provider,
                calls,
                Round(TimeSpan.FromSeconds(
                    (double)timestampTicks / Stopwatch.Frequency).TotalMilliseconds),
                allocatedBytes,
                includeWorkingSet ? workingSetGrowthBytes : 0);

        public static OperationAccumulator Sum(IEnumerable<OperationAccumulator> values)
        {
            OperationAccumulator total = new();
            foreach (OperationAccumulator value in values)
            {
                total.calls += value.calls;
                total.timestampTicks += value.timestampTicks;
                total.allocatedBytes += value.allocatedBytes;
                total.workingSetGrowthBytes += value.workingSetGrowthBytes;
            }

            return total;
        }
    }
}

internal sealed class ProfilingProviderAdapter(
    ISessionProviderAdapter inner,
    IndexPipelineProfiler profiler) : ISessionProviderAdapter
{
    public SessionProvider Provider => inner.Provider;

    public async ValueTask<ProviderDiscoveryResult> DiscoverAsync(
        string rootPath,
        CancellationToken cancellationToken)
    {
        IndexPipelineProfiler.OperationToken token = IndexPipelineProfiler.StartProviderOperation(
            Provider,
            IndexPipelineProfiler.ProviderOperationKind.Discovery);
        try
        {
            return await inner.DiscoverAsync(rootPath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            profiler.CompleteProviderOperation(token);
        }
    }

    public async ValueTask<ProviderReadResult> ReadAsync(
        ProviderSource source,
        long startOffset,
        CancellationToken cancellationToken)
    {
        IndexPipelineProfiler.OperationToken token = IndexPipelineProfiler.StartProviderOperation(
            Provider,
            IndexPipelineProfiler.ProviderOperationKind.ContentRead);
        try
        {
            return await inner.ReadAsync(
                source,
                startOffset,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            profiler.CompleteProviderOperation(token);
        }
    }
}
