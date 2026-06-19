using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Engram;

/// <summary>
/// The on-demand "librarian": shells out to the `claude` CLI to name the
/// emergent clusters and propose links/merges. Never called per-note — only
/// when the user asks (button / reindex). Cluster names are applied directly
/// (cheap, reversible); link/merge proposals go to the review panel.
/// </summary>
internal sealed class Librarian
{
    private readonly Database _db;

    public Librarian(Database db) => _db = db;

    public sealed record Result(int named, int links, int merges, string? error);

    public async Task<Result> RunAsync(CancellationToken ct = default)
    {
        var clusters = _db.GetClusters().Where(c => c.count > 0).ToList();
        var notes = _db.GetAllNotes();
        if (notes.Count == 0) return new Result(0, 0, 0, "No notes yet.");

        var prompt = BuildPrompt(clusters, notes);
        string raw;
        try { raw = await InvokeClaudeAsync(prompt, ct); }
        catch (Exception ex) { return new Result(0, 0, 0, ex.Message); }

        var json = ExtractJson(raw);
        if (json is null) return new Result(0, 0, 0, "Could not parse Claude's response.");

        int named = 0, links = 0, merges = 0;
        var now = DateTime.UtcNow.ToString("o");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("clusters", out var cl) && cl.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cl.EnumerateArray())
                {
                    if (c.TryGetProperty("id", out var idEl) && c.TryGetProperty("name", out var nameEl))
                    {
                        var name = nameEl.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            _db.RenameCluster(idEl.GetInt64(), name!.Trim());
                            named++;
                        }
                    }
                }
            }

            _db.ClearPendingSuggestions();

            if (root.TryGetProperty("links", out var lk) && lk.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in lk.EnumerateArray())
                {
                    _db.AddSuggestion("link", l.GetRawText(), now);
                    links++;
                }
            }
            if (root.TryGetProperty("merges", out var mg) && mg.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mg.EnumerateArray())
                {
                    _db.AddSuggestion("merge", m.GetRawText(), now);
                    merges++;
                }
            }
        }
        catch (Exception ex)
        {
            return new Result(named, links, merges, "Parse error: " + ex.Message);
        }

        return new Result(named, links, merges, null);
    }

    private static string BuildPrompt(List<ClusterInfo> clusters, List<Note> notes)
    {
        var byCluster = notes.GroupBy(n => n.ClusterId ?? 0).ToDictionary(g => g.Key, g => g.ToList());
        var sb = new StringBuilder();
        sb.AppendLine("You are the librarian for a personal note-taking app called engram.");
        sb.AppendLine("Below are emergent clusters of notes (grouped automatically by semantic similarity).");
        sb.AppendLine("Each note has an id, title, and snippet.");
        sb.AppendLine();
        foreach (var c in clusters)
        {
            sb.AppendLine($"## Cluster {c.id} (currently \"{c.name}\")");
            if (byCluster.TryGetValue(c.id, out var ns))
                foreach (var n in ns)
                    sb.AppendLine($"- [{n.Id}] {n.Title}: {Truncate(n.Body, 160)}");
            sb.AppendLine();
        }
        sb.AppendLine("Tasks:");
        sb.AppendLine("1. Give each cluster a short, human, descriptive name (2-4 words).");
        sb.AppendLine("2. Suggest up to 8 links between specific notes that are related but may be in different clusters.");
        sb.AppendLine("3. Suggest up to 5 merges of near-duplicate notes.");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object, no prose, in exactly this shape:");
        sb.AppendLine("""
        {
          "clusters": [ { "id": 1, "name": "..." } ],
          "links":    [ { "a": 12, "b": 34, "reason": "..." } ],
          "merges":   [ { "from": 12, "to": 34, "reason": "..." } ]
        }
        """);
        return sb.ToString();
    }

    private static async Task<string> InvokeClaudeAsync(string prompt, CancellationToken ct)
    {
        var exe = ResolveClaudeExe();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Paths.DataDir,
        };
        psi.ArgumentList.Add("-p");                       // print mode (non-interactive)
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("text");

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        await proc.StandardInput.WriteAsync(prompt);
        proc.StandardInput.Close();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0 && string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException($"claude exited {proc.ExitCode}: {stderr}");
        return stdout;
    }

    private static string ResolveClaudeExe()
    {
        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        return File.Exists(local) ? local : "claude";
    }

    private static string? ExtractJson(string text)
    {
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return text.Substring(start, end - start + 1);
    }

    private static string Truncate(string s, int n)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }
}
