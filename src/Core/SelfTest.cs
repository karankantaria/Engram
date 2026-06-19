using System.IO;
using System.Text;

namespace Engram;

/// <summary>
/// Headless smoke test of the capture → embed → link → cluster → search loop.
/// Runs in a throwaway temp directory (does not touch the real %APPDATA% data)
/// and writes a human-readable report next to the exe as selftest.out.
/// Invoke with: engram.exe --selftest
/// </summary>
internal static class SelfTest
{
    public static void Run()
    {
        var sb = new StringBuilder();
        var tempDir = Path.Combine(Path.GetTempPath(), "engram-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");

        try
        {
            using var db = new Database(dbPath);
            var embedder = EmbedderFactory.Create();
            sb.AppendLine($"embedder backend : {embedder.Backend} (dim {embedder.Dim})");

            // Notes land in temp, not in the real %APPDATA% data dir.
            var notes = new NoteService(db, embedder, Path.Combine(tempDir, "notes"));

            // Three themes that should separate into clusters.
            string[] seed =
            {
                "Depreciation reduces the book value of a fixed asset over its useful life",
                "Straight-line depreciation spreads cost evenly across accounting periods",
                "Accruals match revenue to the period in which it was earned",
                "Transformer attention lets the model weigh tokens by relevance",
                "Large language models are trained to predict the next token in a sequence",
                "Fine-tuning adapts a pretrained model to a specific downstream task",
                "Stand-up meeting moved to 9:30am on Mondays",
                "Need to send the quarterly report to the team by Friday",
            };
            foreach (var s in seed) notes.Capture(s);

            var graph = notes.GetGraph();
            sb.AppendLine($"notes captured   : {graph.nodes.Count}");
            sb.AppendLine($"edges formed     : {graph.links.Count}");
            sb.AppendLine($"clusters         : {graph.clusters.Count}");
            sb.AppendLine();
            sb.AppendLine("clusters:");
            foreach (var c in graph.clusters)
                sb.AppendLine($"  [{c.id}] {c.name} ({c.count})");

            sb.AppendLine();
            sb.AppendLine("search 'amortization of asset value':");
            foreach (var h in notes.Search("amortization of asset value", 3))
                sb.AppendLine($"  {h.score:F3}  {h.title}");

            sb.AppendLine();
            sb.AppendLine("search 'neural network token prediction':");
            foreach (var h in notes.Search("neural network token prediction", 3))
                sb.AppendLine($"  {h.score:F3}  {h.title}");

            // Import: a structured markdown file (heading split) + an
            // unstructured text file (paragraph-merge chunking).
            var importDir = Path.Combine(tempDir, "import");
            Directory.CreateDirectory(importDir);
            File.WriteAllText(Path.Combine(importDir, "structured.md"),
                "# Budgets\nA budget forecasts income and expenditure.\n\n" +
                "## Variance\nVariance is the gap between budgeted and actual figures.\n\n" +
                "## Cash flow\nCash flow tracks money moving in and out over time.");
            File.WriteAllText(Path.Combine(importDir, "loose.txt"),
                "Random thought about gradient descent stepping down the loss surface.\n\n" +
                "Unrelated note: remember to renew the parking permit next month.");

            int before = notes.GetAllNotes().Count;
            var imp = notes.ImportFiles(new[] { importDir });
            int after = notes.GetAllNotes().Count;
            sb.AppendLine();
            sb.AppendLine($"import           : {imp.files} files → {imp.notes} notes (skipped {imp.skipped})");
            sb.AppendLine($"notes total      : {before} → {after}");

            // TODO: local checkbox scan + write-back round-trip.
            var todoSvc = new TodoService(db, notes);
            notes.Capture("Shopping list\n- [ ] buy milk\n- [x] call the bank\n- [ ] email the tutor");
            var todos = todoSvc.GetTodos();
            int localCount = todos.Count(t => t.source == "local");
            var milk = todos.First(t => t.text.Contains("buy milk"));
            todoSvc.Toggle(milk.id, true); // complete it → should write [x] back to the note
            bool milkDone = todoSvc.GetTodos().First(t => t.text.Contains("buy milk")).done;
            sb.AppendLine();
            sb.AppendLine($"todos (local)    : {localCount} found; 'buy milk' done after toggle = {milkDone}");

            bool ok = after > before && imp.notes >= 4 && localCount == 3 && milkDone;
            sb.AppendLine();
            sb.AppendLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        }
        catch (Exception ex)
        {
            sb.AppendLine("RESULT: FAIL");
            sb.AppendLine(ex.ToString());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }

        var outPath = Path.Combine(AppContext.BaseDirectory, "selftest.out");
        File.WriteAllText(outPath, sb.ToString());
    }

