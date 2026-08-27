using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Search;
using SessionSearch.Core.Sessions;
using SessionSearch.Infrastructure.Claude;
using SessionSearch.Infrastructure.Codex;
using SessionSearch.Infrastructure.Indexing;
using SessionSearch.Infrastructure.Search;
using SessionSearch.Infrastructure.Storage;

namespace SessionSearch.Benchmarks;

internal sealed class BenchmarkRunner
{
    private const string DatabaseFileName = "session-search-benchmark.sqlite3";
    private const string ManifestResourceName =
        "SessionSearch.Benchmarks.query-manifest.json";
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<BenchmarkReport> RunAsync(
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateIsolation(options);
        IReadOnlyList<BenchmarkQuery> queries = LoadQueries();

        Directory.CreateDirectory(options.DataRoot);
        string databasePath = Path.Combine(options.DataRoot, DatabaseFileName);
        string[] ownedArtifacts = DatabaseArtifacts(databasePath);
        if (ownedArtifacts.Any(File.Exists))
        {
            throw new BenchmarkUsageException(
                "The isolated data root already contains a benchmark index artifact.");
        }

        BenchmarkReport? report = null;
        try
        {
            WorkingSetTracker workingSet = new();
            await using (SessionDatabase database = await SessionDatabase.CreateAsync(
                databasePath,
                protectDirectory: OperatingSystem.IsWindows(),
                cancellationToken).ConfigureAwait(false))
            {
                IndexPipelineProfiler? profiler = options.ProfileStages
                    ? new IndexPipelineProfiler()
                    : null;
                ISessionProviderAdapter claudeAdapter = ProfileIfRequested(
                    new ClaudeSessionProviderAdapter(),
                    profiler);
                ISessionProviderAdapter codexAdapter = ProfileIfRequested(
                    new CodexProviderAdapter(),
                    profiler);
                IndexingCoordinator coordinator = new(
                    database,
                    [
                        new ProviderRegistration(
                            claudeAdapter,
                            options.ClaudeRoot),
                        new ProviderRegistration(
                            codexAdapter,
                            options.CodexRoot),
                    ]);
                SessionSearchService search = new(database, () => coordinator.IsPartial);

                workingSet.CaptureBeforeIndex();
                profiler?.StartIndex();
                await using SchedulerResponsivenessProbe responsivenessProbe = new(
                    intervalMilliseconds: 25,
                    cancellationToken);
                (IndexingReport index, double? firstMetadataMilliseconds) =
                    await MeasureIndexAsync(
                        coordinator,
                        search,
                        profiler,
                        cancellationToken).ConfigureAwait(false);
                ResponsivenessBenchmarkMetrics responsiveness =
                    await responsivenessProbe.CompleteAsync().ConfigureAwait(false);
                profiler?.CompleteIndex(index);
                workingSet.CaptureAfterIndex();
                IReadOnlyList<PersistedProviderDiagnostic> recentDiagnostics =
                    await new DiagnosticsRepository(database).ListRecentAsync(
                        100,
                        cancellationToken).ConfigureAwait(false);
                IndexDiagnosticMetrics[] diagnosticGroups = recentDiagnostics
                    .GroupBy(diagnostic => new
                    {
                        Provider = diagnostic.Provider?.ToString() ?? "Application",
                        Severity = diagnostic.Severity.ToString(),
                        diagnostic.Code,
                    })
                    .Select(group => new IndexDiagnosticMetrics(
                        group.Key.Provider,
                        group.Key.Severity,
                        group.Key.Code,
                        group.Count()))
                    .OrderBy(group => group.Provider, StringComparer.Ordinal)
                    .ThenBy(group => group.Severity, StringComparer.Ordinal)
                    .ThenBy(group => group.Code, StringComparer.Ordinal)
                    .ToArray();

                profiler?.StartSearch();
                SearchBenchmarkMetrics searchMetrics = await MeasureSearchAsync(
                    search,
                    queries,
                    options.WarmupIterations,
                    options.MeasuredIterations,
                    workingSet,
                    cancellationToken).ConfigureAwait(false);
                profiler?.CompleteSearch();
                workingSet.CaptureAfterSearch();

                bool checkpointTruncated = await database.CheckpointAndTruncateAsync(
                    cancellationToken).ConfigureAwait(false);
                IdleWorkingSetBenchmarkMetrics idleWorkingSet =
                    await MeasureIdleWorkingSetAsync(
                        workingSet,
                        database,
                        search,
                        queries,
                        options.IdleWaitSeconds,
                        options.IdleSampleIntervalSeconds,
                        cancellationToken).ConfigureAwait(false);
                StorageBenchmarkMetrics storage = MeasureStorage(
                    databasePath,
                    checkpointTruncated);
                report = new BenchmarkReport(
                    SchemaVersion: 5,
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    Environment.ProcessorCount,
                    new IndexBenchmarkMetrics(
                        index.DiscoveredSessions,
                        index.CompletedSessions,
                        index.ChangedSources,
                        Math.Min(50, index.DiscoveredSessions),
                        firstMetadataMilliseconds.HasValue
                            ? Math.Min(50, index.DiscoveredSessions)
                            : 0,
                        Round(firstMetadataMilliseconds),
                        Round(index.Elapsed.TotalMilliseconds),
                        index.IsPartial,
                        diagnosticGroups),
                    searchMetrics,
                    storage,
                    workingSet.ToMetrics(),
                    idleWorkingSet,
                    responsiveness,
                    profiler?.CreateReport());
            }
        }
        finally
        {
            DeleteOwnedArtifacts(ownedArtifacts);
        }

        if (report is null)
        {
            throw new InvalidOperationException("The benchmark did not produce a report.");
        }

        await WriteReportAsync(
            options.OutputPath,
            report,
            cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsWindows())
        {
            AppDataSecurity.ProtectStandaloneFile(options.OutputPath);
        }

        return report;
    }

