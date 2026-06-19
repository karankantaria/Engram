using System.Text.RegularExpressions;

namespace Engram;

/// <summary>
/// Finds markdown checkbox tasks (<c>- [ ]</c> / <c>- [x]</c>) inside a note
/// body and toggles them in place. These are the "local" TODOs — the markdown
/// stays the source of truth, so completion round-trips back into the file.
/// </summary>
internal static partial class TaskScanner
{
    // groups: 1=prefix "- [", 2=mark, 3="] ", 4=text
    [GeneratedRegex(@"^(\s*[-*+]\s*\[)([ xX])(\]\s+)(.+?)\s*$")]
    private static partial Regex Checkbox();

    public readonly record struct Item(int line, string text, bool done);

    public static List<Item> Scan(string body)
    {
        var items = new List<Item>();
        var lines = body.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Checkbox().Match(lines[i]);
            if (m.Success)
                items.Add(new Item(i, m.Groups[4].Value.Trim(), m.Groups[2].Value is not " "));
        }
        return items;
    }

    /// <summary>Rewrite a single checkbox line to the requested done state.</summary>
    public static string SetLine(string line, bool done)
    {
        var m = Checkbox().Match(line);
        if (!m.Success) return line;
        return m.Groups[1].Value + (done ? "x" : " ") + m.Groups[3].Value + m.Groups[4].Value;
    }
}
