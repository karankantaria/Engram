using System.Text;
using System.Text.Json;

namespace Engram;

/// <summary>
/// Surfaces TODOs from the notes, grouped by cluster. Two sources, per the
/// hybrid design: <b>local</b> markdown checkboxes (scanned live, round-tripped
/// to the source file on completion) and <b>claude</b> implicit action items
/// (extracted on demand, tracked in the DB). The composite item id encodes the
/// source: "L:{noteId}:{line}" or "C:{taskId}".
/// </summary>
internal sealed class TodoService
{
    private readonly Database _db;
    private readonly NoteService _notes;

    public TodoService(Database db, NoteService notes)
    {
        _db = db;
        _notes = notes;
    }

    public List<TodoItem> GetTodos()
    {
        var notes = _db.GetAllNotes();
        var clusters = _db.GetClusters().ToDictionary(c => c.id);
        var items = new List<TodoItem>();

        // Local checkbox tasks (markdown is the source of truth).
        foreach (var n in notes)
            foreach (var it in TaskScanner.Scan(n.Body))
            {
                var (cl, name, color) = ClusterOf(n, clusters);
                items.Add(new TodoItem($"L:{n.Id}:{it.line}", it.text, it.done, "local", n.Id, n.Title, cl, name, color));
            }

        // Claude-extracted implicit tasks.
        var byId = notes.ToDictionary(n => n.Id);
        foreach (var t in _db.GetTasks())
        {
            if (!byId.TryGetValue(t.noteId, out var n)) continue;
            var (cl, name, color) = ClusterOf(n, clusters);
            items.Add(new TodoItem($"C:{t.id}", t.text, t.done, "claude", n.Id, n.Title, cl, name, color));
        }

        return items;
    }

    public void Toggle(string id, bool done)
    {
        if (id.StartsWith("L:", StringComparison.Ordinal))
        {
            var parts = id.Split(':');
            if (parts.Length == 3 && long.TryParse(parts[1], out var noteId) && int.TryParse(parts[2], out var line))
                _notes.SetChecklistItem(noteId, line, done);
        }
        else if (id.StartsWith("C:", StringComparison.Ordinal) && long.TryParse(id[2..], out var taskId))
        {
            _db.SetTaskDone(taskId, done);
        }
    }

    public sealed record ExtractResult(int added, string? error);

    /// <summary>Ask Claude to pull implicit action items from the notes
    /// (anything not already a markdown checkbox) and store them.</summary>
    public async Task<ExtractResult> ExtractAsync(CancellationToken ct = default)
    {
        var notes = _db.GetAllNotes();
        if (notes.Count == 0) return new ExtractResult(0, "No notes yet.");

        string raw;
        try { raw = await ClaudeCli.RunAsync(BuildPrompt(notes), ct); }
        catch (Exception ex) { return new ExtractResult(0, ex.Message); }

        var json = ExtractJson(raw);
        if (json is null) return new ExtractResult(0, "Could not parse Claude's response.");

        int added = 0;
        var now = DateTime.UtcNow.ToString("o");
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tasks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var valid = notes.Select(n => n.Id).ToHashSet();
                foreach (var t in arr.EnumerateArray())
                {
                    if (!t.TryGetProperty("note", out var nEl) || !t.TryGetProperty("text", out var txEl))
                        continue;
                    var nid = nEl.GetInt64();
                    var text = txEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(text) && valid.Contains(nid) && _db.AddTask(nid, text!, now))
                        added++;
                }
            }
        }
        catch (Exception ex) { return new ExtractResult(added, "Parse error: " + ex.Message); }

        return new ExtractResult(added, null);
    }

    private static (long, string, string) ClusterOf(Note n, Dictionary<long, ClusterInfo> clusters)
    {
        long cl = n.ClusterId ?? 0;
        if (cl != 0 && clusters.TryGetValue(cl, out var c)) return (cl, c.name, c.color);
        return (0, "unsorted", "#5C6370");
    }

    private static string BuildPrompt(List<Note> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Extract concrete, actionable TODO items implied by the personal notes below.");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Only genuine actions the person needs to DO (not facts or reference material).");
        sb.AppendLine("- Do NOT include items already written as markdown checkboxes.");
        sb.AppendLine("- Phrase each as a short imperative (e.g. 'Send the quarterly report').");
        sb.AppendLine("- Attribute each task to the note id it came from.");
        sb.AppendLine();
        foreach (var n in notes)
            sb.AppendLine($"[{n.Id}] {n.Title}: {Truncate(n.Body, 220)}");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object:");
        sb.AppendLine("""{ "tasks": [ { "note": 12, "text": "..." } ] }""");
        return sb.ToString();
    }

    private static string Truncate(string s, int n)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }

    private static string? ExtractJson(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        return (start < 0 || end <= start) ? null : text.Substring(start, end - start + 1);
    }
}