    private static async Task<(IndexingReport Report, double? FirstMetadataMilliseconds)>
        MeasureIndexAsync(
            IndexingCoordinator coordinator,
            SessionSearchService search,
            IndexPipelineProfiler? profiler,
            CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();
        TaskCompletionSource<int> metadataReadySignal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        InlineProgress<IndexingProgress> progress = new(value =>
        {
            if (string.Equals(value.Stage, "Metadata ready", StringComparison.Ordinal))
            {
                profiler?.MetadataReady();
                metadataReadySignal.TrySetResult(Math.Min(50, value.DiscoveredSessions));
            }
        });

        Task<IndexingReport> indexingTask = coordinator.ReconcileAsync(
            progress,
            cancellationToken).AsTask();
        Task<double?> metadataTask = ObserveFirstMetadataAsync(
            search,
            indexingTask,
            metadataReadySignal.Task,
            clock,
            cancellationToken);

        IndexingReport report = await indexingTask.ConfigureAwait(false);
        double? firstMetadataMilliseconds = await metadataTask.ConfigureAwait(false);
        return (report, firstMetadataMilliseconds);
    }

    private static ISessionProviderAdapter ProfileIfRequested(
        ISessionProviderAdapter adapter,
        IndexPipelineProfiler? profiler) =>
        profiler is null ? adapter : new ProfilingProviderAdapter(adapter, profiler);

