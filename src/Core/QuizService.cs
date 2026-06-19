using System.Text;
using System.Text.Json;

namespace Engram;

/// <summary>
/// Generates flashcards (Q&amp;A) from a cluster's notes via Claude, for active
/// recall. Cards are cached per cluster (regenerated on demand) in the
/// flashcards table. On-demand only — one CLI call per generation.
/// </summary>
internal sealed class QuizService
{
    private readonly Database _db;

    public QuizService(Database db) => _db = db;

    public sealed record Card(long id, string question, string answer);
    public sealed record QuizResult(IReadOnlyList<Card> cards, string? error);

    public async Task<QuizResult> GetOrGenerateAsync(long clusterId, bool regenerate, CancellationToken ct = default)
    {
        if (!regenerate)
        {
            var existing = _db.GetFlashcards(clusterId);
            if (existing.Count > 0)
                return new QuizResult(existing.Select(c => new Card(c.id, c.question, c.answer)).ToList(), null);
        }

        var notes = _db.GetAllNotes().Where(n => (n.ClusterId ?? 0) == clusterId).ToList();
        if (notes.Count == 0) return new QuizResult(Array.Empty<Card>(), "This cluster has no notes.");

        string raw;
        try { raw = await ClaudeCli.RunAsync(BuildPrompt(notes), ct); }
        catch (Exception ex) { return new QuizResult(Array.Empty<Card>(), ex.Message); }

        var json = ExtractJson(raw);
        if (json is null) return new QuizResult(Array.Empty<Card>(), "Could not parse Claude's response.");

        var pairs = new List<(string q, string a)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("cards", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var q = c.TryGetProperty("q", out var qe) ? qe.GetString() : null;
                    var a = c.TryGetProperty("a", out var ae) ? ae.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(q) && !string.IsNullOrWhiteSpace(a))
                        pairs.Add((q!.Trim(), a!.Trim()));
                }
            }
        }
        catch (Exception ex) { return new QuizResult(Array.Empty<Card>(), "Parse error: " + ex.Message); }

        if (pairs.Count == 0) return new QuizResult(Array.Empty<Card>(), "No cards generated.");

        _db.ReplaceFlashcards(clusterId, pairs, DateTime.UtcNow.ToString("o"));
        return new QuizResult(
            _db.GetFlashcards(clusterId).Select(c => new Card(c.id, c.question, c.answer)).ToList(), null);
    }

    private static string BuildPrompt(List<Note> notes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Create flashcards for active recall from the personal study notes below.");
        sb.AppendLine("Rules:");
        sb.AppendLine("- Each card: a clear question and a concise, correct answer drawn from the notes.");
        sb.AppendLine("- Test understanding, not trivia. 1 card per distinct idea; up to 12 cards.");
        sb.AppendLine("- Do not invent facts beyond the notes.");
        sb.AppendLine();
        foreach (var n in notes)
            sb.AppendLine($"[{n.Id}] {n.Title}: {Truncate(n.Body, 400)}");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY a JSON object:");
        sb.AppendLine("""{ "cards": [ { "q": "...", "a": "..." } ] }""");
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
