using System.Text;

namespace Engram;

/// <summary>
/// Conversational "ask" over the notes: retrieval-augmented generation. Uses
/// the local embeddings to pull the most relevant notes, then asks Claude (via
/// the CLI) to answer the question grounded strictly in them, with citations
/// back to the source notes. On-demand only — one CLI call per question.
/// </summary>
internal sealed class Assistant
{
    private const int TopK = 8;
    private const int MaxBodyChars = 1500;

    private readonly NoteService _notes;

    public Assistant(NoteService notes) => _notes = notes;

    public sealed record Source(long id, string title, string color, double score);
    public sealed record Answer(string text, IReadOnlyList<Source> sources, string? error);

    public async Task<Answer> AskAsync(string question, CancellationToken ct = default)
    {
        question = (question ?? "").Trim();
        if (question.Length == 0)
            return new Answer("", Array.Empty<Source>(), "Ask a question.");

        var hits = _notes.RetrieveRelevant(question, TopK);
        if (hits.Count == 0)
            return new Answer("", Array.Empty<Source>(), "No notes yet to answer from.");

        var prompt = BuildPrompt(question, hits);
        string raw;
        try { raw = await ClaudeCli.RunAsync(prompt, ct); }
        catch (Exception ex) { return new Answer("", Array.Empty<Source>(), ex.Message); }

        var sources = hits
            .Select(h => new Source(h.note.Id, h.note.Title, h.color, Math.Round(h.score, 4)))
            .ToList();

        return new Answer(raw.Trim(), sources, null);
    }

    private static string BuildPrompt(string question, List<(Note note, double score, string color)> hits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are engram, a personal knowledge assistant. Answer the user's question");
        sb.AppendLine("using ONLY the notes below — these are the user's own notes. Rules:");
        sb.AppendLine("- If the notes don't contain enough to answer, say so plainly and note what's missing.");
        sb.AppendLine("- Be concise and direct. Synthesize across notes; don't just list them.");
        sb.AppendLine("- Cite supporting notes inline using their id in square brackets, e.g. [12].");
        sb.AppendLine();
        sb.AppendLine("=== NOTES ===");
        foreach (var h in hits)
        {
            var body = h.note.Body.Length > MaxBodyChars ? h.note.Body[..MaxBodyChars] + "…" : h.note.Body;
            sb.AppendLine($"[{h.note.Id}] {h.note.Title}");
            sb.AppendLine(body.Trim());
            sb.AppendLine("---");
        }
        sb.AppendLine();
        sb.AppendLine("=== QUESTION ===");
        sb.AppendLine(question);
        sb.AppendLine();
        sb.AppendLine("Answer:");
        return sb.ToString();
    }
}
