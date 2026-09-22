namespace SimplArchive.Presentation;

/// <summary>
/// Line arithmetic for the text preview (issue #1317) — where each logical line starts, which is what a
/// line-number gutter is drawn from. Shared because both clients must number the same text identically
/// (ADR 0650): the web pane renders a row per logical line, the desktop positions numbers at each line
/// start's measured Y — two different renderings of ONE answer to "where do the lines begin".
/// </summary>
public static class TextLines
{
    /// <summary>
    /// The character offset of every logical line start: always begins with 0 for non-null text, then one
    /// entry after each '\n'. A trailing newline therefore yields a final empty line — deliberately, because
    /// that is the line the caret would sit on and the one a log file's tail visibly has. Empty for null.
    /// </summary>
    public static IReadOnlyList<int> Starts(string? text)
    {
        if (text is null)
        {
            return [];
        }

        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }
}
