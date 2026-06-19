using System.IO;
using System.IO.Compression;

namespace Engram;

/// <summary>
/// Orchestrates the capture → embed → link → cluster loop and serves the graph,
/// search, and export. The markdown files are the source of truth; the SQLite
/// DB is a rebuildable index.
/// </summary>
internal sealed class NoteService
{
    // A pair of notes is linked when cosine similarity clears this bar.
    // all-MiniLM-L6-v2 yields moderate cosines (~0.3–0.5) for clearly related
    // sentences, so the bar sits lower than a naive 0.5 would suggest.
    private const double SimThreshold = 0.33;
    // Cap edges per note so the graph stays readable.
    private const int MaxNeighbors = 8;

    private readonly Database _db;
    private readonly IEmbedder _embedder;
    private readonly string _notesDir;

    public NoteService(Database db, IEmbedder embedder, string? notesDir = null)
    {
        _db = db;
        _embedder = embedder;
        _notesDir = notesDir ?? Paths.NotesDir;
        Directory.CreateDirectory(_notesDir);
    }

    public string EmbedderBackend => _embedder.Backend;

    // ---- capture / edit / delete ------------------------------------------

    public Note Capture(string text)
    {
        text = (text ?? string.Empty).Trim();
        var title = DeriveTitle(text);
        var now = DateTime.UtcNow.ToString("o");

        var id = _db.InsertNote(title, "", text, now);
        var path = Path.Combine(_notesDir, $"{id}.md");
        File.WriteAllText(path, text);
        _db.SetNotePath(id, path);

        _db.SaveEmbedding(id, _embedder.Embed(EmbedInput(title, text)));
        Reindex();
        return _db.GetNote(id)!;
    }

    public Note? Update(long id, string text)
    {
        var existing = _db.GetNote(id);
        if (existing is null) return null;
        text = (text ?? string.Empty).Trim();
        var title = DeriveTitle(text);
        var now = DateTime.UtcNow.ToString("o");

        _db.UpdateNote(id, title, text, now);
        File.WriteAllText(existing.Path, text);
        _db.SaveEmbedding(id, _embedder.Embed(EmbedInput(title, text)));
        Reindex();
        return _db.GetNote(id);
    }

    public void Delete(long id)
    {
        var note = _db.GetNote(id);
        _db.DeleteNote(id);
        if (note is not null && File.Exists(note.Path))
        {
            try { File.Delete(note.Path); } catch { /* best effort */ }
        }
        Reindex();
    }

    // ---- indexing ---------------------------------------------------------

    /// <summary>Recompute edges and clusters from current embeddings.</summary>
    public void Reindex()
    {
        var emb = _db.GetAllEmbeddings();
        var edges = ComputeEdges(emb);

        // Re-apply user-accepted links so they survive recomputation.
        var present = new HashSet<(long, long)>(edges.Select(e => Key(e.source, e.target)));
        foreach (var (a, b) in _db.GetAcceptedLinks())
        {
            if (!emb.ContainsKey(a) || !emb.ContainsKey(b)) continue;
            var k = Key(a, b);
            if (present.Add(k)) edges.Add(new GraphLink(k.Item1, k.Item2, 1.0));
        }

        _db.ReplaceAllEdges(edges);
        var (clusters, map) = Clustering.Compute(emb.Keys.ToList(), edges);
        _db.ReplaceClusters(clusters, map);
    }

    private static (long, long) Key(long a, long b) => a < b ? (a, b) : (b, a);

    /// <summary>Merge note <paramref name="fromId"/> into <paramref name="toId"/>:
    /// append its body, re-embed the survivor, delete the source.</summary>
    public void Merge(long fromId, long toId)
    {
        var from = _db.GetNote(fromId);
        var to = _db.GetNote(toId);
        if (from is null || to is null || fromId == toId) return;

        var merged = (to.Body.TrimEnd() + "\n\n" + from.Body.Trim()).Trim();
        var title = DeriveTitle(merged);
        var now = DateTime.UtcNow.ToString("o");
        _db.UpdateNote(toId, title, merged, now);
        File.WriteAllText(to.Path, merged);
        _db.SaveEmbedding(toId, _embedder.Embed(EmbedInput(title, merged)));

        _db.DeleteNote(fromId);
        if (File.Exists(from.Path)) { try { File.Delete(from.Path); } catch { } }
        Reindex();
    }

