using System.Runtime.Versioning;
using System.Security;

namespace SessionSearch.Infrastructure.Windows;

public enum DirectoryRedirectFailure
{
    None,
    Missing,
    NotRedirect,
    InvalidTarget,
    Unreadable,
}

public sealed record DirectoryRedirectResolution(
    string? TargetPath,
    DirectoryRedirectFailure Failure)
{
    public bool IsResolved => Failure == DirectoryRedirectFailure.None && TargetPath is not null;
}

public interface IDirectoryRedirectReader
{
    DirectoryRedirectResolution ReadTarget(string directoryPath);
}

[SupportedOSPlatform("windows")]
public sealed class PhysicalDirectoryRedirectReader : IDirectoryRedirectReader
{
    public DirectoryRedirectResolution ReadTarget(string directoryPath)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(directoryPath);
            if ((attributes & FileAttributes.Directory) == 0)
            {
                return new DirectoryRedirectResolution(null, DirectoryRedirectFailure.Missing);
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return new DirectoryRedirectResolution(null, DirectoryRedirectFailure.NotRedirect);
            }

            var directory = new DirectoryInfo(directoryPath);
            string? target = directory.LinkTarget;
            if (string.IsNullOrWhiteSpace(target))
            {
                return new DirectoryRedirectResolution(null, DirectoryRedirectFailure.InvalidTarget);
            }

            string fullTarget = Path.IsPathFullyQualified(target)
                ? Path.GetFullPath(target)
                : Path.GetFullPath(target, directory.Parent!.FullName);
            return new DirectoryRedirectResolution(fullTarget, DirectoryRedirectFailure.None);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new DirectoryRedirectResolution(null, DirectoryRedirectFailure.Missing);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                SecurityException or
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
        {
            return new DirectoryRedirectResolution(null, DirectoryRedirectFailure.Unreadable);
        }
    }
}

public enum ExecutableAliasFailure
{
    None,
    NotKnown,
    UnsafeRoot,
    MissingRedirect,
    UnexpectedRedirect,
    InvalidReleaseTarget,
    UnsafeExecutable,
}

public sealed record ExecutableAliasResolution(
    string? CanonicalTargetPath,
    string? CanonicalAliasPath,
    ExecutableAliasFailure Failure)
{
    public bool IsResolved =>
        Failure == ExecutableAliasFailure.None &&
        CanonicalTargetPath is not null &&
        CanonicalAliasPath is not null;
}

public interface IExecutableAliasPolicy
{
    bool IsKnownAlias(TrustedExecutableProfile profile, string candidate);

    ExecutableAliasResolution Resolve(
        TrustedExecutableProfile profile,
        string candidate);
}

public sealed class CodexInstallerAliasPolicy : IExecutableAliasPolicy
{
    private readonly LocalPathPolicy pathPolicy;
    private readonly IDirectoryRedirectReader redirectReader;
    private readonly string aliasPath;
    private readonly string aliasDirectory;
    private readonly string aliasParent;
    private readonly string currentDirectory;
    private readonly string currentBinDirectory;
    private readonly string standaloneRoot;
    private readonly string releasesRoot;

    public CodexInstallerAliasPolicy(
        LocalPathPolicy pathPolicy,
        IDirectoryRedirectReader redirectReader,
        string userProfile,
        string localAppData)
    {
        ArgumentNullException.ThrowIfNull(pathPolicy);
        ArgumentNullException.ThrowIfNull(redirectReader);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppData);

