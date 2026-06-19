using System.IO;
using Microsoft.Data.Sqlite;

namespace Engram;

/// <summary>
/// SQLite index over the notes. Holds metadata, embedding vectors (as float32
/// BLOBs), similarity edges, emergent clusters, and review suggestions.
/// The markdown files on disk remain the portable source of truth; this DB is
/// a rebuildable cache/index. Single connection guarded by a lock — this is a
/// single-user desktop app, so contention is negligible.
/// </summary>
internal sealed class Database : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public Database(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
        CreateSchema();
    }

    private void CreateSchema()
    {
        Exec(@"
CREATE TABLE IF NOT EXISTS notes(
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    title       TEXT NOT NULL,
    path        TEXT NOT NULL,
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    body        TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS embeddings(
    note_id     INTEGER PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
    vector      BLOB NOT NULL,
    dim         INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS edges(
    a_id        INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    b_id        INTEGER NOT NULL REFERENCES notes(id) ON DELETE CASCADE,
    weight      REAL NOT NULL,
    PRIMARY KEY(a_id, b_id)
);
CREATE TABLE IF NOT EXISTS clusters(
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    name        TEXT NOT NULL,
    color       TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS note_clusters(
    note_id     INTEGER PRIMARY KEY REFERENCES notes(id) ON DELETE CASCADE,
    cluster_id  INTEGER NOT NULL REFERENCES clusters(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS suggestions(
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    kind        TEXT NOT NULL,
    payload     TEXT NOT NULL,
    status      TEXT NOT NULL DEFAULT 'pending',
    created_at  TEXT NOT NULL
);");
    }

    // ---- Notes -------------------------------------------------------------

    public long InsertNote(string title, string path, string body, string nowIso)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO notes(title, path, created_at, updated_at, body)
                                VALUES($t, $p, $c, $c, $b);
                                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$c", nowIso);
            cmd.Parameters.AddWithValue("$b", body);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void UpdateNote(long id, string title, string body, string nowIso)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE notes SET title=$t, body=$b, updated_at=$u WHERE id=$id;";
            cmd.Parameters.AddWithValue("$t", title);
            cmd.Parameters.AddWithValue("$b", body);
            cmd.Parameters.AddWithValue("$u", nowIso);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void SetNotePath(long id, string path)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE notes SET path=$p WHERE id=$id;";
            cmd.Parameters.AddWithValue("$p", path);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteNote(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM notes WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    public Note? GetNote(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT n.id, n.title, n.path, n.created_at, n.updated_at, n.body, nc.cluster_id
                                FROM notes n LEFT JOIN note_clusters nc ON nc.note_id = n.id
                                WHERE n.id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadNote(r) : null;
        }
    }

    public List<Note> GetAllNotes()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT n.id, n.title, n.path, n.created_at, n.updated_at, n.body, nc.cluster_id
                                FROM notes n LEFT JOIN note_clusters nc ON nc.note_id = n.id
                                ORDER BY n.created_at DESC;";
            using var r = cmd.ExecuteReader();
            var list = new List<Note>();
            while (r.Read()) list.Add(ReadNote(r));
            return list;
        }
    }

    private static Note ReadNote(SqliteDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetInt64(6));

    // ---- Embeddings --------------------------------------------------------

    public void SaveEmbedding(long noteId, float[] vec)
    {
        var blob = new byte[vec.Length * sizeof(float)];
        Buffer.BlockCopy(vec, 0, blob, 0, blob.Length);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO embeddings(note_id, vector, dim) VALUES($id,$v,$d)
                                ON CONFLICT(note_id) DO UPDATE SET vector=$v, dim=$d;";
            cmd.Parameters.AddWithValue("$id", noteId);
            cmd.Parameters.AddWithValue("$v", blob);
            cmd.Parameters.AddWithValue("$d", vec.Length);
            cmd.ExecuteNonQuery();
        }
    }

    public Dictionary<long, float[]> GetAllEmbeddings()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT note_id, vector, dim FROM embeddings;";
            using var r = cmd.ExecuteReader();
            var map = new Dictionary<long, float[]>();
            while (r.Read())
            {
                var blob = (byte[])r["vector"];
                var dim = r.GetInt32(2);
                var vec = new float[dim];
                Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
                map[r.GetInt64(0)] = vec;
            }
            return map;
        }
    }

    // ---- Edges -------------------------------------------------------------

    /// <summary>Replace the entire edge set (recomputed globally after a change).</summary>
    public void ReplaceAllEdges(IEnumerable<GraphLink> edges)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using (var del = _conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM edges;";
                del.ExecuteNonQuery();
            }
            using (var ins = _conn.CreateCommand())
            {
                ins.CommandText = "INSERT OR REPLACE INTO edges(a_id,b_id,weight) VALUES($a,$b,$w);";
                var pa = ins.Parameters.Add("$a", SqliteType.Integer);
                var pb = ins.Parameters.Add("$b", SqliteType.Integer);
                var pw = ins.Parameters.Add("$w", SqliteType.Real);
                foreach (var e in edges)
                {
                    pa.Value = e.source; pb.Value = e.target; pw.Value = e.weight;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();
        }
    }

    public List<GraphLink> GetAllEdges()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT a_id, b_id, weight FROM edges;";
            using var r = cmd.ExecuteReader();
            var list = new List<GraphLink>();
            while (r.Read()) list.Add(new GraphLink(r.GetInt64(0), r.GetInt64(1), r.GetDouble(2)));
            return list;
        }
    }

    // ---- Clusters ----------------------------------------------------------

    /// <summary>Rebuild clusters from a fresh assignment. Preserves existing
    /// cluster names where a previous cluster maps to the same id slot.</summary>
    public void ReplaceClusters(IReadOnlyList<(long clusterId, string name, string color)> clusters,
                                IReadOnlyDictionary<long, long> noteToCluster)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            Run("DELETE FROM note_clusters;");
            Run("DELETE FROM clusters;");
            using (var ins = _conn.CreateCommand())
            {
                ins.CommandText = "INSERT INTO clusters(id,name,color) VALUES($id,$n,$c);";
                var pid = ins.Parameters.Add("$id", SqliteType.Integer);
                var pn = ins.Parameters.Add("$n", SqliteType.Text);
                var pc = ins.Parameters.Add("$c", SqliteType.Text);
                foreach (var (cid, name, color) in clusters)
                {
                    pid.Value = cid; pn.Value = name; pc.Value = color;
                    ins.ExecuteNonQuery();
                }
            }
            using (var ins = _conn.CreateCommand())
            {
                ins.CommandText = "INSERT INTO note_clusters(note_id,cluster_id) VALUES($n,$c);";
                var pn = ins.Parameters.Add("$n", SqliteType.Integer);
                var pc = ins.Parameters.Add("$c", SqliteType.Integer);
                foreach (var kv in noteToCluster)
                {
                    pn.Value = kv.Key; pc.Value = kv.Value;
                    ins.ExecuteNonQuery();
                }
            }
            tx.Commit();

            void Run(string sql)
            {
                using var c = _conn.CreateCommand();
                c.CommandText = sql;
                c.ExecuteNonQuery();
            }
        }
    }

    public List<ClusterInfo> GetClusters()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT c.id, c.name, c.color, COUNT(nc.note_id)
                                FROM clusters c LEFT JOIN note_clusters nc ON nc.cluster_id=c.id
                                GROUP BY c.id ORDER BY c.id;";
            using var r = cmd.ExecuteReader();
            var list = new List<ClusterInfo>();
            while (r.Read()) list.Add(new ClusterInfo(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
            return list;
        }
    }

    public void RenameCluster(long clusterId, string name)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE clusters SET name=$n WHERE id=$id;";
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$id", clusterId);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- Suggestions -------------------------------------------------------

    public void ClearPendingSuggestions()
    {
        lock (_lock) Exec("DELETE FROM suggestions WHERE status='pending';");
    }

    public long AddSuggestion(string kind, string payloadJson, string nowIso)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO suggestions(kind,payload,status,created_at)
                                VALUES($k,$p,'pending',$c); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$p", payloadJson);
            cmd.Parameters.AddWithValue("$c", nowIso);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public List<Suggestion> GetPendingSuggestions()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT id,kind,payload,status,created_at FROM suggestions
                                WHERE status='pending' ORDER BY id;";
            using var r = cmd.ExecuteReader();
            var list = new List<Suggestion>();
            while (r.Read())
                list.Add(new Suggestion(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            return list;
        }
    }

    public Suggestion? GetSuggestion(long id)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id,kind,payload,status,created_at FROM suggestions WHERE id=$id;";
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            return r.Read()
                ? new Suggestion(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))
                : null;
        }
    }

    /// <summary>User-accepted links are re-applied as edges after each reindex
    /// so they survive recomputation.</summary>
    public List<(long a, long b)> GetAcceptedLinks()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT payload FROM suggestions WHERE kind='link' AND status='accepted';";
            using var r = cmd.ExecuteReader();
            var list = new List<(long, long)>();
            while (r.Read())
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(r.GetString(0));
                    var root = doc.RootElement;
                    list.Add((root.GetProperty("a").GetInt64(), root.GetProperty("b").GetInt64()));
                }
                catch { /* skip malformed */ }
            }
            return list;
        }
    }

    public void SetSuggestionStatus(long id, string status)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE suggestions SET status=$s WHERE id=$id;";
            cmd.Parameters.AddWithValue("$s", status);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    // ---- helpers -----------------------------------------------------------

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
