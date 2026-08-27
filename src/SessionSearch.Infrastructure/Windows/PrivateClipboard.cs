using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SessionSearch.Infrastructure.Windows;

public enum PrivateClipboardFailure
{
    None,
    ClipboardBusy,
    FormatRegistrationFailed,
    EmptyClipboardFailed,
    MemoryAllocationFailed,
    SetClipboardDataFailed,
    CloseClipboardFailed,
    NativeFailure,
}

public sealed record PrivateClipboardResult(
    bool Success,
    PrivateClipboardFailure Failure,
    int Attempts,
    string Message);

public interface IPrivateClipboard
{
    Task<PrivateClipboardResult> WriteTextAsync(
        string text,
        CancellationToken cancellationToken = default);
}

public interface IPrivateClipboardNativeApi
{
    uint RegisterClipboardFormat(string formatName);

    bool OpenClipboard(IntPtr ownerWindow);

    bool EmptyClipboard();

    IntPtr AllocateGlobalMemory(int byteCount);

    void WriteGlobalMemory(IntPtr memory, byte[] bytes);

    bool SetClipboardData(uint format, IntPtr memory);

    void FreeGlobalMemory(IntPtr memory);

    bool CloseClipboard();
}

public interface IStaThreadRunner
{
    Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken);
}

public interface IClipboardRetryDelay
{
    void Wait(int failedAttempt, CancellationToken cancellationToken);
}

public sealed class PrivateClipboard(
    IPrivateClipboardNativeApi nativeApi,
    IStaThreadRunner staThreadRunner,
    IClipboardRetryDelay retryDelay,
    int maximumAttempts = 5) : IPrivateClipboard
{
    public const string ClipboardHistoryFormatName = "CanIncludeInClipboardHistory";
    public const string CloudClipboardFormatName = "CanUploadToCloudClipboard";

    private const uint UnicodeTextFormat = 13;

    public Task<PrivateClipboardResult> WriteTextAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumAttempts < 1)
        {
            throw new InvalidOperationException("The clipboard attempt limit must be positive.");
        }

        return staThreadRunner.RunAsync(
            () => WriteOnStaThread(text, cancellationToken),
            cancellationToken);
    }

    private PrivateClipboardResult WriteOnStaThread(
        string text,
        CancellationToken cancellationToken)
    {
        try
        {
            uint historyFormat = nativeApi.RegisterClipboardFormat(ClipboardHistoryFormatName);
            uint cloudFormat = nativeApi.RegisterClipboardFormat(CloudClipboardFormatName);
            if (historyFormat == 0 || cloudFormat == 0)
            {
                return Failure(
                    PrivateClipboardFailure.FormatRegistrationFailed,
                    0,
                    "Clipboard privacy formats could not be registered.");
            }

            for (int attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nativeApi.OpenClipboard(IntPtr.Zero))
                {
                    if (attempt == maximumAttempts)
                    {
                        return Failure(
                            PrivateClipboardFailure.ClipboardBusy,
                            attempt,
                            "The clipboard remained unavailable after bounded retries.");
                    }

                    retryDelay.Wait(attempt, cancellationToken);
                    continue;
                }

                PrivateClipboardResult writeResult;
                bool closed;
                try
                {
                    writeResult = WriteOpenedClipboard(text, historyFormat, cloudFormat, attempt);
                }
                finally
                {
                    closed = nativeApi.CloseClipboard();
                }

                return closed
                    ? writeResult
                    : Failure(
                        PrivateClipboardFailure.CloseClipboardFailed,
                        attempt,
                        "The clipboard could not be closed cleanly.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failure(
                PrivateClipboardFailure.NativeFailure,
                0,
                "The clipboard write failed at the Windows boundary.");
        }

        return Failure(
            PrivateClipboardFailure.NativeFailure,
            0,
            "The clipboard write did not complete.");
    }

    private PrivateClipboardResult WriteOpenedClipboard(
        string text,
        uint historyFormat,
        uint cloudFormat,
        int attempt)
    {
        if (!nativeApi.EmptyClipboard())
        {
            return Failure(
                PrivateClipboardFailure.EmptyClipboardFailed,
                attempt,
                "The clipboard could not be prepared for writing.");
        }

        byte[] privacyValue = new byte[sizeof(uint)];
        byte[] unicodeText = Encoding.Unicode.GetBytes(string.Concat(text, '\0'));
        if (!TryTransfer(historyFormat, privacyValue, out PrivateClipboardFailure transferFailure) ||
            !TryTransfer(cloudFormat, privacyValue, out transferFailure) ||
            !TryTransfer(UnicodeTextFormat, unicodeText, out transferFailure))
        {
            return Failure(
                transferFailure,
                attempt,
                "The clipboard data could not be transferred to Windows.");
        }

        return new PrivateClipboardResult(
            true,
            PrivateClipboardFailure.None,
            attempt,
            "The command was copied with local clipboard privacy requests.");
    }

    private bool TryTransfer(
        uint format,
        byte[] bytes,
        out PrivateClipboardFailure failure)
    {
        IntPtr memory = nativeApi.AllocateGlobalMemory(bytes.Length);
        if (memory == IntPtr.Zero)
        {
            failure = PrivateClipboardFailure.MemoryAllocationFailed;
            return false;
        }

        try
        {
            nativeApi.WriteGlobalMemory(memory, bytes);
            if (!nativeApi.SetClipboardData(format, memory))
            {
                failure = PrivateClipboardFailure.SetClipboardDataFailed;
                return false;
            }

            memory = IntPtr.Zero;
            failure = PrivateClipboardFailure.None;
            return true;
        }
        finally
        {
            if (memory != IntPtr.Zero)
            {
                nativeApi.FreeGlobalMemory(memory);
            }
        }
    }

    private static PrivateClipboardResult Failure(
        PrivateClipboardFailure failure,
        int attempts,
        string message) =>
        new(false, failure, attempts, message);
}

