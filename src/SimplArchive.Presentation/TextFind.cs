namespace SimplArchive.Presentation;

/// <summary>
/// Find-in-a-text-preview arithmetic (#1063, ADR 0651's rule): which offsets match, how the current match
/// cycles, and how the text splits into render segments. Both clients must answer these identically — the
/// web pane renders segments as inline marks, the desktop as styled runs — so the answers live here and the
/// panes keep only their own drawing. Matching is case-insensitive and non-overlapping (the next scan
/// resumes after a match), exactly what a reader expects from a browser-style find.
/// </summary>
public static class TextFind
{
    /// <summary>Cap on reported matches: a degenerate query (one letter against a huge document) must not
    /// mint tens of thousands of marks/runs in whichever pane draws them.</summary>
    public const int MaxMatches = 500;

    /// <summary>Match start offsets of <paramref name="query"/> in <paramref name="text"/> —
    /// case-insensitive, non-overlapping, capped at <see cref="MaxMatches"/>. Empty when either is empty.</summary>
    public static IReadOnlyList<int> Matches(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return [];
        }

        var matches = new List<int>();
        var at = 0;
        while (matches.Count < MaxMatches
               && (at = text.IndexOf(query, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            matches.Add(at);
            at += query.Length;
        }

        return matches;
    }

    /// <summary>Advances a 1-based current-match index by <paramref name="delta"/> (±1), wrapping; 0 stays 0
    /// (no matches).</summary>
    public static int Cycle(int index, int count, int delta) =>
        count == 0 ? 0 : ((index - 1 + delta) % count + count) % count + 1;

    /// <summary>The text split into render segments: plain runs and matches, the active one flagged.
    /// <paramref name="activeIndex"/> is 1-based (0 = none active).</summary>
    public static IEnumerable<(string Segment, bool IsHit, bool IsActive)> Segments(
        string? text, IReadOnlyList<int> matches, int queryLength, int activeIndex)
    {
        if (text is null)
        {
            yield break;
        }

        if (matches.Count == 0)
        {
            yield return (text, false, false);
            yield break;
        }

        var pos = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i];
            if (start > pos)
            {
                yield return (text[pos..start], false, false);
            }

            yield return (text.Substring(start, queryLength), true, i + 1 == activeIndex);
            pos = start + queryLength;
        }

        if (pos < text.Length)
        {
            yield return (text[pos..], false, false);
        }
    }
}