    private static async Task<double?> ObserveFirstMetadataAsync(
        SessionSearchService search,
        Task<IndexingReport> indexingTask,
        Task<int> metadataReadySignal,
        Stopwatch clock,
        CancellationToken cancellationToken)
    {
        Task firstSignal = await Task.WhenAny(
            metadataReadySignal,
            indexingTask).ConfigureAwait(false);
        if (firstSignal == indexingTask && !indexingTask.IsCompletedSuccessfully)
        {
            return null;
        }

        int expectedRows = metadataReadySignal.IsCompletedSuccessfully
            ? await metadataReadySignal.ConfigureAwait(false)
            : 0;

        ParsedQuery browse = QueryParser.Parse(string.Empty).Query
            ?? throw new InvalidOperationException("The browse query could not be parsed.");
        SessionSearchRequest request = new(browse, PageSize: 50);

        while (!indexingTask.IsCompleted)
        {
            if (await HasVisibleSessionsAsync(
                search,
                request,
                expectedRows,
                cancellationToken).ConfigureAwait(false))
            {
                return clock.Elapsed.TotalMilliseconds;
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }

        return await HasVisibleSessionsAsync(
            search,
            request,
            expectedRows,
            cancellationToken).ConfigureAwait(false)
            ? clock.Elapsed.TotalMilliseconds
            : null;
    }

    private static async Task<bool> HasVisibleSessionsAsync(
        SessionSearchService search,
        SessionSearchRequest request,
        int expectedRows,
        CancellationToken cancellationToken)
    {
        try
        {
            SessionSearchPage page = await search.SearchAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            return page.TotalCount >= expectedRows
                && page.Results.Count >= expectedRows;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return false;
        }
    }

    private static async Task<SearchBenchmarkMetrics> MeasureSearchAsync(
        SessionSearchService search,
        IReadOnlyList<BenchmarkQuery> queries,
        int warmupIterations,
        int measuredIterations,
        WorkingSetTracker workingSet,
        CancellationToken cancellationToken)
    {
        List<double> allMeasurements = [];
        List<SearchCategoryMetrics> categories = [];
        Dictionary<string, List<double>> groupMeasurements = new(StringComparer.Ordinal);
        Dictionary<string, int> groupPartialResponses = new(StringComparer.Ordinal);

        foreach (BenchmarkQuery query in queries)
        {
            ParsedQuery parsed = QueryParser.Parse(query.Query).Query
                ?? throw new InvalidDataException(
                    "The embedded benchmark query manifest is invalid.");
            SessionSearchRequest request = new(
                parsed,
                ContentMode: query.Group == "metadata"
                    ? SearchContentMode.MetadataOnly
                    : SearchContentMode.All);

            for (int iteration = 0; iteration < warmupIterations; iteration++)
            {
                SessionSearchPage warmup = await search.SearchAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
                GC.KeepAlive(warmup.TotalCount);
            }

            List<double> measurements = new(measuredIterations);
            int partialResponses = 0;
            for (int iteration = 0; iteration < measuredIterations; iteration++)
            {
                long started = Stopwatch.GetTimestamp();
                SessionSearchPage page = await search.SearchAsync(
                    request,
                    cancellationToken).ConfigureAwait(false);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                measurements.Add(elapsed);
                allMeasurements.Add(elapsed);
                partialResponses += page.IsPartial ? 1 : 0;
                GC.KeepAlive(page.TotalCount);
                workingSet.Capture();
            }

            if (!groupMeasurements.TryGetValue(query.Group, out List<double>? grouped))
            {
                grouped = [];
                groupMeasurements.Add(query.Group, grouped);
            }

            grouped.AddRange(measurements);
            groupPartialResponses[query.Group] =
                groupPartialResponses.GetValueOrDefault(query.Group) + partialResponses;

            categories.Add(new SearchCategoryMetrics(
                query.Category,
                query.Group,
                measurements.Count,
                Percentile(measurements, 0.50),
                Percentile(measurements, 0.95),
                Round(measurements.Min()),
                Round(measurements.Max()),
                partialResponses));
        }

        SearchGroupMetrics[] groups = groupMeasurements
            .Select(pair => new SearchGroupMetrics(
                pair.Key,
                pair.Value.Count,
                Percentile(pair.Value, 0.50),
                Percentile(pair.Value, 0.95),
                groupPartialResponses[pair.Key]))
            .OrderBy(group => SearchGroupOrder(group.Group))
            .ToArray();

        return new SearchBenchmarkMetrics(
            warmupIterations,
            measuredIterations,
            allMeasurements.Count,
            Percentile(allMeasurements, 0.50),
            Percentile(allMeasurements, 0.95),
            groups,
            categories);
    }

    private static IReadOnlyList<BenchmarkQuery> LoadQueries()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            ManifestResourceName)
            ?? throw new InvalidDataException(
                "The embedded benchmark query manifest is unavailable.");
        QueryManifest? manifest = JsonSerializer.Deserialize<QueryManifest>(
            stream,
            ManifestJsonOptions);
        if (manifest is null
            || manifest.SchemaVersion != 1
            || manifest.Queries.Count < 20
            || manifest.Queries.Count > 32)
        {
            throw new InvalidDataException(
                "The embedded benchmark query manifest is invalid.");
        }

