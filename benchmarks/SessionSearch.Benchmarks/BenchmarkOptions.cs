namespace SessionSearch.Benchmarks;

internal sealed record BenchmarkOptions(
    string ClaudeRoot,
    string CodexRoot,
    string DataRoot,
    string OutputPath,
    int WarmupIterations,
    int MeasuredIterations,
    int IdleWaitSeconds,
    int IdleSampleIntervalSeconds,
    bool ProfileStages,
    bool ShowHelp)
{
    private const int DefaultWarmups = 5;
    private const int DefaultIterations = 30;
    private const int DefaultIdleWaitSeconds = 30;
    private const int DefaultIdleSampleIntervalSeconds = 5;

    public static string HelpText =>
        "Usage: SessionSearch.Benchmarks --claude-root <directory> --codex-root <directory> "
        + "--data-root <isolated-directory> --output <report.json> [--warmups <0-20>] "
        + "[--iterations <5-1000>] [--idle-wait <0-300>] [--idle-interval <0-60>] "
        + "[--profile-stages]";

    public static BenchmarkOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(argument => string.Equals(
            argument,
            "--help",
            StringComparison.OrdinalIgnoreCase)))
        {
            return new BenchmarkOptions(
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                DefaultWarmups,
                DefaultIterations,
                DefaultIdleWaitSeconds,
                DefaultIdleSampleIntervalSeconds,
                ProfileStages: false,
                ShowHelp: true);
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        bool profileStages = false;
        for (int index = 0; index < args.Length; index++)
        {
            string argument = args[index];
            if (string.Equals(argument, "--profile-stages", StringComparison.OrdinalIgnoreCase))
            {
                profileStages = true;
                continue;
            }

            if (!KnownValueOptions.Contains(argument))
            {
                throw new BenchmarkUsageException("An unknown option was supplied.");
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new BenchmarkUsageException($"{argument} requires a value.");
            }

            if (!values.TryAdd(argument, args[++index]))
            {
                throw new BenchmarkUsageException($"{argument} was supplied more than once.");
            }
        }

        string claudeRoot = RequiredPath(values, "--claude-root");
        string codexRoot = RequiredPath(values, "--codex-root");
        string dataRoot = RequiredPath(values, "--data-root");
        string outputPath = RequiredPath(values, "--output");
        int warmups = OptionalInteger(values, "--warmups", DefaultWarmups, 0, 20);
        int iterations = OptionalInteger(values, "--iterations", DefaultIterations, 5, 1_000);
        int idleWait = OptionalInteger(
            values,
            "--idle-wait",
            DefaultIdleWaitSeconds,
            0,
            300);
        int idleInterval = OptionalInteger(
            values,
            "--idle-interval",
            DefaultIdleSampleIntervalSeconds,
            0,
            60);

        return new BenchmarkOptions(
            claudeRoot,
            codexRoot,
            dataRoot,
            outputPath,
            warmups,
            iterations,
            idleWait,
            idleInterval,
            profileStages,
            ShowHelp: false);
    }

    private static HashSet<string> KnownValueOptions { get; } = new(
        [
            "--claude-root",
            "--codex-root",
            "--data-root",
            "--output",
            "--warmups",
            "--iterations",
            "--idle-wait",
            "--idle-interval",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static string RequiredPath(
        Dictionary<string, string> values,
        string option)
    {
        if (!values.TryGetValue(option, out string? value)
            || string.IsNullOrWhiteSpace(value))
        {
            throw new BenchmarkUsageException($"{option} is required.");
        }

        if (!Path.IsPathFullyQualified(value))
        {
            throw new BenchmarkUsageException($"{option} must be an absolute path.");
        }

        return Path.GetFullPath(value);
    }

    private static int OptionalInteger(
        Dictionary<string, string> values,
        string option,
        int defaultValue,
        int minimum,
        int maximum)
    {
        if (!values.TryGetValue(option, out string? value))
        {
            return defaultValue;
        }

        if (!int.TryParse(value, out int parsed) || parsed < minimum || parsed > maximum)
        {
            throw new BenchmarkUsageException(
                $"{option} must be an integer from {minimum} through {maximum}.");
        }

        return parsed;
    }
}

internal sealed class BenchmarkUsageException(string message) : Exception(message);
