using Microsoft.Data.Sqlite;

namespace UavOps.MapBuilder;

/// <summary>An MBTiles 1.3 file (SQLite): the simulator serves its tiles straight from it.
/// With <c>resume</c>, an existing file is kept and added to, and what's written is committed every
/// few hundred tiles (WAL), so a long download can be stopped and restarted, and the simulator can
/// already serve what's there.</summary>
public sealed class MbTilesWriter : IDisposable
{
    private const int CommitEvery = 500;

    private readonly SqliteConnection _db;
    private SqliteTransaction _tx;
    private readonly SqliteCommand _insert;
    private readonly object _lock = new();
    private int _sinceCommit;

    public MbTilesWriter(string path, IReadOnlyDictionary<string, string> metadata, bool resume = false)
    {
        if (!resume && File.Exists(path))
            File.Delete(path);
        _db = new SqliteConnection($"Data Source={path};Pooling=False");
        _db.Open();
        using (var create = _db.CreateCommand())
        {
            create.CommandText = (resume ? "PRAGMA journal_mode = WAL;" : "PRAGMA journal_mode = OFF;") + """
                PRAGMA synchronous = OFF;
                CREATE TABLE IF NOT EXISTS metadata (name TEXT PRIMARY KEY, value TEXT);
                CREATE TABLE IF NOT EXISTS tiles (zoom_level INTEGER, tile_column INTEGER, tile_row INTEGER, tile_data BLOB,
                                    PRIMARY KEY (zoom_level, tile_column, tile_row));
                """;
            create.ExecuteNonQuery();
        }
        foreach (var (name, value) in metadata)
        {
            using var meta = _db.CreateCommand();
            meta.CommandText = "INSERT OR REPLACE INTO metadata (name, value) VALUES ($n, $v)";
            meta.Parameters.AddWithValue("$n", name);
            meta.Parameters.AddWithValue("$v", value);
            meta.ExecuteNonQuery();
        }
        _tx = _db.BeginTransaction();
        _insert = _db.CreateCommand();
        _insert.Transaction = _tx;
        _insert.CommandText = "INSERT OR REPLACE INTO tiles (zoom_level, tile_column, tile_row, tile_data) VALUES ($z, $x, $y, $d)";
        _insert.Parameters.Add("$z", SqliteType.Integer);
        _insert.Parameters.Add("$x", SqliteType.Integer);
        _insert.Parameters.Add("$y", SqliteType.Integer);
        _insert.Parameters.Add("$d", SqliteType.Blob);
    }

    public int Count { get; private set; }

    /// <summary>Stores tile (z, x, y) in XYZ numbering (MBTiles rows are flipped, TMS).</summary>
    public void Add(int z, int x, int y, byte[] data)
    {
        lock (_lock)
        {
            _insert.Parameters["$z"].Value = z;
            _insert.Parameters["$x"].Value = x;
            _insert.Parameters["$y"].Value = (1 << z) - 1 - y;
            _insert.Parameters["$d"].Value = data;
            _insert.ExecuteNonQuery();
            Count++;
            if (++_sinceCommit >= CommitEvery)
                Commit();
        }
    }

    /// <summary>A tile already written (XYZ numbering), for building the zoom level below it.</summary>
    public byte[]? Get(int z, int x, int y)
    {
        lock (_lock)
        {
            using var select = _db.CreateCommand();
            select.Transaction = _tx;
            select.CommandText = "SELECT tile_data FROM tiles WHERE zoom_level = $z AND tile_column = $x AND tile_row = $y";
            select.Parameters.AddWithValue("$z", z);
            select.Parameters.AddWithValue("$x", x);
            select.Parameters.AddWithValue("$y", (1 << z) - 1 - y);
            return select.ExecuteScalar() as byte[];
        }
    }

    private void Commit()
    {
        _tx.Commit();
        _tx.Dispose();
        _tx = _db.BeginTransaction();
        _insert.Transaction = _tx;
        _sinceCommit = 0;
    }

    public void Dispose()
    {
        _tx.Commit();
        _insert.Dispose();
        _tx.Dispose();
        _db.Close();
        _db.Dispose();
    }
}
