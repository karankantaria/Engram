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
    // Every Nth note, an incremental add does a full exact rebuild instead, to
    // sweep up the small edge surplus the incremental path can accumulate.
    private const int FullReindexEvery = 50;

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
        var id = AddNoteCore(text);
        ReindexFor(new[] { id });
        return _db.GetNote(id)!;
    }

    /// <summary>Insert + persist + embed a single note without reindexing.
    /// Bulk paths reindex once at the end.</summary>
    private long AddNoteCore(string text)
    {
        text = (text ?? string.Empty).Trim();
        var title = DeriveTitle(text);
        var now = DateTime.UtcNow.ToString("o");

        var id = _db.InsertNote(title, "", text, now);
        var path = Path.Combine(_notesDir, $"{id}.md");
        File.WriteAllText(path, text);
        _db.SetNotePath(id, path);

        _db.SaveEmbedding(id, _embedder.Embed(EmbedInput(title, text)));
        return id;
    }

    /// <summary>Toggle a markdown checkbox in a note and write it back to disk.
    /// Deliberately skips re-embedding/reindexing — a checkbox flip doesn't
    /// change the note's meaning, and re-embedding on every tick would be wasteful.</summary>
    public void SetChecklistItem(long noteId, int lineIndex, bool done)
    {
        var note = _db.GetNote(noteId);
        if (note is null) return;
        var lines = note.Body.Replace("\r\n", "\n").Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length) return;

        lines[lineIndex] = TaskScanner.SetLine(lines[lineIndex], done);
        var body = string.Join("\n", lines);
        _db.UpdateNote(noteId, note.Title, body, DateTime.UtcNow.ToString("o"));
        try { File.WriteAllText(note.Path, body); } catch { /* best effort */ }
    }

    public sealed record ImportResult(int files, int notes, int skipped);

    /// <summary>Import files (dropped or picked): extract text, chunk into
    /// note-sized pieces, add each, then reindex once.</summary>
    public ImportResult ImportFiles(IEnumerable<string> paths)
    {
        int files = 0, notes = 0, skipped = 0;
        var added = new List<long>();
        foreach (var path in ExpandPaths(paths))
        {
            var extracted = FileImporter.Extract(path);
            if (extracted is null) { skipped++; continue; }

            var chunks = Chunker.Chunk(extracted.Value.Text, extracted.Value.Markdown);
            if (chunks.Count == 0) { skipped++; continue; }

            foreach (var c in chunks) { added.Add(AddNoteCore(c)); notes++; }
            files++;
        }
        if (added.Count > 0) ReindexFor(added);
        return new ImportResult(files, notes, skipped);
    }

    /// <summary>Flatten dropped paths: expand any directories to their files.</summary>
    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    yield return f;
            }
            else if (File.Exists(p))
            {
                yield return p;
            }
        }
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
        ReindexFor(new[] { id });
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

    /// <summary>Full recompute of edges and clusters from all embeddings. O(n²)
    /// in the note count — used on structural changes (delete/merge) and as the
    /// exact rebuild. Adds/updates use the cheaper <see cref="ReindexFor"/>.</summary>
    public void Reindex()
    {
        var (oldMeta, oldAssign) = SnapshotClusters();
        var emb = _db.GetAllEmbeddings();
        var edges = ComputeEdges(emb);
        ApplyAcceptedLinks(edges, emb);
        Persist(emb, edges, oldMeta, oldAssign);
    }

    /// <summary>
    /// Incremental reindex after adding/updating <paramref name="changedIds"/>.
    /// Only the changed notes — and existing notes similar to them — have their
    /// top-K neighbours recomputed (O(changed·n) cosines); every other edge is
    /// carried over untouched. This avoids the full O(n²) recompute on each
    /// capture. It never drops a still-valid edge (carried edges are a superset
    /// of the exact result), so the graph can't drift; a periodic full
    /// <see cref="Reindex"/> (on delete/merge) keeps it tight. Falls back to a
    /// full reindex when the graph is small or a large fraction changed.
    /// </summary>
    public void ReindexFor(IReadOnlyCollection<long> changedIds)
    {
        var emb = _db.GetAllEmbeddings();
        var changed = changedIds.Where(emb.ContainsKey).ToHashSet();
        // Fall back to a full rebuild when: nothing valid changed; the graph is
        // small enough that the quadratic cost is trivial; a large fraction
        // changed (incremental bookkeeping isn't worth it); or periodically, so
        // the small edge surplus incremental can leave (carried displaced
        // neighbours) is swept up even for capture-only users who never trigger
        // a structural rebuild via delete/merge.
        if (changed.Count == 0 || emb.Count <= 64 || changed.Count * 4 >= emb.Count
            || emb.Count % FullReindexEvery == 0)
        {
            Reindex();
            return;
        }

        var (oldMeta, oldAssign) = SnapshotClusters();
        var ids = emb.Keys.ToList();

        // 1. Recompute each changed note's top-K, collecting the existing notes
        //    that are similar to it ("affected" — their top-K may shift too).
        var affected = new HashSet<long>();
        var fresh = new List<GraphLink>();
        foreach (var c in changed)
            fresh.AddRange(TopKEdges(c, ids, emb, changed, affected));

        // 2. Recompute the affected existing notes' top-K as well.
        foreach (var a in affected)
            fresh.AddRange(TopKEdges(a, ids, emb, null, null));

        // 3. Carry over every edge not incident to a *changed* note, then merge
        //    in the freshly computed edges (keeping the larger weight on dupes).
        var merged = new Dictionary<(long, long), double>();
        foreach (var e in _db.GetAllEdges())
        {
            if (changed.Contains(e.source) || changed.Contains(e.target)) continue;
            merged[Key(e.source, e.target)] = e.weight;
        }
        foreach (var e in fresh)
        {
            var k = Key(e.source, e.target);
            if (!merged.TryGetValue(k, out var w) || e.weight > w) merged[k] = e.weight;
        }

        var edges = merged.Select(kv => new GraphLink(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
        ApplyAcceptedLinks(edges, emb);
        Persist(emb, edges, oldMeta, oldAssign);
    }

    /// <summary>Snapshot cluster metadata + assignment BEFORE replacing them, so
    /// librarian-assigned names/summaries can be carried forward.</summary>
    private (Dictionary<long, (string name, string summary)> meta, Dictionary<long, long> assign) SnapshotClusters()
    {
        var meta = _db.GetClusters().ToDictionary(c => c.id, c => (c.name, c.summary));
        var assign = _db.GetAllNotes()
            .Where(n => n.ClusterId is not null)
            .ToDictionary(n => n.Id, n => n.ClusterId!.Value);
        return (meta, assign);
    }

    /// <summary>Re-apply user-accepted links (weight 1.0) so they survive a
    /// recompute even when they fall below the similarity threshold.</summary>
    private void ApplyAcceptedLinks(List<GraphLink> edges, Dictionary<long, float[]> emb)
    {
        var present = new HashSet<(long, long)>(edges.Select(e => Key(e.source, e.target)));
        foreach (var (a, b) in _db.GetAcceptedLinks())
        {
            if (!emb.ContainsKey(a) || !emb.ContainsKey(b)) continue;
            var k = Key(a, b);
            if (present.Add(k)) edges.Add(new GraphLink(k.Item1, k.Item2, 1.0));
        }
    }

    /// <summary>Write the edge set, recompute clusters, and persist them with
    /// carried-over names/summaries.</summary>
    private void Persist(Dictionary<long, float[]> emb, List<GraphLink> edges,
        Dictionary<long, (string name, string summary)> oldMeta, Dictionary<long, long> oldAssign)
    {
        _db.ReplaceAllEdges(edges);
        var (clusters, map) = Clustering.Compute(emb.Keys.ToList(), edges);
        _db.ReplaceClusters(CarryClusterMeta(clusters, map, oldMeta, oldAssign), map);
    }

    /// <summary>A node's top-K neighbours above the similarity threshold, as
    /// directed edges. If <paramref name="affected"/> is supplied, any neighbour
    /// not in <paramref name="exclude"/> is recorded there (notes whose own top-K
    /// may have shifted because of this node).</summary>
    private static IEnumerable<GraphLink> TopKEdges(long node, List<long> ids,
        Dictionary<long, float[]> emb, HashSet<long>? exclude, HashSet<long>? affected)
    {
        var v = emb[node];
        var sims = new List<(long other, double w)>();
        foreach (var o in ids)
        {
            if (o == node) continue;
            double s = Cosine(v, emb[o]);
            if (s < SimThreshold) continue;
            sims.Add((o, s));
            if (affected is not null && (exclude is null || !exclude.Contains(o))) affected.Add(o);
        }
        return sims.OrderByDescending(x => x.w).Take(MaxNeighbors)
                   .Select(x => new GraphLink(node, x.other, Math.Round(x.w, 4)))
                   .ToList();
    }

    /// <summary>Cluster ids are renumbered on every reindex, so a librarian's
    /// name/summary can't be matched by id. Instead, match each new cluster to
    /// the old cluster the majority of its members came from, and carry the old
    /// (non-default) name + summary forward. Each old cluster's name is reused
    /// at most once (best-overlap wins) so a split doesn't duplicate it.</summary>
    private static List<(long clusterId, string name, string color, string summary)> CarryClusterMeta(
        List<(long clusterId, string name, string color)> fresh,
        Dictionary<long, long> noteToCluster,
        Dictionary<long, (string name, string summary)> oldMeta,
        Dictionary<long, long> oldAssign)
    {
        // new cluster id -> { old cluster id -> shared-member count }
        var overlap = new Dictionary<long, Dictionary<long, int>>();
        foreach (var (noteId, newCid) in noteToCluster)
        {
            if (!oldAssign.TryGetValue(noteId, out var oldCid)) continue;
            if (!overlap.TryGetValue(newCid, out var tally)) overlap[newCid] = tally = new();
            tally[oldCid] = tally.GetValueOrDefault(oldCid) + 1;
        }

        var candidates = overlap
            .SelectMany(kv => kv.Value.Select(o => (newCid: kv.Key, oldCid: o.Key, count: o.Value)))
            .OrderByDescending(c => c.count);

        var carried = new Dictionary<long, (string name, string summary)>();
        var usedOld = new HashSet<long>();
        foreach (var c in candidates)
        {
            if (carried.ContainsKey(c.newCid) || usedOld.Contains(c.oldCid)) continue;
            if (!oldMeta.TryGetValue(c.oldCid, out var meta) || IsDefaultName(meta.name)) continue;
            carried[c.newCid] = meta;
            usedOld.Add(c.oldCid);
        }

        return fresh.Select(f => carried.TryGetValue(f.clusterId, out var m)
            ? (f.clusterId, m.name, f.color, m.summary)
            : (f.clusterId, f.name, f.color, "")).ToList();
    }

    /// <summary>True for the auto-generated "cluster N" placeholder names that
    /// carry no user/librarian intent and aren't worth preserving.</summary>
    private static bool IsDefaultName(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name ?? "", @"^cluster \d+$");

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
            var color = cl != 0 && colorByCluster.TryGetValue(cl, out var c) ? c : "#5C6675";
            return new GraphNode(n.Id, n.Title, cl, color, Snippet(n.Body), n.Body.Length);
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
                var color = cl != 0 && clusters.TryGetValue(cl, out var c) ? c.color : "#5C6675";
                return new SearchHit(n.Id, n.Title, Snippet(n.Body), Math.Round(x.score, 4), cl, color);
            })
            .ToList();
    }

    /// <summary>Top-k most relevant notes (full bodies) for grounding a RAG
    /// answer, with each note's cluster color for citation display.</summary>
    public List<(Note note, double score, string color)> RetrieveRelevant(string query, int k)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var q = _embedder.Embed(query);
        var emb = _db.GetAllEmbeddings();
        var notes = _db.GetAllNotes().ToDictionary(n => n.Id);
        var clusters = _db.GetClusters().ToDictionary(c => c.id);

        return emb
            .Select(kv => (id: kv.Key, score: Cosine(q, kv.Value)))
            .Where(x => notes.ContainsKey(x.id))
            .OrderByDescending(x => x.score)
            .Take(k)
            .Select(x =>
            {
                var n = notes[x.id];
                long cl = n.ClusterId ?? 0;
                var color = cl != 0 && clusters.TryGetValue(cl, out var c) ? c.color : "#5C6675";
                return (n, x.score, color);
            })
            .ToList();
    }

    // ---- export -----------------------------------------------------------

    /// <summary>Zip up all markdown notes (+ the DB index) for moving machines.</summary>
    public string ExportZip(string destPath)
    {
        if (File.Exists(destPath)) File.Delete(destPath);
        // Fold the WAL into the main db file so the copied snapshot is complete.
        _db.Checkpoint();
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
