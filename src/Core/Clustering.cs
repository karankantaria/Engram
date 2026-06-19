namespace Engram;

/// <summary>
/// Emergent categories via label-propagation community detection over the
/// similarity graph. Deterministic (fixed node ordering + lowest-id tie-break)
/// so re-running on unchanged data gives stable results. Isolated notes become
/// their own singleton cluster (candidates for "orphan rescue" later).
/// </summary>
internal static class Clustering
{
    // Brand-harmonized palette, cycled by cluster index. Leads with engram's
    // own node colors (green/cyan/amber/magenta), then a wide, distinct spread
    // so many clusters stay visually separable.
    private static readonly string[] Palette =
    {
        "#43E08B", "#5CC7F5", "#F5B657", "#F07CC0", // brand node colors
        "#6CF0EA", "#BFA0FF", "#FF8F6B", "#9CE85C",
        "#7C9CFF", "#FFD479", "#E879F9", "#4FD6B8",
        "#FF6B8A", "#B8E04F", "#5CD0FF", "#C68BFF",
        "#F2A65A", "#59E0C4", "#86A8FF", "#FF9EC4",
    };

    public static (List<(long clusterId, string name, string color)> clusters,
                   Dictionary<long, long> noteToCluster)
        Compute(IReadOnlyCollection<long> noteIds, IReadOnlyList<GraphLink> edges)
    {
        var nodes = noteIds.OrderBy(x => x).ToArray();
        var adj = new Dictionary<long, List<(long nb, double w)>>();
        foreach (var id in nodes) adj[id] = new List<(long, double)>();
        foreach (var e in edges)
        {
            if (!adj.ContainsKey(e.source) || !adj.ContainsKey(e.target)) continue;
            adj[e.source].Add((e.target, e.weight));
            adj[e.target].Add((e.source, e.weight));
        }

        // Label propagation.
        var label = nodes.ToDictionary(n => n, n => n);
        for (int iter = 0; iter < 50; iter++)
        {
            bool changed = false;
            foreach (var n in nodes)
            {
                if (adj[n].Count == 0) continue;
                var score = new Dictionary<long, double>();
                foreach (var (nb, w) in adj[n])
                {
                    var lab = label[nb];
                    score[lab] = score.GetValueOrDefault(lab) + w;
                }
                // Pick highest-weight label; tie-break to the smallest label id.
                long best = label[n];
                double bestScore = double.NegativeInfinity;
                foreach (var kv in score.OrderBy(k => k.Key))
                {
                    if (kv.Value > bestScore + 1e-9)
                    {
                        bestScore = kv.Value;
                        best = kv.Key;
                    }
                }
                if (best != label[n]) { label[n] = best; changed = true; }
            }
            if (!changed) break;
        }

        // Compact raw labels into 1..k cluster ids (ordered by smallest member).
        var groups = label.GroupBy(kv => kv.Value)
                          .OrderBy(g => g.Min(kv => kv.Key))
                          .ToList();
        var clusters = new List<(long, string, string)>();
        var noteToCluster = new Dictionary<long, long>();
        long cid = 1;
        foreach (var g in groups)
        {
            var color = Palette[(int)((cid - 1) % Palette.Length)];
            clusters.Add((cid, $"cluster {cid}", color));
            foreach (var kv in g) noteToCluster[kv.Key] = cid;
            cid++;
        }
        return (clusters, noteToCluster);
    }
}
