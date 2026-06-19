using System.Text;
using System.Text.RegularExpressions;

namespace Engram;

/// <summary>
/// Splits an imported document into note-sized chunks. Structured markdown is
/// cut at headings (each section becomes a note); unstructured prose is merged
/// paragraph-by-paragraph toward a target size so we get atomic-but-not-tiny
/// notes. Over-long paragraphs are split at sentence boundaries.
/// </summary>
internal static partial class Chunker
{
    private const int Target = 1200;  // preferred chunk size (chars)
    private const int Max = 2200;     // hard ceiling before forced split

    [GeneratedRegex(@"(?m)^\s{0,3}#{1,6}\s")]
    private static partial Regex HeadingLine();

    [GeneratedRegex(@"\r?\n\s*\r?\n")]
    private static partial Regex ParaBreak();

    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex SentenceBreak();

    public static List<string> Chunk(string text, bool markdown)
    {
        text = (text ?? "").Replace("\r\n", "\n").Trim();
        if (text.Length == 0) return new();

        var chunks = (markdown && HeadingLine().IsMatch(text))
            ? SplitByHeadings(text)
            : MergeParagraphs(text);

        return chunks.Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
    }

    private static List<string> SplitByHeadings(string text)
    {
        var sections = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            bool isHeading = HeadingLine().IsMatch(line);
            if (isHeading && sb.Length > 0)
            {
                sections.Add(sb.ToString());
                sb.Clear();
            }
            sb.AppendLine(line);
        }
        if (sb.Length > 0) sections.Add(sb.ToString());

        // A heading section may still be huge; chunk those further.
        var result = new List<string>();
        foreach (var s in sections)
        {
            if (s.Length <= Max) result.Add(s);
            else result.AddRange(MergeParagraphs(s));
        }
        return result;
    }

    private static List<string> MergeParagraphs(string text)
    {
        var paras = ParaBreak().Split(text)
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .ToList();

        var result = new List<string>();
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length > 0) { result.Add(buffer.ToString().Trim()); buffer.Clear(); }
        }

        foreach (var p in paras)
        {
            if (p.Length > Max)
            {
                Flush();
                result.AddRange(SplitLong(p));
                continue;
            }
            if (buffer.Length == 0)
                buffer.Append(p);
            else if (buffer.Length + p.Length + 2 <= Target)
                buffer.Append("\n\n").Append(p);
            else
            {
                Flush();
                buffer.Append(p);
            }
        }
        Flush();
        return result;
    }

    private static IEnumerable<string> SplitLong(string para)
    {
        var sentences = SentenceBreak().Split(para);
        var buffer = new StringBuilder();
        foreach (var s in sentences)
        {
            var sentence = s;
            // A single monster sentence: hard-split by length.
            while (sentence.Length > Max)
            {
                if (buffer.Length > 0) { yield return buffer.ToString().Trim(); buffer.Clear(); }
                yield return sentence[..Max];
                sentence = sentence[Max..];
            }
            if (buffer.Length + sentence.Length + 1 <= Target)
                buffer.Append(buffer.Length > 0 ? " " : "").Append(sentence);
            else
            {
                if (buffer.Length > 0) { yield return buffer.ToString().Trim(); buffer.Clear(); }
                buffer.Append(sentence);
            }
        }
        if (buffer.Length > 0) yield return buffer.ToString().Trim();
    }
}
