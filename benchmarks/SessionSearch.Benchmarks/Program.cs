using System.Text.Json;

namespace SessionSearch.Benchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            BenchmarkOptions options = BenchmarkOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(BenchmarkOptions.HelpText);
                return 0;
            }

            BenchmarkReport report = await BenchmarkRunner.RunAsync(
                options,
                CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine(JsonSerializer.Serialize(
                report,
                BenchmarkJson.Options));
            return report.Index.IsPartial ? 2 : 0;
        }
        catch (BenchmarkUsageException exception)
        {
            Console.Error.WriteLine($"Benchmark arguments are invalid: {exception.Message}");
            Console.Error.WriteLine(BenchmarkOptions.HelpText);
            return 64;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Benchmark canceled.");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"Benchmark failed safely ({exception.GetType().Name}). No source details were emitted.");
            return 1;
        }
    }
}