    /// <summary>Diagnostic for one file: report extracted length + chunk count.</summary>
    public static void ExtractOne(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"file: {path}");
        var ex = FileImporter.Extract(path);
        if (ex is null)
        {
            sb.AppendLine("RESULT: skipped (unsupported/binary/empty)");
        }
        else
        {
            var chunks = Chunker.Chunk(ex.Value.Text, ex.Value.Markdown);
            sb.AppendLine($"extracted chars : {ex.Value.Text.Length}");
            sb.AppendLine($"markdown        : {ex.Value.Markdown}");
            sb.AppendLine($"chunks          : {chunks.Count}");
            sb.AppendLine();
            sb.AppendLine("--- first 400 chars ---");
            sb.AppendLine(ex.Value.Text[..Math.Min(400, ex.Value.Text.Length)]);
            sb.AppendLine();
            sb.AppendLine(chunks.Count > 0 ? "RESULT: PASS" : "RESULT: FAIL (no chunks)");
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "extract.out"), sb.ToString());
    }

    /// <summary>End-to-end RAG check: seeds notes, asks Claude a real question,
    /// writes the grounded answer + cited sources to ask.out. Makes ONE real
    /// `claude` CLI call. Invoke: engram.exe --ask "your question"</summary>
    public static void AskOne(string question)
    {
        var sb = new StringBuilder();
        var tempDir = Path.Combine(Path.GetTempPath(), "engram-ask-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        try
        {
            using var db = new Database(Path.Combine(tempDir, "ask.db"));
            var embedder = EmbedderFactory.Create();
            var notes = new NoteService(db, embedder, Path.Combine(tempDir, "notes"));
            foreach (var s in new[]
            {
                "Depreciation reduces the book value of a fixed asset over its useful life",
                "Straight-line depreciation spreads an asset's cost evenly across accounting periods",
                "Accruals match revenue to the period in which it was earned, not when cash moves",
                "Transformer attention lets a model weigh tokens by relevance to each other",
                "Fine-tuning adapts a pretrained model to a specific downstream task",
            }) notes.Capture(s);

            var assistant = new Assistant(notes);
            var ans = assistant.AskAsync(question).GetAwaiter().GetResult();

            sb.AppendLine($"Q: {question}");
            sb.AppendLine();
            if (ans.error != null) { sb.AppendLine("ERROR: " + ans.error); sb.AppendLine("RESULT: FAIL"); }
            else
            {
                sb.AppendLine("A: " + ans.text);
                sb.AppendLine();
                sb.AppendLine("sources:");
                foreach (var s in ans.sources) sb.AppendLine($"  [{s.id}] {s.score:F3}  {s.title}");
                sb.AppendLine();
                sb.AppendLine(string.IsNullOrWhiteSpace(ans.text) ? "RESULT: FAIL (empty answer)" : "RESULT: PASS");
            }
        }
        catch (Exception ex) { sb.AppendLine("RESULT: FAIL"); sb.AppendLine(ex.ToString()); }
        finally { try { Directory.Delete(tempDir, true); } catch { } }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ask.out"), sb.ToString());
    }
}
