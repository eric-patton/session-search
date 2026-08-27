using System.Diagnostics;

namespace SessionSearch.Benchmarks;

internal sealed class SchedulerResponsivenessProbe : IAsyncDisposable
{
    private const int MaximumSamples = 100_000;
    private readonly int intervalMilliseconds;
    private readonly CancellationTokenSource cancellation;
    private readonly Task worker;
    private readonly List<double> delays = [];
    private bool wasCapped;
    private bool completed;

    public SchedulerResponsivenessProbe(
        int intervalMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(intervalMilliseconds);
        this.intervalMilliseconds = intervalMilliseconds;
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        worker = Task.Run(RunAsync, CancellationToken.None);
    }

    public async ValueTask<ResponsivenessBenchmarkMetrics> CompleteAsync()
    {
        if (!completed)
        {
            completed = true;
            await cancellation.CancelAsync().ConfigureAwait(false);
            await worker.ConfigureAwait(false);
        }

        List<double> captured = [.. delays];
        return new ResponsivenessBenchmarkMetrics(
            "process-scheduler-only-no-winforms-ui",
            intervalMilliseconds,
            captured.Count,
            Percentile(captured, 0.50),
            Percentile(captured, 0.95),
            captured.Count == 0 ? 0 : Round(captured.Max()),
            captured.Count(delay => delay > 100),
            wasCapped);
    }

    public async ValueTask DisposeAsync()
    {
        _ = await CompleteAsync().ConfigureAwait(false);
        cancellation.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (delays.Count < MaximumSamples)
            {
                long started = Stopwatch.GetTimestamp();
                await Task.Delay(
                    intervalMilliseconds,
                    cancellation.Token).ConfigureAwait(false);
                double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                delays.Add(Math.Max(0, elapsed - intervalMilliseconds));
            }

            wasCapped = true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

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
}
