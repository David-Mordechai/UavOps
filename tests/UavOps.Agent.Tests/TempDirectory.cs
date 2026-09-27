using Microsoft.Data.Sqlite;

namespace UavOps.Agent.Tests;

/// <summary>Deletes a test's temporary SQLite folder. <see cref="SqliteConnection.ClearAllPools"/>
/// is process-wide and other test classes open SQLite files in parallel, so the file can still be
/// held for a moment (seen: 1 run in ~10 failed with "aoi.db is being used by another process").
/// A few short retries, then give up quietly - a leftover temp folder isn't a test failure.</summary>
internal static class TempDirectory
{
    public static void DeleteSqliteFolder(string directory)
    {
        SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 5 && Directory.Exists(directory); attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(100);
                SqliteConnection.ClearAllPools();
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}