        this.pathPolicy = pathPolicy;
        this.redirectReader = redirectReader;
        aliasParent = RequireSafeLexical(Path.Combine(
            localAppData,
            "Programs",
            "OpenAI",
            "Codex"));
        aliasDirectory = RequireSafeLexical(Path.Combine(aliasParent, "bin"));
        aliasPath = RequireSafeLexical(Path.Combine(aliasDirectory, "codex.exe"));
        standaloneRoot = RequireSafeLexical(Path.Combine(
            userProfile,
            ".codex",
            "packages",
            "standalone"));
        currentDirectory = RequireSafeLexical(Path.Combine(standaloneRoot, "current"));
        currentBinDirectory = RequireSafeLexical(Path.Combine(currentDirectory, "bin"));
        releasesRoot = RequireSafeLexical(Path.Combine(standaloneRoot, "releases"));
    }

    public bool IsKnownAlias(TrustedExecutableProfile profile, string candidate)
    {
        if (profile.Kind != TrustedExecutableKind.Codex ||
            !string.Equals(profile.FileName, "codex.exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        LocalPathValidation lexical = LocalPathPolicy.ValidateLexically(candidate);
        return lexical.IsSafe && PathsEqual(lexical.CanonicalPath!, aliasPath);
    }

    public ExecutableAliasResolution Resolve(
        TrustedExecutableProfile profile,
        string candidate)
    {
        if (!IsKnownAlias(profile, candidate))
        {
            return Failure(ExecutableAliasFailure.NotKnown);
        }

        if (!IsSafeUnredirectedDirectory(aliasParent) ||
            !IsSafeUnredirectedDirectory(standaloneRoot) ||
            !IsSafeUnredirectedDirectory(releasesRoot))
        {
            return Failure(ExecutableAliasFailure.UnsafeRoot);
        }

        DirectoryRedirectResolution aliasRedirect = redirectReader.ReadTarget(aliasDirectory);
        if (!aliasRedirect.IsResolved)
        {
            return Failure(ExecutableAliasFailure.MissingRedirect);
        }

        LocalPathValidation aliasTarget = LocalPathPolicy.ValidateLexically(aliasRedirect.TargetPath);
        if (!aliasTarget.IsSafe || !PathsEqual(aliasTarget.CanonicalPath!, currentBinDirectory))
        {
            return Failure(ExecutableAliasFailure.UnexpectedRedirect);
        }

        DirectoryRedirectResolution currentRedirect = redirectReader.ReadTarget(currentDirectory);
        if (!currentRedirect.IsResolved)
        {
            return Failure(ExecutableAliasFailure.MissingRedirect);
        }

        LocalPathValidation releaseTarget = LocalPathPolicy.ValidateLexically(
            currentRedirect.TargetPath);
        if (!releaseTarget.IsSafe ||
            !IsImmediateChild(releaseTarget.CanonicalPath!, releasesRoot))
        {
            return Failure(ExecutableAliasFailure.InvalidReleaseTarget);
        }

        LocalPathValidation releaseDirectory = pathPolicy.ValidateExistingDirectory(
            releaseTarget.CanonicalPath!,
            releasesRoot);
        if (!releaseDirectory.IsSafe ||
            !PathsEqual(releaseDirectory.CanonicalPath!, releaseTarget.CanonicalPath!))
        {
            return Failure(ExecutableAliasFailure.InvalidReleaseTarget);
        }

        string finalExecutable = Path.Combine(
            releaseDirectory.CanonicalPath!,
            "bin",
            profile.FileName);
        LocalPathValidation executable = pathPolicy.ValidateExistingFile(
            finalExecutable,
            profile.FileName,
            allowReparsePoint: false,
            trustedRoot: releasesRoot);
        if (!executable.IsSafe || !PathsEqual(executable.CanonicalPath!, finalExecutable))
        {
            return Failure(ExecutableAliasFailure.UnsafeExecutable);
        }

        return new ExecutableAliasResolution(
            executable.CanonicalPath,
            aliasPath,
            ExecutableAliasFailure.None);
    }

    private bool IsSafeUnredirectedDirectory(string path)
    {
        LocalPathValidation validation = pathPolicy.ValidateExistingDirectory(path);
        return validation.IsSafe && PathsEqual(validation.CanonicalPath!, path);
    }

    private static bool IsImmediateChild(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return relative.Length > 0 &&
            relative != "." &&
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.Contains(Path.DirectorySeparatorChar) &&
            !relative.Contains(Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string RequireSafeLexical(string path)
    {
        LocalPathValidation lexical = LocalPathPolicy.ValidateLexically(path);
        if (!lexical.IsSafe)
        {
            throw new ArgumentException("An installer path root is not a safe absolute local path.");
        }

        return lexical.CanonicalPath!;
    }

    private static ExecutableAliasResolution Failure(ExecutableAliasFailure failure) =>
        new(null, null, failure);
}
