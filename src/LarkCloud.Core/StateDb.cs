using Microsoft.Data.Sqlite;

namespace LarkCloud.Core;

/// <summary>What every path looked like after it was last synced (one SQLite file per synced folder).
/// The engine keeps the whole table in memory during a pass and writes changes in one transaction.</summary>
public sealed class StateDb : IDisposable
{
    readonly SqliteConnection _db;
    readonly object _lock = new();
    readonly object _dbLock = new();          // one thread at a time on the SQLite connection (it isn't thread-safe)
    readonly Dictionary<string, BaseEntry> _all = new(StringComparer.Ordinal);
    readonly Dictionary<string, BaseEntry?> _dirty = new(StringComparer.Ordinal);

    public StateDb(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL");
        Exec(@"CREATE TABLE IF NOT EXISTS entries (path TEXT PRIMARY KEY, kind TEXT NOT NULL, token TEXT, rtype TEXT,
               rmtime TEXT, size INTEGER NOT NULL DEFAULT 0, lmtime INTEGER NOT NULL DEFAULT 0, url TEXT)");
        Exec("CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT)");
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT path, kind, token, rtype, rmtime, size, lmtime, url FROM entries";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var e = new BaseEntry
            {
                Path = r.GetString(0), Kind = r.GetString(1),
                Token = r.IsDBNull(2) ? null : r.GetString(2), RType = r.IsDBNull(3) ? null : r.GetString(3),
                RMTime = r.IsDBNull(4) ? null : r.GetString(4), Size = r.GetInt64(5), LMTime = r.GetInt64(6),
                Url = r.IsDBNull(7) ? null : r.GetString(7),
            };
            _all[e.Path] = e;
        }
    }

    void Exec(string sql)
    {
        using var c = _db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    public int Count { get { lock (_lock) return _all.Count; } }

    public BaseEntry? Get(string path) { lock (_lock) return _all.TryGetValue(path, out var e) ? e.Clone() : null; }

    public List<BaseEntry> All() { lock (_lock) return _all.Values.Select(e => e.Clone()).ToList(); }

    public void Put(BaseEntry e)
    {
        lock (_lock) { var c = e.Clone(); _all[e.Path] = c; _dirty[e.Path] = c; }
    }

    public void Remove(string path)
    {
        lock (_lock) { if (_all.Remove(path)) _dirty[path] = null; }
    }

    /// <summary>Remove a folder and everything under it.</summary>
    public void RemoveTree(string path)
    {
        lock (_lock)
        {
            if (_all.TryGetValue(path, out var one) && one.Kind != Kinds.Folder) { Remove(path); return; }     // a file: nothing under it
            foreach (var p in _all.Keys.Where(p => p == path || p.StartsWith(path + "/", StringComparison.Ordinal)).ToList())
                Remove(p);
        }
    }

    /// <summary>After a move/rename: every path under `from` now lives under `to`.</summary>
    public void RenameTree(string from, string to)
    {
        lock (_lock)
        {
            foreach (var p in _all.Keys.Where(p => p == from || p.StartsWith(from + "/", StringComparison.Ordinal)).ToList())
            {
                var e = _all[p].Clone();
                Remove(p);
                e.Path = to + p[from.Length..];
                Put(e);
            }
        }
    }

    public string? GetMeta(string key)
    {
        lock (_dbLock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "SELECT value FROM meta WHERE key=$k";
            c.Parameters.AddWithValue("$k", key);
            return c.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_dbLock)
        {
            using var c = _db.CreateCommand();
            c.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES($k, $v)";
            c.Parameters.AddWithValue("$k", key);
            c.Parameters.AddWithValue("$v", value);
            c.ExecuteNonQuery();
        }
    }

    /// <summary>Write all changes made since the last commit.</summary>
    public void Commit()
    {
        lock (_dbLock)
        {
            List<KeyValuePair<string, BaseEntry?>> work;
            lock (_lock) work = _dirty.ToList();
            if (work.Count == 0) return;
            WriteAll(work);
            lock (_lock)
                foreach (var (path, entry) in work)
                    if (_dirty.TryGetValue(path, out var current) && ReferenceEquals(current, entry))
                        _dirty.Remove(path);
        }
    }

    void WriteAll(List<KeyValuePair<string, BaseEntry?>> work)
    {
        using var tx = _db.BeginTransaction();
        using var del = _db.CreateCommand();
        del.Transaction = tx;
        del.CommandText = "DELETE FROM entries WHERE path=$p";
        var dp = del.Parameters.Add("$p", SqliteType.Text);
        using var ins = _db.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = "INSERT OR REPLACE INTO entries(path, kind, token, rtype, rmtime, size, lmtime, url) VALUES($p,$k,$t,$rt,$rm,$s,$lm,$u)";
        var p = new[] { "$p", "$k", "$t", "$rt", "$rm", "$s", "$lm", "$u" }.Select(n => ins.Parameters.Add(n, SqliteType.Text)).ToArray();
        p[5].SqliteType = SqliteType.Integer;
        p[6].SqliteType = SqliteType.Integer;
        foreach (var (path, e) in work)
        {
            if (e == null)
            {
                dp.Value = path;
                del.ExecuteNonQuery();
                continue;
            }
            p[0].Value = e.Path; p[1].Value = e.Kind; p[2].Value = (object?)e.Token ?? DBNull.Value;
            p[3].Value = (object?)e.RType ?? DBNull.Value; p[4].Value = (object?)e.RMTime ?? DBNull.Value;
            p[5].Value = e.Size; p[6].Value = e.LMTime; p[7].Value = (object?)e.Url ?? DBNull.Value;
            ins.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void Dispose()
    {
        try { Commit(); } catch (Exception ex) { Log.Error($"Couldn't save sync history: {ex.Message}"); }
        lock (_dbLock) _db.Dispose();
        SqliteConnection.ClearAllPools();
    }
}
