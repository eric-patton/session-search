using System.ComponentModel;
using System.Diagnostics;
using SessionSearch.Core.Models;

namespace SessionSearch.Infrastructure.Windows;

public sealed record ResumeRequest(
    SessionIdentity Identity,
    string WorkingDirectory,
    ResolvedExecutable ProviderExecutable,
    ResolvedExecutable WindowsTerminal);

public sealed record ResumeCommand(
    SessionIdentity Identity,
    string WorkingDirectory,
    ResolvedExecutable ProviderExecutable);

public interface IResumeCommandRevalidator
{
    bool Revalidate(ResumeCommand command, out string reason);
}

public sealed class ResumeCommandRevalidator(
    LocalPathPolicy pathPolicy,
    TrustedExecutableResolver executableResolver) : IResumeCommandRevalidator
{
    public bool Revalidate(ResumeCommand command, out string reason)
    {
        if (command.Identity.SessionId == Guid.Empty)
        {
            reason = "The immutable session ID is not a valid non-empty UUID.";
            return false;
        }

        LocalPathValidation directory = pathPolicy.ValidateExistingDirectory(
            command.WorkingDirectory);
        if (!directory.IsSafe ||
            !string.Equals(
                directory.CanonicalPath,
                command.WorkingDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            reason = $"The working directory failed revalidation: {directory.Failure}.";
            return false;
        }

        if (!executableResolver.Revalidate(command.ProviderExecutable))
        {
            reason = "The provider executable failed identity or trust revalidation.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

public sealed record ResumePlan(
    SessionIdentity Identity,
    string WorkingDirectory,
    ResolvedExecutable ProviderExecutable,
    ResolvedExecutable WindowsTerminal,
    IReadOnlyList<string> Arguments)
{
    public ProcessStartInfo CreateStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WindowsTerminal.CanonicalPath,
            UseShellExecute = false,
        };

        foreach (string argument in Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}

internal static class ProviderResumeArgumentBuilder
{
    public static string[] Build(SessionIdentity identity)
    {
        string sessionId = identity.SessionId.ToString("D");
        return identity.Provider switch
        {
            SessionProvider.ClaudeCode =>
                ["--dangerously-skip-permissions", "--resume", sessionId],
            SessionProvider.Codex => ["--yolo", "resume", sessionId],
            _ => throw new ArgumentOutOfRangeException(
                nameof(identity),
                "The provider is not supported."),
        };
    }
}

public interface IResumePlanRevalidator
{
    bool Revalidate(ResumePlan plan, out string reason);
}

public sealed class ResumePlanRevalidator(
    LocalPathPolicy pathPolicy,
    TrustedExecutableResolver executableResolver) : IResumePlanRevalidator
{
    public bool Revalidate(ResumePlan plan, out string reason)
    {
        LocalPathValidation directory = pathPolicy.ValidateExistingDirectory(plan.WorkingDirectory);
        if (!directory.IsSafe ||
            !string.Equals(directory.CanonicalPath, plan.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"The working directory failed revalidation: {directory.Failure}.";
            return false;
        }

        if (!executableResolver.Revalidate(plan.ProviderExecutable))
        {
            reason = "The provider executable failed identity or trust revalidation.";
            return false;
        }

        if (!executableResolver.Revalidate(plan.WindowsTerminal))
        {
            reason = "Windows Terminal failed identity or trust revalidation.";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

public sealed class ResumePlanner(IResumePlanRevalidator revalidator)
{
    public ResumePlan Create(ResumeRequest request)
    {
        if (request.Identity.SessionId == Guid.Empty)
        {
            throw new ArgumentException("The immutable session ID must be a non-empty UUID.", nameof(request));
        }

        string[] providerArguments = ProviderResumeArgumentBuilder.Build(request.Identity);

        var arguments = new List<string>
        {
            "-w",
            "0",
            "new-tab",
            "--startingDirectory",
            request.WorkingDirectory,
            request.ProviderExecutable.CanonicalPath,
        };
        arguments.AddRange(providerArguments);

        var plan = new ResumePlan(
            request.Identity,
            request.WorkingDirectory,
            request.ProviderExecutable,
            request.WindowsTerminal,
            arguments);

        if (!revalidator.Revalidate(plan, out string reason))
        {
            throw new InvalidOperationException(reason);
        }

        return plan;
    }

    public IReadOnlyList<ResumePlan> CreateBatch(IEnumerable<ResumeRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var seen = new HashSet<SessionIdentity>();
        var plans = new List<ResumePlan>();
        foreach (ResumeRequest request in requests)
        {
            if (seen.Add(request.Identity))
            {
                plans.Add(Create(request));
            }
        }

        return plans;
    }
}

public static class PowerShellCommandFormatter
{
    public static string Format(ResumePlan plan) => Format(new ResumeCommand(
        plan.Identity,
        plan.WorkingDirectory,
        plan.ProviderExecutable));

    public static string Format(ResumeCommand command)
    {
        if (command.Identity.SessionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The immutable session ID must be a non-empty UUID.",
                nameof(command));
        }

        string[] providerArguments = ProviderResumeArgumentBuilder.Build(command.Identity);

        return $"Set-Location -LiteralPath {Quote(command.WorkingDirectory)}; & {Quote(command.ProviderExecutable.CanonicalPath)} {string.Join(' ', providerArguments.Select(Quote))}";
    }

    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return string.Concat(
            '\'',
            value.Replace("'", "''", StringComparison.Ordinal),
            '\'');
    }
}

public sealed record ProcessLaunchResult(bool Started, int? ProcessId, string? Error);

public interface IProcessLauncher
{
    ValueTask<ProcessLaunchResult> LaunchAsync(
        ResumePlan plan,
        CancellationToken cancellationToken = default);
}

public sealed class RecordingProcessLauncher(IResumePlanRevalidator revalidator) : IProcessLauncher
{
    private readonly List<ProcessStartInfo> starts = [];

    public IReadOnlyList<ProcessStartInfo> Starts => starts;

    public ValueTask<ProcessLaunchResult> LaunchAsync(
        ResumePlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!revalidator.Revalidate(plan, out string reason))
        {
            return ValueTask.FromResult(new ProcessLaunchResult(false, null, reason));
        }

        starts.Add(plan.CreateStartInfo());
        return ValueTask.FromResult(new ProcessLaunchResult(true, null, null));
    }
}

public sealed class SystemProcessLauncher(IResumePlanRevalidator revalidator) : IProcessLauncher
{
    public ValueTask<ProcessLaunchResult> LaunchAsync(
        ResumePlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!revalidator.Revalidate(plan, out string reason))
        {
            return ValueTask.FromResult(new ProcessLaunchResult(false, null, reason));
        }

        try
        {
            using Process? process = Process.Start(plan.CreateStartInfo());
            return ValueTask.FromResult(
                process is null
                    ? new ProcessLaunchResult(false, null, "Process creation returned no process.")
                    : new ProcessLaunchResult(true, process.Id, null));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or Win32Exception or FileNotFoundException)
        {
            return ValueTask.FromResult(
                new ProcessLaunchResult(false, null, exception.GetType().Name));
        }
    }
}
