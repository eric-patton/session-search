using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace SessionSearch.Infrastructure.Windows;

public sealed record ProcessCommandLineReadResult(
    IReadOnlyList<string> ResumeArguments,
    bool IsComplete);

public interface IProcessCommandLineReader
{
    ProcessCommandLineReadResult Read(int processId);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsProcessCommandLineReader : IProcessCommandLineReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private const int StatusBufferTooSmall = unchecked((int)0xC0000023);
    private const int MaxCommandLineBytes = 128 * 1024;

    public ProcessCommandLineReadResult Read(int processId)
    {
        if (processId <= 0)
        {
            return Incomplete();
        }

        using SafeProcessHandle process = NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (process.IsInvalid)
        {
            return Incomplete();
        }

        int status = NativeMethods.NtQueryInformationProcess(
            process,
            ProcessCommandLineInformation,
            IntPtr.Zero,
            0,
            out int requiredBytes);
        if (status is not StatusInfoLengthMismatch and not StatusBufferTooSmall ||
            requiredBytes < Marshal.SizeOf<UnicodeString>() ||
            requiredBytes > MaxCommandLineBytes)
        {
            return Incomplete();
        }

        IntPtr buffer = Marshal.AllocHGlobal(requiredBytes);
        try
        {
            status = NativeMethods.NtQueryInformationProcess(
                process,
                ProcessCommandLineInformation,
                buffer,
                requiredBytes,
                out int returnedBytes);
            if (status < 0 ||
                returnedBytes < Marshal.SizeOf<UnicodeString>() ||
                returnedBytes > requiredBytes)
            {
                return Incomplete();
            }

            UnicodeString commandLine = Marshal.PtrToStructure<UnicodeString>(buffer);
            if (commandLine.Length == 0)
            {
                return new ProcessCommandLineReadResult([], true);
            }

            if ((commandLine.Length & 1) != 0 ||
                commandLine.Length > commandLine.MaximumLength ||
                !PointsInsideBuffer(
                    buffer,
                    requiredBytes,
                    commandLine.Buffer,
                    commandLine.Length))
            {
                return Incomplete();
            }

            string? value = Marshal.PtrToStringUni(
                commandLine.Buffer,
                commandLine.Length / sizeof(char));
            if (string.IsNullOrWhiteSpace(value))
            {
                return new ProcessCommandLineReadResult([], true);
            }

            string[]? arguments = ParseArguments(value);
            return arguments is null
                ? Incomplete()
                : new ProcessCommandLineReadResult(
                    ExtractResumeArguments(arguments),
                    true);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool PointsInsideBuffer(
        IntPtr buffer,
        int bufferLength,
        IntPtr value,
        int valueLength)
    {
        long start = buffer.ToInt64();
        long end = start + bufferLength;
        long valueStart = value.ToInt64();
        long valueEnd = valueStart + valueLength;
        return valueStart >= start && valueEnd >= valueStart && valueEnd <= end;
    }

    private static string[]? ParseArguments(string commandLine)
    {
        IntPtr argumentPointers = NativeMethods.CommandLineToArgv(
            commandLine,
            out int argumentCount);
        if (argumentPointers == IntPtr.Zero || argumentCount <= 0)
        {
            return null;
        }

        try
        {
            var arguments = new string[argumentCount];
            for (int index = 0; index < argumentCount; index++)
            {
                IntPtr argumentPointer = Marshal.ReadIntPtr(
                    argumentPointers,
                    index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(argumentPointer) ?? string.Empty;
            }

            return arguments;
        }
        finally
        {
            NativeMethods.LocalFree(argumentPointers);
        }
    }

    private static IReadOnlyList<string> ExtractResumeArguments(
        string[] arguments)
    {
        for (int index = 1; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--resume", StringComparison.Ordinal) &&
                index + 1 < arguments.Length &&
                Guid.TryParseExact(arguments[index + 1], "D", out Guid separateSessionId) &&
                separateSessionId != Guid.Empty)
            {
                return ["--resume", separateSessionId.ToString("D")];
            }

            if (argument.StartsWith("--resume=", StringComparison.Ordinal) &&
                Guid.TryParseExact(argument[9..], "D", out Guid inlineSessionId) &&
                inlineSessionId != Guid.Empty)
            {
                return [$"--resume={inlineSessionId:D}"];
            }
        }

        return [];
    }

    private static ProcessCommandLineReadResult Incomplete() => new([], false);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct UnicodeString
    {
        internal readonly ushort Length;
        internal readonly ushort MaximumLength;
        internal readonly IntPtr Buffer;
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("ntdll.dll")]
        internal static extern int NtQueryInformationProcess(
            SafeProcessHandle processHandle,
            int processInformationClass,
            IntPtr processInformation,
            int processInformationLength,
            out int returnLength);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode)]
        internal static extern IntPtr CommandLineToArgv(
            string commandLine,
            out int argumentCount);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr memory);
    }
}
