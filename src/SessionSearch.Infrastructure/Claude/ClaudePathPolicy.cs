using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SessionSearch.Infrastructure.Claude;

internal static class ClaudePathPolicy
{
    private const string ExtendedPathPrefix = @"\\?\";
    private const string DevicePathPrefix = @"\\.\";

    public static bool TryResolveProjectsRoot(string configuredPath, out string projectsRoot)
    {
        projectsRoot = string.Empty;
        if (!TryNormalizeLocalFixedPath(configuredPath, out string normalizedRoot))
        {
            return false;
        }

        string candidate = string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(normalizedRoot)),
            "projects",
            StringComparison.OrdinalIgnoreCase)
            ? normalizedRoot
            : Path.Combine(normalizedRoot, "projects");

        if (HasReparsePoint(candidate) || !Directory.Exists(candidate))
        {
            return false;
        }

        projectsRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return true;
    }

    public static bool TryValidateSource(
        string projectsRoot,
        string sourcePath,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (!TryNormalizeLocalFixedPath(sourcePath, out string normalizedSource)
            || !IsStrictDescendant(projectsRoot, normalizedSource)
            || HasReparsePoint(normalizedSource))
        {
            return false;
        }

        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                normalizedSource,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.SequentialScan);

            string finalPath = OperatingSystem.IsWindows()
                ? ClaudeWindowsPath.GetFinalPath(handle)
                : normalizedSource;
            if (!TryNormalizeLocalFixedPath(finalPath, out string normalizedFinal)
                || !IsStrictDescendant(projectsRoot, normalizedFinal))
            {
                return false;
            }

            canonicalPath = normalizedFinal;
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryRevalidateSource(
        string canonicalPath,
        string relativePath,
        out string validatedPath)
    {
        validatedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        string normalizedRelative = relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedRelative.Split(Path.DirectorySeparatorChar)
            .Any(segment => segment is "" or "." or ".."))
        {
            return false;
        }

        string normalizedCanonical;
        try
        {
            normalizedCanonical = Path.GetFullPath(canonicalPath);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException)
        {
            return false;
        }

        string suffix = Path.DirectorySeparatorChar + normalizedRelative;
        if (!normalizedCanonical.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string projectsRoot = normalizedCanonical[..^suffix.Length];
        return TryValidateSource(projectsRoot, normalizedCanonical, out validatedPath);
    }

    public static bool TryValidateDirectory(string projectsRoot, string directoryPath)
    {
        return TryNormalizeLocalFixedPath(directoryPath, out string normalizedDirectory)
            && IsStrictDescendant(projectsRoot, normalizedDirectory)
            && !HasReparsePoint(normalizedDirectory)
            && Directory.Exists(normalizedDirectory);
    }

    public static bool IsStrictDescendant(string rootPath, string candidatePath)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string candidate = Path.GetFullPath(candidatePath);
        return candidate.StartsWith(
            root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeLocalFixedPath(string path, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path)
            || path.StartsWith(ExtendedPathPrefix, StringComparison.Ordinal)
            || path.StartsWith(DevicePathPrefix, StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            string fullPath = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(fullPath);
            if (root is null
                || root.Length != 3
                || root[1] != Path.VolumeSeparatorChar
                || fullPath.AsSpan(root.Length).Contains(Path.VolumeSeparatorChar))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                return false;
            }

            normalizedPath = fullPath.Length == root.Length
                ? fullPath
                : Path.TrimEndingDirectorySeparator(fullPath);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath)!;
            string current = root;
            ReadOnlySpan<char> relative = fullPath.AsSpan(root.Length);

            foreach (Range range in relative.SplitAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                ReadOnlySpan<char> segment = relative[range];
                if (segment.IsEmpty)
                {
                    continue;
                }

                current = Path.Combine(current, segment.ToString());
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

[SupportedOSPlatform("windows")]
internal static partial class ClaudeWindowsPath
{
    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public static string GetFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0)
        {
            throw new IOException("The source final path could not be resolved.");
        }

        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length)
            {
                throw new IOException("The source final path could not be resolved.");
            }
        }

        string finalPath = new(buffer, 0, checked((int)length));
        if (finalPath.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + finalPath[ExtendedUncPrefix.Length..];
        }

        return finalPath.StartsWith(ExtendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? finalPath[ExtendedPrefix.Length..]
            : finalPath;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint flags);
}
