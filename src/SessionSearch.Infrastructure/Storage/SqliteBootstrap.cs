using System.Threading;

namespace SessionSearch.Infrastructure.Storage;

public static class SqliteBootstrap
{
    private static int initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref initialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }
}