[SupportedOSPlatform("windows")]
public sealed class WindowsStaThreadRunner : IStaThreadRunner
{
    public Task<T> RunAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.SetResult(action());
            }
            catch (OperationCanceledException exception)
            {
                completion.SetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "SessionSearch private clipboard",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}

public sealed class ClipboardRetryDelay(TimeSpan delay) : IClipboardRetryDelay
{
    public ClipboardRetryDelay()
        : this(TimeSpan.FromMilliseconds(20))
    {
    }

    public void Wait(int failedAttempt, CancellationToken cancellationToken)
    {
        TimeSpan boundedDelay = TimeSpan.FromMilliseconds(
            Math.Min(delay.TotalMilliseconds * Math.Max(1, failedAttempt), 100));
        if (cancellationToken.WaitHandle.WaitOne(boundedDelay))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPrivateClipboardNativeApi : IPrivateClipboardNativeApi
{
    private const uint GlobalMemoryMoveable = 0x0002;
    private const uint GlobalMemoryZeroInitialize = 0x0040;

    public uint RegisterClipboardFormat(string formatName) =>
        NativeMethods.RegisterClipboardFormat(formatName);

    public bool OpenClipboard(IntPtr ownerWindow) => NativeMethods.OpenClipboard(ownerWindow);

    public bool EmptyClipboard() => NativeMethods.EmptyClipboard();

    public IntPtr AllocateGlobalMemory(int byteCount) =>
        NativeMethods.GlobalAlloc(
            GlobalMemoryMoveable | GlobalMemoryZeroInitialize,
            checked((nuint)byteCount));

    public void WriteGlobalMemory(IntPtr memory, byte[] bytes)
    {
        IntPtr destination = NativeMethods.GlobalLock(memory);
        if (destination == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        bool unlocked;
        int unlockError;
        try
        {
            Marshal.Copy(bytes, 0, destination, bytes.Length);
        }
        finally
        {
            unlocked = NativeMethods.GlobalUnlock(memory);
            unlockError = Marshal.GetLastWin32Error();
        }

        if (!unlocked && unlockError != 0)
        {
            throw new Win32Exception(unlockError);
        }
    }

    public bool SetClipboardData(uint format, IntPtr memory) =>
        NativeMethods.SetClipboardData(format, memory) != IntPtr.Zero;

    public void FreeGlobalMemory(IntPtr memory)
    {
        if (NativeMethods.GlobalFree(memory) != IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public bool CloseClipboard() => NativeMethods.CloseClipboard();

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenClipboard(IntPtr ownerWindow);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyClipboard();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr SetClipboardData(uint format, IntPtr memory);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseClipboard();

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint RegisterClipboardFormat(string formatName);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr GlobalAlloc(uint flags, nuint byteCount);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr GlobalLock(IntPtr memory);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GlobalUnlock(IntPtr memory);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        internal static extern IntPtr GlobalFree(IntPtr memory);
    }
}
