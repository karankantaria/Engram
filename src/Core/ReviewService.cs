namespace Engram;

/// <summary>
/// Lightweight spaced-repetition resurfacing. Notes that have never been
/// reviewed surface first (oldest by age), then notes whose scheduled review is
/// due. Rating a note reschedules it with an SM-2-style interval/ease. This is
/// gentle "bring old notes back to attention," not a strict flashcard trainer.
/// </summary>
internal sealed class ReviewService
{
    private const double DefaultEase = 2.3;
    private const double MinEase = 1.3;

    private readonly Database _db;

    public ReviewService(Database db) => _db = db;

    public sealed record ReviewItem(long id, string title, string snippet, string color);

    public int DueCount() => _db.CountDue(DateTime.UtcNow.ToString("o"));

    public List<ReviewItem> GetDue(int limit = 12)
    {
        var due = _db.GetDueNotes(limit, DateTime.UtcNow.ToString("o"));
        var clusters = _db.GetClusters().ToDictionary(c => c.id);
        var byCluster = _db.GetAllNotes().ToDictionary(n => n.Id, n => n.ClusterId ?? 0);

        return due.Select(d =>
        {
            long cl = byCluster.GetValueOrDefault(d.id);
            var color = cl != 0 && clusters.TryGetValue(cl, out var c) ? c.color : "#5C6675";
            return new ReviewItem(d.id, d.title, Snippet(d.body), color);
        }).ToList();
    }

    /// <summary>Reschedule a note after review. grade: "again" | "good" | "easy".</summary>
    public void Rate(long noteId, string grade)
    {
        var state = _db.GetReview(noteId);
        double interval = state?.interval ?? 0;
        double ease = state?.ease ?? DefaultEase;
        var now = DateTime.UtcNow;

        switch (grade)
        {
            case "again":
                ease = Math.Max(MinEase, ease - 0.2);
                interval = 1;
                break;
            case "easy":
                ease += 0.1;
                interval = interval < 1 ? 3 : interval * ease * 1.3;
                break;
            default: // "good"
                interval = interval < 1 ? 1 : interval * ease;
                break;
        }

        var due = now.AddDays(interval).ToString("o");
        _db.SaveReview(noteId, interval, ease, due, now.ToString("o"));
    }

    private static string Snippet(string body)
    {
        var flat = body.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= 140 ? flat : flat[..140].TrimEnd() + "…";
    }
}
