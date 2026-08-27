using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace SessionSearch.Infrastructure.Windows;

public enum TrustedExecutableKind
{
    ClaudeCode,
    Codex,
    WindowsTerminal,
}

public sealed record TrustedExecutableProfile(
    TrustedExecutableKind Kind,
    string FileName,
    IReadOnlyList<string> ExpectedPublishers);

public sealed record ExecutableTrustVerification(
    bool IsAuthenticodeTrusted,
    string? Publisher,
    string? FileIdentity,
    bool IsVerifiedPackageAlias = false,
    string? Failure = null);

public interface IExecutableTrustVerifier
{
    ExecutableTrustVerification Verify(string canonicalPath, TrustedExecutableProfile profile);
}

public sealed record ResolvedExecutable(
    TrustedExecutableProfile Profile,
    string CanonicalPath,
    string FileIdentity,
    string Publisher,
    bool IsVerifiedPackageAlias);

public sealed record ExecutableResolution(
    ResolvedExecutable? Executable,
    string Reason)
{
    public bool IsResolved => Executable is not null;
}

public sealed class TrustedExecutableResolver(
    LocalPathPolicy pathPolicy,
    IExecutableTrustVerifier trustVerifier)
{
    public ExecutableResolution Resolve(
        TrustedExecutableProfile profile,
        IEnumerable<string> installedCandidates,
        string? explicitPath = null)
    {
        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(explicitPath)
            ? installedCandidates
            : new[] { explicitPath }.Concat(installedCandidates);

        var failures = new List<string>();
        foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            bool terminalAlias = profile.Kind == TrustedExecutableKind.WindowsTerminal &&
                IsWindowsTerminalAlias(candidate);
            LocalPathValidation validation = pathPolicy.ValidateExistingFile(
                candidate,
                profile.FileName,
                allowReparsePoint: terminalAlias);

            if (!validation.IsSafe)
            {
                failures.Add($"{candidate}: {validation.Failure}");
                continue;
            }

            ExecutableTrustVerification verification = trustVerifier.Verify(
                validation.CanonicalPath!,
                profile);
            if (!verification.IsAuthenticodeTrusted ||
                string.IsNullOrWhiteSpace(verification.Publisher) ||
                string.IsNullOrWhiteSpace(verification.FileIdentity) ||
                !profile.ExpectedPublishers.Contains(
                    verification.Publisher,
                    StringComparer.OrdinalIgnoreCase) ||
                (profile.Kind == TrustedExecutableKind.WindowsTerminal &&
                    !verification.IsVerifiedPackageAlias))
            {
                failures.Add($"{candidate}: signature or publisher validation failed");
                continue;
            }

            return new ExecutableResolution(
                new ResolvedExecutable(
                    profile,
                    validation.CanonicalPath!,
                    verification.FileIdentity,
                    verification.Publisher,
                    verification.IsVerifiedPackageAlias),
                string.Empty);
        }

        return new ExecutableResolution(
            null,
            failures.Count == 0
                ? $"No {profile.FileName} candidate was provided."
                : string.Join(" | ", failures));
    }

    public bool Revalidate(ResolvedExecutable executable)
    {
        ExecutableResolution resolution = Resolve(
            executable.Profile,
            [executable.CanonicalPath]);
        return resolution.Executable is not null &&
            string.Equals(
                resolution.Executable.FileIdentity,
                executable.FileIdentity,
                StringComparison.Ordinal);
    }

    private static bool IsWindowsTerminalAlias(string path)
    {
        LocalPathValidation lexical = LocalPathPolicy.ValidateLexically(path);
        if (!lexical.IsSafe)
        {
            return false;
        }

        string normalized = lexical.CanonicalPath!;
        return normalized.EndsWith(
            @"\Microsoft\WindowsApps\wt.exe",
            StringComparison.OrdinalIgnoreCase);
    }
}

public static class InstalledExecutablePatterns
{
    public static IReadOnlyList<string> GetCandidates(
        TrustedExecutableKind kind,
        string userProfile,
        string localAppData,
        string? programFiles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);

        return kind switch
        {
            TrustedExecutableKind.ClaudeCode =>
            [
                Path.Combine(userProfile, ".local", "bin", "claude.exe"),
                Path.Combine(localAppData, "Anthropic", "Claude", "claude.exe"),
            ],
            TrustedExecutableKind.Codex =>
            [
                Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
                Path.Combine(userProfile, ".codex", "bin", "codex.exe"),
                Path.Combine(localAppData, "OpenAI", "Codex", "codex.exe"),
            ],
            TrustedExecutableKind.WindowsTerminal => GetWindowsTerminalCandidates(
                localAppData,
                programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string[] GetWindowsTerminalCandidates(
        string localAppData,
        string programFiles)
    {
        var candidates = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            candidates.AddRange(GetRegisteredWindowsTerminalCandidates());
        }

        string packageRoot = Path.Combine(programFiles, "WindowsApps");
        try
        {
            foreach (string pattern in new[]
            {
                "Microsoft.WindowsTerminal_*",
                "Microsoft.WindowsTerminalPreview_*",
            })
            {
                foreach (string packageDirectory in Directory.EnumerateDirectories(
                    packageRoot,
                    pattern,
                    SearchOption.TopDirectoryOnly))
                {
                    string packageBinary = Path.Combine(packageDirectory, "wt.exe");
                    if (File.Exists(packageBinary))
                    {
                        candidates.Add(packageBinary);
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        candidates.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(right, left));
        candidates.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps", "wt.exe"));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static List<string> GetRegisteredWindowsTerminalCandidates()
    {
        const string packagesKey =
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        var candidates = new List<string>();
        try
        {
            using RegistryKey? packages = Registry.CurrentUser.OpenSubKey(packagesKey);
            if (packages is null)
            {
                return candidates;
            }

            foreach (string packageName in packages.GetSubKeyNames())
            {
                if (!packageName.StartsWith(
                        "Microsoft.WindowsTerminal_",
                        StringComparison.OrdinalIgnoreCase) &&
                    !packageName.StartsWith(
                        "Microsoft.WindowsTerminalPreview_",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using RegistryKey? package = packages.OpenSubKey(packageName);
                if (package?.GetValue("PackageRootFolder") is not string packageRoot ||
                    string.IsNullOrWhiteSpace(packageRoot))
                {
                    continue;
                }

                string packageBinary = Path.Combine(packageRoot, "wt.exe");
                if (File.Exists(packageBinary))
                {
                    candidates.Add(packageBinary);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
        }

        return candidates;
    }
}
