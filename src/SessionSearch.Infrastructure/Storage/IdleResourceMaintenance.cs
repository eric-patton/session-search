using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;

namespace SessionSearch.Infrastructure.Storage;

public static class IdleResourceMaintenance
{
    private const long MinimumAllocationsBetweenReleases = 16L * 1024 * 1024;
    private const long WorkingSetTrimThreshold = 96L * 1024 * 1024;
    private static long lastReleaseAllocatedBytes;
    private static int releaseRunning;

    public static async Task<bool> TryReleaseTransientResourcesAsync(
        SessionDatabase database,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        cancellationToken.ThrowIfCancellationRequested();
        long allocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        long previousRelease = Volatile.Read(ref lastReleaseAllocatedBytes);
        if (previousRelease != 0 &&
            allocatedBytes - previousRelease < MinimumAllocationsBetweenReleases)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref releaseRunning, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            await Task.Run(
                () =>
                {
                    database.ClearConnectionPool();
                    CompactTransientHeap(GCCollectionMode.Forced);
                    GC.WaitForPendingFinalizers();
                    GCSettings.LargeObjectHeapCompactionMode =
                        GCLargeObjectHeapCompactionMode.CompactOnce;
                    _ = GC.GetTotalMemory(forceFullCollection: true);
                    if (OperatingSystem.IsWindows())
                    {
                        TryTrimWindowsWorkingSet();
                    }
                },
                cancellationToken).ConfigureAwait(false);
            Volatile.Write(
                ref lastReleaseAllocatedBytes,
                GC.GetTotalAllocatedBytes(precise: false));
            return true;
        }
        finally
        {
            Volatile.Write(ref releaseRunning, 0);
        }
    }

    private static void CompactTransientHeap(GCCollectionMode collectionMode)
    {
        GCSettings.LargeObjectHeapCompactionMode =
            GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(
            GC.MaxGeneration,
            collectionMode,
            blocking: true,
            compacting: true);
    }

    [SupportedOSPlatform("windows")]
    private static void TryTrimWindowsWorkingSet()
    {
        using Process process = Process.GetCurrentProcess();
        if (process.WorkingSet64 <= WorkingSetTrimThreshold)
        {
            return;
        }

        _ = NativeMethods.EmptyWorkingSet(process.Handle);
    }

    private static class NativeMethods
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EmptyWorkingSet(IntPtr process);
    }
}
