using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionSearch.Benchmarks;

internal sealed record BenchmarkReport(
    int SchemaVersion,
    string RuntimeVersion,
    string ProcessArchitecture,
    int LogicalProcessorCount,
    IndexBenchmarkMetrics Index,
    SearchBenchmarkMetrics Search,
    StorageBenchmarkMetrics Storage,
    WorkingSetBenchmarkMetrics WorkingSet,
    IdleWorkingSetBenchmarkMetrics IdleWorkingSet,
    ResponsivenessBenchmarkMetrics Responsiveness,
    StageProfileReport? Profile);

internal sealed record IndexBenchmarkMetrics(
    int DiscoveredSessions,
    int CompletedSessions,
    int ChangedSources,
    int FirstMetadataRowsRequired,
    int FirstMetadataRowsObserved,
    double? FirstMetadataMilliseconds,
    double CompletionMilliseconds,
    bool IsPartial,
    IReadOnlyList<IndexDiagnosticMetrics> RecentDiagnosticGroups);

internal sealed record IndexDiagnosticMetrics(
    string Provider,
    string Severity,
    string Code,
    int Count);

internal sealed record SearchBenchmarkMetrics(
    int WarmupIterationsPerCategory,
    int MeasuredIterationsPerCategory,
    int TotalMeasurements,
    double P50Milliseconds,
    double P95Milliseconds,
    IReadOnlyList<SearchGroupMetrics> Groups,
    IReadOnlyList<SearchCategoryMetrics> Categories);

internal sealed record SearchGroupMetrics(
    string Group,
    int Measurements,
    double P50Milliseconds,
    double P95Milliseconds,
    int PartialResponses);

internal sealed record SearchCategoryMetrics(
    string Category,
    string Group,
    int Measurements,
    double P50Milliseconds,
    double P95Milliseconds,
    double MinimumMilliseconds,
    double MaximumMilliseconds,
    int PartialResponses);

internal sealed record StorageBenchmarkMetrics(
    long DatabaseBytes,
    long WriteAheadLogBytes,
    long SharedMemoryBytes,
    long TotalBytes,
    bool CheckpointTruncated);

internal sealed record WorkingSetBenchmarkMetrics(
    long BeforeIndexBytes,
    long AfterIndexBytes,
    long AfterSearchBytes,
    long MaximumObservedBytes,
    long ProcessPeakBytes,
    int SampleCount);

internal sealed record IdleWorkingSetBenchmarkMetrics(
    int WaitBeforeSamplesSeconds,
    int SampleIntervalSeconds,
    IReadOnlyList<long> SamplesBytes,
    long MaximumBytes,
    IReadOnlyList<long> ManagedHeapSamplesBytes,
    long MaximumManagedHeapBytes,
    IReadOnlyList<long> GcCommittedSamplesBytes,
    long MaximumGcCommittedBytes,
    IReadOnlyList<long> FragmentedHeapSamplesBytes,
    long MaximumFragmentedHeapBytes,
    PostIdleQueryBenchmarkMetrics PostIdleQuery);

internal sealed record PostIdleQueryBenchmarkMetrics(
    string MetadataCategory,
    double MetadataMilliseconds,
    string ContentCategory,
    double ContentMilliseconds,
    long WorkingSetAfterQueriesBytes);

internal sealed record ResponsivenessBenchmarkMetrics(
    string Coverage,
    int IntervalMilliseconds,
    int Samples,
    double MedianDelayMilliseconds,
    double P95DelayMilliseconds,
    double MaximumDelayMilliseconds,
    int DelaysOver100Milliseconds,
    bool WasCapped);

internal sealed record StageProfileReport(
    ProfileStageMetrics ProviderDiscovery,
    IReadOnlyList<ProviderOperationMetrics> ProviderDiscoveryBreakdown,
    ProfileStageMetrics MetadataPersistence,
    ProfileStageMetrics ContentIndexing,
    IReadOnlyList<ProviderOperationMetrics> ProviderContentReadBreakdown,
    ProfileStageMetrics ContentPersistence,
    ProfileStageMetrics Search);

internal sealed record ProfileStageMetrics(
    double ElapsedMilliseconds,
    long AllocatedBytes,
    long WorkingSetGrowthBytes);

internal sealed record ProviderOperationMetrics(
    string Provider,
    int Calls,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    long WorkingSetGrowthBytes);

internal static class BenchmarkJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
