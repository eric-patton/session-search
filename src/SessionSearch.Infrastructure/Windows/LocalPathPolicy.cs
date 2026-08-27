using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SessionSearch.Infrastructure.Windows;

public enum LocalPathFailure
{
    None,
    Empty,
    Relative,
    Remote,
    DeviceNamespace,
    AlternateDataStream,
    InvalidCharacter,
    ReservedDeviceName,
    CanonicalizationFailed,
    NonFixedDrive,
    Missing,
    ReparsePoint,
    EscapesTrustedRoot,
}

public sealed record LocalPathValidation(
    string? CanonicalPath,
    LocalPathFailure Failure,
    string Reason)
{
    public bool IsSafe => Failure == LocalPathFailure.None;
}

public interface IWindowsPathProbe
{
    DriveType GetDriveType(string driveRoot);

    bool DirectoryExists(string path);

    bool FileExists(string path);

    bool HasReparsePoint(string path);

    string GetFinalPath(string path, bool directory);
}

public sealed class LocalPathPolicy(IWindowsPathProbe probe)
{
    private static readonly HashSet<string> ReservedDeviceNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "CLOCK$",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    };

    public LocalPathValidation ValidateExistingDirectory(
        string path,
        string? trustedRoot = null) =>
        Validate(path, directory: true, expectedFileName: null, trustedRoot, allowReparsePoint: false);

    public LocalPathValidation ValidateExistingFile(
        string path,
        string expectedFileName,
        bool allowReparsePoint = false,
        string? trustedRoot = null) =>
        Validate(path, directory: false, expectedFileName, trustedRoot, allowReparsePoint);

    public static LocalPathValidation ValidateLexically(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Failure(LocalPathFailure.Empty, "The path is empty.");
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return Failure(LocalPathFailure.DeviceNamespace, "Device namespace paths are not allowed.");
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.StartsWith("//", StringComparison.Ordinal))
        {
            return Failure(LocalPathFailure.Remote, "Remote paths are not allowed.");
        }

        if (path.Length < 3 ||
            !IsAsciiLetter(path[0]) ||
            path[1] != ':' ||
            (path[2] != '\\' && path[2] != '/'))
        {
            return Failure(LocalPathFailure.Relative, "The path must be an absolute drive path.");
        }

        if (path.AsSpan(2).Contains(':'))
        {
            return Failure(LocalPathFailure.AlternateDataStream, "Alternate data streams are not allowed.");
        }

        if (path.Any(character =>
                char.IsControl(character) || character is '*' or '?' or '"' or '<' or '>' or '|'))
        {
            return Failure(LocalPathFailure.InvalidCharacter, "The path contains an invalid character.");
        }

        foreach (string component in path[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            string deviceCandidate = component.TrimEnd(' ', '.');
            int extensionSeparator = deviceCandidate.IndexOf('.');
            if (extensionSeparator >= 0)
            {
                deviceCandidate = deviceCandidate[..extensionSeparator];
            }

            if (ReservedDeviceNames.Contains(deviceCandidate))
            {
                return Failure(LocalPathFailure.ReservedDeviceName, "Reserved device names are not allowed.");
            }
        }

        try
        {
            string canonicalPath = Path.GetFullPath(path);
            return new LocalPathValidation(canonicalPath, LocalPathFailure.None, string.Empty);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(LocalPathFailure.CanonicalizationFailed, "The path cannot be canonicalized.");
        }
    }

    private LocalPathValidation Validate(
        string path,
        bool directory,
        string? expectedFileName,
        string? trustedRoot,
        bool allowReparsePoint)
    {
        LocalPathValidation lexical = ValidateLexically(path);
        if (!lexical.IsSafe)
        {
            return lexical;
        }

        string canonicalPath = lexical.CanonicalPath!;
        if (expectedFileName is not null &&
            !string.Equals(Path.GetFileName(canonicalPath), expectedFileName, StringComparison.OrdinalIgnoreCase))
        {
            return Failure(LocalPathFailure.InvalidCharacter, "The executable filename is not expected.");
        }

        string driveRoot = Path.GetPathRoot(canonicalPath)!;
        if (probe.GetDriveType(driveRoot) != DriveType.Fixed)
        {
            return Failure(LocalPathFailure.NonFixedDrive, "The path is not on a local fixed drive.");
        }

        bool exists = directory ? probe.DirectoryExists(canonicalPath) : probe.FileExists(canonicalPath);
        if (!exists)
        {
            return Failure(LocalPathFailure.Missing, "The path does not exist.");
        }

        if (!allowReparsePoint && probe.HasReparsePoint(canonicalPath))
        {
            return Failure(LocalPathFailure.ReparsePoint, "The path crosses a reparse point.");
        }

        string finalPath;
        try
        {
            finalPath = probe.GetFinalPath(canonicalPath, directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Failure(LocalPathFailure.CanonicalizationFailed, "The final handle path cannot be resolved.");
        }

        LocalPathValidation finalLexical = ValidateLexically(finalPath);
        if (!finalLexical.IsSafe)
        {
            return finalLexical;
        }

        string finalCanonicalPath = finalLexical.CanonicalPath!;
        if (expectedFileName is not null &&
            !string.Equals(
                Path.GetFileName(finalCanonicalPath),
                expectedFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            return Failure(LocalPathFailure.InvalidCharacter, "The final executable filename is not expected.");
        }

        if (trustedRoot is not null)
        {
            LocalPathValidation rootLexical = ValidateLexically(trustedRoot);
            if (!rootLexical.IsSafe || !IsWithinRoot(finalCanonicalPath, rootLexical.CanonicalPath!))
            {
                return Failure(LocalPathFailure.EscapesTrustedRoot, "The final path escapes the trusted root.");
            }
        }

        return new LocalPathValidation(finalCanonicalPath, LocalPathFailure.None, string.Empty);
    }

    private static bool IsWithinRoot(string path, string root)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static LocalPathValidation Failure(LocalPathFailure failure, string reason) =>
        new(null, failure, reason);
}

[SupportedOSPlatform("windows")]
public sealed class PhysicalWindowsPathProbe : IWindowsPathProbe
{
    public DriveType GetDriveType(string driveRoot) => new DriveInfo(driveRoot).DriveType;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public bool HasReparsePoint(string path)
    {
        string root = Path.GetPathRoot(path)!;
        string current = root;
        string relative = Path.GetRelativePath(root, path);
        foreach (string component in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    public string GetFinalPath(string path, bool directory)
    {
        const uint fileFlagBackupSemantics = 0x02000000;
        uint flags = directory ? fileFlagBackupSemantics : 0;
        using SafeFileHandle handle = NativeMethods.CreateFile(
            path,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            flags,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var buffer = new char[512];
        uint length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (length >= buffer.Length)
        {
            buffer = new char[checked((int)length + 1)];
            length = NativeMethods.GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        string finalPath = new(buffer, 0, checked((int)length));
        return finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)
            ? @"\\" + finalPath[8..]
            : finalPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
                ? finalPath[4..]
                : finalPath;
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            FileShare shareMode,
            IntPtr securityAttributes,
            FileMode creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] char[] filePath,
            uint filePathLength,
            uint flags);
    }
}
