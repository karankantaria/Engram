using System.IO;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Engram;

/// <summary>
/// Extracts plain text from a dropped/selected file so it can be chunked into
/// notes. Handles PDF (via PdfPig) and any UTF-8 text-like file; binary files
/// (images, archives, executables) are skipped.
/// </summary>
internal static class FileImporter
{
    private static readonly HashSet<string> MarkdownExt = new(StringComparer.OrdinalIgnoreCase)
        { ".md", ".markdown", ".mdown", ".mkd" };

    private static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".svg",
        ".zip", ".rar", ".7z", ".gz", ".tar", ".exe", ".dll", ".bin",
        ".mp3", ".mp4", ".mov", ".avi", ".wav", ".docx", ".xlsx", ".pptx",
    };

    public readonly record struct Extracted(string Text, bool Markdown);

    public static Extracted? Extract(string path)
    {
        var ext = Path.GetExtension(path);
        if (SkipExt.Contains(ext)) return null;

        try
        {
            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var text = ExtractPdf(path);
                return string.IsNullOrWhiteSpace(text) ? null : new Extracted(text, false);
            }

            var bytes = File.ReadAllBytes(path);
            if (LooksBinary(bytes)) return null;
            var content = Encoding.UTF8.GetString(bytes);
            if (string.IsNullOrWhiteSpace(content)) return null;
            return new Extracted(content, MarkdownExt.Contains(ext));
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractPdf(string path)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            // ContentOrderTextExtractor preserves reading order better than page.Text.
            sb.AppendLine(ContentOrderTextExtractor.GetText(page));
            sb.AppendLine(); // page break → paragraph break for the chunker
        }
        return sb.ToString();
    }

    /// <summary>Heuristic: a NUL byte in the first 8KB means it's not text.</summary>
    private static bool LooksBinary(byte[] bytes)
    {
        int n = Math.Min(bytes.Length, 8192);
        for (int i = 0; i < n; i++)
            if (bytes[i] == 0) return true;
        return false;
    }
}