    private static List<GraphLink> ComputeEdges(Dictionary<long, float[]> emb)
    {
        var ids = emb.Keys.ToList();
        // candidate[a] = list of (b, weight) above threshold
        var candidates = ids.ToDictionary(id => id, _ => new List<(long b, double w)>());
        for (int i = 0; i < ids.Count; i++)
        {
            for (int j = i + 1; j < ids.Count; j++)
            {
                double sim = Cosine(emb[ids[i]], emb[ids[j]]);
                if (sim < SimThreshold) continue;
                candidates[ids[i]].Add((ids[j], sim));
                candidates[ids[j]].Add((ids[i], sim));
            }
        }

        // Keep top-K per node, then dedupe undirected pairs.
        var seen = new HashSet<(long, long)>();
        var edges = new List<GraphLink>();
        foreach (var id in ids)
        {
            foreach (var (b, w) in candidates[id].OrderByDescending(x => x.w).Take(MaxNeighbors))
            {
                var key = id < b ? (id, b) : (b, id);
                if (seen.Add(key)) edges.Add(new GraphLink(key.Item1, key.Item2, Math.Round(w, 4)));
            }
        }
        return edges;
    }

    private static double Cosine(float[] a, float[] b)
    {
        // Vectors are L2-normalized at embed time, so dot == cosine.
        int n = Math.Min(a.Length, b.Length);
        double dot = 0;
        for (int i = 0; i < n; i++) dot += (double)a[i] * b[i];
        return dot;
    }

    // ---- read models ------------------------------------------------------

    public GraphData GetGraph()
    {
        var notes = _db.GetAllNotes();
        var clusters = _db.GetClusters();
        var colorByCluster = clusters.ToDictionary(c => c.id, c => c.color);
        var edges = _db.GetAllEdges();

        var nodes = notes.Select(n =>
        {
            long cl = n.ClusterId ?? 0;
            var color = cl != 0 && colorByCluster.TryGetValue(cl, out var c) ? c : "#5C6370";
            return new GraphNode(n.Id, n.Title, cl, color, Snippet(n.Body));
        }).ToList();

        return new GraphData(nodes, edges, clusters);
    }

    public Note? GetNote(long id) => _db.GetNote(id);
    public List<Note> GetAllNotes() => _db.GetAllNotes();

    public List<SearchHit> Search(string query, int topN = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var q = _embedder.Embed(query);
        var emb = _db.GetAllEmbeddings();
        var notes = _db.GetAllNotes().ToDictionary(n => n.Id);
        var clusters = _db.GetClusters().ToDictionary(c => c.id);

        return emb
            .Select(kv => (id: kv.Key, score: Cosine(q, kv.Value)))
            .Where(x => x.score > 0.15 && notes.ContainsKey(x.id))
            .OrderByDescending(x => x.score)
            .Take(topN)
            .Select(x =>
            {
                var n = notes[x.id];
                long cl = n.ClusterId ?? 0;
                var color = cl != 0 && clusters.TryGetValue(cl, out var c) ? c.color : "#5C6370";
                return new SearchHit(n.Id, n.Title, Snippet(n.Body), Math.Round(x.score, 4), cl, color);
            })
            .ToList();
    }

    // ---- export -----------------------------------------------------------

    /// <summary>Zip up all markdown notes (+ the DB index) for moving machines.</summary>
    public string ExportZip(string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);
        using var zip = ZipFile.Open(destPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(_notesDir, "*.md"))
            zip.CreateEntryFromFile(file, $"notes/{Path.GetFileName(file)}");
        if (File.Exists(Paths.DbPath))
            zip.CreateEntryFromFile(Paths.DbPath, "engram.db");
        return destPath;
    }

    // ---- helpers ----------------------------------------------------------

    private static string EmbedInput(string title, string body)
        => string.IsNullOrEmpty(title) ? body : title + "\n" + body;

    private static string DeriveTitle(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart('#', ' ', '\t', '-', '*', '>').Trim();
            if (line.Length == 0) continue;
            return line.Length <= 60 ? line : line[..60].TrimEnd() + "…";
        }
        return "untitled";
    }

    private static string Snippet(string body)
    {
        var flat = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 140 ? flat : flat[..140].TrimEnd() + "…";
    }
}
