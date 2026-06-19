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

            sb.AppendLine();
            sb.AppendLine("RESULT: PASS");
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
}
