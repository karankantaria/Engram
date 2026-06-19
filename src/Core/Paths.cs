using System.IO;

namespace Engram;

/// <summary>
/// Resolves all on-disk locations engram uses. Everything lives under
/// %APPDATA%\engram so the app is movable via the in-app export feature.
/// </summary>
internal static class Paths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "engram");

    /// <summary>Markdown note files, one per note: notes/{id}.md</summary>
    public static string NotesDir => Path.Combine(DataDir, "notes");

    /// <summary>SQLite index (notes metadata, vectors, edges, clusters, suggestions).</summary>
    public static string DbPath => Path.Combine(DataDir, "engram.db");

    /// <summary>WebView2 keeps its cache/profile here.</summary>
    public static string WebViewProfileDir => Path.Combine(DataDir, "webview2");

    /// <summary>Where embedded web assets are extracted at runtime.</summary>
    public static string WebRootDir => Path.Combine(DataDir, "web");

    /// <summary>
    /// ONNX model + tokenizer vocab. Shipped next to the exe under models\.
    /// If absent, the embedder falls back to a deterministic hash embedding.
    /// </summary>
    public static string ModelsDir => Path.Combine(AppContext.BaseDirectory, "models");
    public static string ModelPath => Path.Combine(ModelsDir, "model.onnx");
    public static string VocabPath => Path.Combine(ModelsDir, "vocab.txt");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(NotesDir);
        Directory.CreateDirectory(WebViewProfileDir);
        Directory.CreateDirectory(WebRootDir);
    }
}
