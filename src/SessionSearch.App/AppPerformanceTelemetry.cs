using System.Diagnostics;

namespace SessionSearch.App;

internal sealed class AppPerformanceTelemetry
{
    private const int MaximumSamplesPerGroup = 100;
    private static readonly string[] DatabaseSuffixes =
        [string.Empty, "-wal", "-shm"];
    private readonly Stopwatch uptime = Stopwatch.StartNew();
    private readonly Queue<double> metadataQueryMilliseconds = new();
    private readonly Queue<double> transcriptQueryMilliseconds = new();
    private double? firstUsableRowsMilliseconds;
    private double? firstMetadataReadyMilliseconds;
    private double? latestQueryMilliseconds;
    private string progress = "Starting";
    private bool hasUnmappedClaudeActivity;

    public TimeSpan Elapsed => uptime.Elapsed;

    public void RecordFirstUsableRows()
    {
        firstUsableRowsMilliseconds ??= uptime.Elapsed.TotalMilliseconds;
    }

    public void RecordFirstMetadataReady()
    {
        firstMetadataReadyMilliseconds ??= uptime.Elapsed.TotalMilliseconds;
    }

    public void RecordQuery(double milliseconds, bool transcriptCapable)
    {
        latestQueryMilliseconds = milliseconds;
        Queue<double> samples = transcriptCapable
            ? transcriptQueryMilliseconds
            : metadataQueryMilliseconds;
        samples.Enqueue(milliseconds);
        while (samples.Count > MaximumSamplesPerGroup)
        {
            samples.Dequeue();
        }
    }

    public void SetProgress(string value)
    {
        progress = value;
    }

    public void SetUnmappedClaudeActivity(bool value)
    {
        hasUnmappedClaudeActivity = value;
    }

    public string FormatStatus(string? databasePath)
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long indexBytes = databasePath is null ? 0 : ReadIndexBytes(databasePath);
        return $"""
            Local performance telemetry

            First usable rows: {FormatMilliseconds(firstUsableRowsMilliseconds)}
            First metadata ready: {FormatMilliseconds(firstMetadataReadyMilliseconds)}
            Latest query: {FormatMilliseconds(latestQueryMilliseconds)}
            Browse and metadata rolling p95: {FormatMilliseconds(Percentile95(metadataQueryMilliseconds))}
            Transcript-capable rolling p95: {FormatMilliseconds(Percentile95(transcriptQueryMilliseconds))}
            Current working set: {FormatBytes(process.WorkingSet64)}
            App index size: {FormatBytes(indexBytes)}
            Index progress: {progress}
            Claude activity warning: {(hasUnmappedClaudeActivity ? "Some live Claude activity could not be mapped to a session." : "None")}

            Telemetry is process-local and excludes queries, paths, session IDs, transcript text, and commands.
            """;
    }

    private static double? Percentile95(IEnumerable<double> values)
    {
        double[] sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        int index = Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95) - 1);
        return sorted[index];
    }

    private static string FormatMilliseconds(double? value) =>
        value.HasValue ? $"{value.Value:0.0} ms" : "Pending";

    private static long ReadIndexBytes(string databasePath)
    {
        try
        {
            long total = 0;
            foreach (string suffix in DatabaseSuffixes)
            {
                FileInfo file = new(databasePath + suffix);
                file.Refresh();
                if (file.Exists)
                {
                    total = checked(total + file.Length);
                }
            }

            return total;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024L * 1024)
        {
            return $"{bytes / 1024d:0.0} KiB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024):0.0} MiB";
        }

        return $"{bytes / (1024d * 1024 * 1024):0.0} GiB";
    }
}
