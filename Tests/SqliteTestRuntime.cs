namespace InfiniteDrive.Tests;

internal static class SqliteTestRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    internal static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (_initialized) return;
            SQLitePCLEx.raw.SetProvider(new SQLitePCLEx.SQLite3Provider_sqlite3());
            _initialized = true;
        }
    }
}
