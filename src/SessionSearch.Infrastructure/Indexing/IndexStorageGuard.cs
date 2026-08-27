namespace SessionSearch.Infrastructure.Indexing;

public sealed record IndexStorageCheck(
    bool MayIndexContent,
    long IndexBytes,
    long AvailableFreeBytes,
    string? DiagnosticCode,
    string? Message);

public interface IIndexStorageGuard
{
    IndexStorageCheck Check(string databasePath);
}

public sealed class PhysicalIndexStorageGuard : IIndexStorageGuard
{
    public const long MaximumIndexBytes = 64L * 1024 * 1024 * 1024;
    public const long MinimumFreeBytes = 5L * 1024 * 1024 * 1024;

    private static readonly string[] DatabaseSuffixes =
        [string.Empty, "-wal", "-shm"];

    public IndexStorageCheck Check(string databasePath)
    {
        try
        {
            string fullPath = Path.GetFullPath(databasePath);
            long indexBytes = 0;
            foreach (string suffix in DatabaseSuffixes)
            {
                FileInfo file = new(fullPath + suffix);
                file.Refresh();
                if (file.Exists)
                {
                    indexBytes = checked(indexBytes + file.Length);
                }
            }

            DriveInfo drive = new(Path.GetPathRoot(fullPath)!);
            long freeBytes = drive.AvailableFreeSpace;
            if (indexBytes >= MaximumIndexBytes)
            {
                return Blocked(
                    indexBytes,
                    freeBytes,
                    "index-size-limit",
                    "Transcript indexing paused because the app index reached 64 GiB.");
            }

            if (freeBytes < MinimumFreeBytes)
            {
                return Blocked(
                    indexBytes,
                    freeBytes,
                    "disk-free-limit",
                    "Transcript indexing paused because the fixed disk has less than 5 GiB free.");
            }

            return new IndexStorageCheck(
                MayIndexContent: true,
                indexBytes,
                freeBytes,
                DiagnosticCode: null,
                Message: null);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return Blocked(
                IndexBytes: 0,
                AvailableFreeBytes: 0,
                "index-storage-check-failed",
                "Transcript indexing paused because app-index storage could not be verified.");
        }
    }

    private static IndexStorageCheck Blocked(
        long IndexBytes,
        long AvailableFreeBytes,
        string code,
        string message) =>
        new(
            MayIndexContent: false,
            IndexBytes,
            AvailableFreeBytes,
            code,
            message);
}
