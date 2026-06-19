namespace Engram;

/// <summary>A captured note. Body lives both in SQLite (for search) and as a
/// markdown file on disk (greppable, portable, the source of truth for export).</summary>
public sealed record Note(
    long Id,
    string Title,
    string Path,
    string CreatedAt,
    string UpdatedAt,
    string Body,
    long? ClusterId);

/// <summary>One node in the force-graph (a note).</summary>
public sealed record GraphNode(long id, string title, long cluster, string color, string snippet);

/// <summary>One similarity edge between two notes.</summary>
public sealed record GraphLink(long source, long target, double weight);

/// <summary>An emergent cluster (community) of related notes.</summary>
public sealed record ClusterInfo(long id, string name, string color, int count);

/// <summary>Full payload handed to force-graph on the frontend.</summary>
public sealed record GraphData(
    IReadOnlyList<GraphNode> nodes,
    IReadOnlyList<GraphLink> links,
    IReadOnlyList<ClusterInfo> clusters);

/// <summary>A pending review-panel item (link/merge/rename suggestion).</summary>
public sealed record Suggestion(long id, string kind, string payload, string status, string createdAt);

/// <summary>A semantic-search hit.</summary>
public sealed record SearchHit(long id, string title, string snippet, double score, long cluster, string color);