        HashSet<string> categories = new(StringComparer.Ordinal);
        foreach (BenchmarkQuery query in manifest.Queries)
        {
            if (string.IsNullOrWhiteSpace(query.Category)
                || query.Category.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character != '-')
                || query.Group is not ("browse" or "metadata" or "transcript")
                || !categories.Add(query.Category)
                || !QueryParser.Parse(query.Query).IsSuccess)
            {
                throw new InvalidDataException(
                    "The embedded benchmark query manifest is invalid.");
            }
        }

        return manifest.Queries;
    }

    private static int SearchGroupOrder(string group) =>
        group switch
        {
            "browse" => 0,
            "metadata" => 1,
            "transcript" => 2,
            _ => 3,
        };

    private static async Task<IdleWorkingSetBenchmarkMetrics> MeasureIdleWorkingSetAsync(
        WorkingSetTracker workingSet,
        SessionDatabase database,
        SessionSearchService search,
        IReadOnlyList<BenchmarkQuery> queries,
        int waitBeforeSamplesSeconds,
        int sampleIntervalSeconds,
        CancellationToken cancellationToken)
    {
        TimeSpan totalWait = TimeSpan.FromSeconds(waitBeforeSamplesSeconds);
        TimeSpan maintenanceDelay = totalWait < TimeSpan.FromSeconds(5)
            ? totalWait
            : TimeSpan.FromSeconds(5);
        if (maintenanceDelay > TimeSpan.Zero)
        {
            await Task.Delay(maintenanceDelay, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await Task.Yield();
        }

        await IdleResourceMaintenance.TryReleaseTransientResourcesAsync(
            database,
            cancellationToken)
            .ConfigureAwait(false);
        TimeSpan remainingWait = totalWait - maintenanceDelay;
        if (remainingWait > TimeSpan.Zero)
        {
            await Task.Delay(remainingWait, cancellationToken).ConfigureAwait(false);
        }

        long[] samples = new long[3];
        long[] managedHeapSamples = new long[3];
        long[] committedSamples = new long[3];
        long[] fragmentedSamples = new long[3];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = workingSet.Capture();
            managedHeapSamples[index] = GC.GetTotalMemory(forceFullCollection: false);
            GCMemoryInfo memory = GC.GetGCMemoryInfo();
            committedSamples[index] = memory.TotalCommittedBytes;
            fragmentedSamples[index] = memory.FragmentedBytes;
            if (index + 1 < samples.Length)
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(sampleIntervalSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        BenchmarkQuery metadataQuery = queries.First(query => query.Group == "metadata");
        BenchmarkQuery contentQuery = queries.First(query => query.Group == "transcript");
        double metadataMilliseconds = await MeasurePostIdleQueryAsync(
            search,
            metadataQuery,
            cancellationToken).ConfigureAwait(false);
        double contentMilliseconds = await MeasurePostIdleQueryAsync(
            search,
            contentQuery,
            cancellationToken).ConfigureAwait(false);
        long workingSetAfterQueries = workingSet.Capture();

        return new IdleWorkingSetBenchmarkMetrics(
            waitBeforeSamplesSeconds,
            sampleIntervalSeconds,
            samples,
            samples.Max(),
            managedHeapSamples,
            managedHeapSamples.Max(),
            committedSamples,
            committedSamples.Max(),
            fragmentedSamples,
            fragmentedSamples.Max(),
            new PostIdleQueryBenchmarkMetrics(
                metadataQuery.Category,
                metadataMilliseconds,
                contentQuery.Category,
                contentMilliseconds,
                workingSetAfterQueries));
    }

    private static async Task<double> MeasurePostIdleQueryAsync(
        SessionSearchService search,
        BenchmarkQuery query,
        CancellationToken cancellationToken)
    {
        ParsedQuery parsed = QueryParser.Parse(query.Query).Query
            ?? throw new InvalidDataException(
                "The embedded benchmark query manifest is invalid.");
        SessionSearchRequest request = new(
            parsed,
            ContentMode: query.Group == "metadata"
                ? SearchContentMode.MetadataOnly
                : SearchContentMode.All);
        long started = Stopwatch.GetTimestamp();
        SessionSearchPage page = await search.SearchAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        GC.KeepAlive(page.TotalCount);
        return Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static StorageBenchmarkMetrics MeasureStorage(
        string databasePath,
        bool checkpointTruncated)
    {
        long databaseBytes = FileLength(databasePath);
        long walBytes = FileLength(databasePath + "-wal");
        long shmBytes = FileLength(databasePath + "-shm");
        return new StorageBenchmarkMetrics(
            databaseBytes,
            walBytes,
            shmBytes,
            checked(databaseBytes + walBytes + shmBytes),
            checkpointTruncated);
    }

    private static long FileLength(string path) =>
        File.Exists(path) ? new FileInfo(path).Length : 0;

    private static double Percentile(List<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        double[] ordered = [.. values.Order()];
        int index = Math.Clamp(
            (int)Math.Ceiling(percentile * ordered.Length) - 1,
            0,
            ordered.Length - 1);
        return Round(ordered[index]);
    }

    private static double Round(double value) => Math.Round(value, 3);

    private static double? Round(double? value) =>
        value.HasValue ? Round(value.Value) : null;

    private static void ValidateIsolation(BenchmarkOptions options)
    {
        if (!Directory.Exists(options.ClaudeRoot)
            || !Directory.Exists(options.CodexRoot))
        {
            throw new BenchmarkUsageException(
                "Both provider roots must be existing directories.");
        }

        string? dataRootDrive = Path.GetPathRoot(options.DataRoot);
        if (string.IsNullOrEmpty(dataRootDrive)
            || PathsEqual(options.DataRoot, dataRootDrive)
            || IsSpecialRoot(options.DataRoot))
        {
            throw new BenchmarkUsageException(
                "The data root must be a dedicated child directory, not a broad system or profile root.");
        }

        if (PathsOverlap(options.DataRoot, options.ClaudeRoot)
            || PathsOverlap(options.DataRoot, options.CodexRoot))
        {
            throw new BenchmarkUsageException(
                "The isolated data root must not overlap either provider root.");
        }

        if (IsWithin(options.OutputPath, options.ClaudeRoot)
            || IsWithin(options.OutputPath, options.CodexRoot))
        {
            throw new BenchmarkUsageException(
                "The output report must not be written inside a provider root.");
        }

        string databasePath = Path.Combine(options.DataRoot, DatabaseFileName);
        if (DatabaseArtifacts(databasePath).Any(path => PathsEqual(path, options.OutputPath)))
        {
            throw new BenchmarkUsageException(
                "The output report must not replace a benchmark database artifact.");
        }
    }

    private static bool IsSpecialRoot(string path)
    {
        string[] broadRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.GetTempPath(),
        ];
        return broadRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Any(root => PathsEqual(path, root));
    }

    private static bool PathsOverlap(string left, string right) =>
        IsWithin(left, right) || IsWithin(right, left);

    private static bool IsWithin(string path, string parent)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if (PathsEqual(normalizedPath, normalizedParent))
        {
            return true;
        }

        string prefix = normalizedParent + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);

    private static string[] DatabaseArtifacts(string databasePath) =>
        [
            databasePath,
            databasePath + "-wal",
            databasePath + "-shm",
            databasePath + "-journal",
        ];

    private static void DeleteOwnedArtifacts(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task WriteReportAsync(
        string outputPath,
        BenchmarkReport report,
        CancellationToken cancellationToken)
    {
        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new BenchmarkUsageException(
                "The output report must have a parent directory.");
        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = outputPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            byte[] contents = JsonSerializer.SerializeToUtf8Bytes(
                report,
                BenchmarkJson.Options);
            await using (FileStream placeholder = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                useAsync: true))
            {
                await placeholder.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (OperatingSystem.IsWindows())
            {
                AppDataSecurity.ProtectStandaloneFile(temporaryPath);
            }

            await File.WriteAllBytesAsync(
                temporaryPath,
                contents,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private sealed record QueryManifest(
        int SchemaVersion,
        IReadOnlyList<BenchmarkQuery> Queries);

    private sealed record BenchmarkQuery(string Category, string Group, string Query);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class WorkingSetTracker
    {
        private long beforeIndex;
        private long afterIndex;
        private long afterSearch;
        private long maximumObserved;
        private int sampleCount;

        public void CaptureBeforeIndex() => beforeIndex = Capture();

        public void CaptureAfterIndex() => afterIndex = Capture();

        public void CaptureAfterSearch() => afterSearch = Capture();

        public long Capture()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            long bytes = process.WorkingSet64;
            maximumObserved = Math.Max(maximumObserved, bytes);
            sampleCount++;
            return bytes;
        }

        public WorkingSetBenchmarkMetrics ToMetrics()
        {
            using Process process = Process.GetCurrentProcess();
            process.Refresh();
            return new WorkingSetBenchmarkMetrics(
                beforeIndex,
                afterIndex,
                afterSearch,
                maximumObserved,
                process.PeakWorkingSet64,
                sampleCount);
        }
    }
}
