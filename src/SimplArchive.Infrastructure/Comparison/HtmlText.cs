using System.Net;
using System.Text.RegularExpressions;

namespace SimplArchive.Infrastructure.Comparison;

// Reduces an email's HTML-only body to readable plain text for the comparison path (ADR 0712). Deliberately
// small: block-level boundaries become line breaks, tags go, entities decode. This is not an HTML renderer —
// it serves a diff of prose a mail client wrapped in markup, where the words and their line structure are what
// the user wants compared. Stored .html FILES are not routed through here (they diff as source).
public static partial class HtmlText
{
    [GeneratedRegex(@"<\s*(script|style)\b[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<\s*(?:br|/p|/div|/li|/h[1-6]|/tr)\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBoundary();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex RunsOfSpace();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex RunsOfBlankLines();

    public static string Strip(string html)
    {
        var text = ScriptOrStyle().Replace(html, string.Empty);
        text = BlockBoundary().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = RunsOfSpace().Replace(text, " ");
        text = string.Join('\n', text.Split('\n').Select(l => l.Trim()));
        return RunsOfBlankLines().Replace(text, "\n\n").Trim();
    }
}
