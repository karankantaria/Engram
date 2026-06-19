using System.IO;
using System.Reflection;

namespace Engram;

/// <summary>
/// Extracts the embedded web frontend (HTML/CSS/JS + force-graph) from the
/// assembly onto disk so WebView2 can serve it via a virtual host mapping.
/// Embedding keeps everything inside the single-file exe; extraction gives
/// WebView2 a real folder to map. Re-extracts only when content changes.
/// </summary>
internal static class AssetManager
{
    private const string Prefix = "web/";

    public static void ExtractWebAssets()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            // LogicalName uses '/' separators relative to web root.
            var relative = name.Substring(Prefix.Length).Replace('/', Path.DirectorySeparatorChar);
            var dest = Path.Combine(Paths.WebRootDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;

            // Skip rewrite if size matches (cheap freshness check during dev).
            if (File.Exists(dest) && new FileInfo(dest).Length == stream.Length)
                continue;

            using var file = File.Create(dest);
            stream.CopyTo(file);
        }
    }
}
