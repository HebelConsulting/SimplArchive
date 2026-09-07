namespace SimplArchive.Client.Pages;

// The preview pane's display state (ADR "Preview pdf.js hit-overlay", 0294) — one instance per PreviewPane.
//
// It used to be a single SHARED object: the Repositories and Intray tabs took turns with one JS-owned host, so
// switching tabs had to reset this or one tab's preview leaked into the other's. Extracting PreviewPane gave
// each tab its own host and its own state (ADR 0558), which is what actually removed that hazard — Clear() now
// exists for the ordinary case of "the selection went away", not to stop a leak between tabs. It stays a
// separate type so that reset remains unit-testable without a renderer.
public sealed class PreviewPaneState
{
    // image / pdf / text / unsupported / error.
    public string Kind { get; set; } = string.Empty;

    // Page-rendered formats (image/pdf) use the JS host; other kinds render as text or a placeholder.
    public bool HasPages => Kind is "image" or "pdf";

    public string? Text { get; set; }

    public string FindQuery { get; set; } = string.Empty;

    public int Count { get; set; }

    public int Index { get; set; }

    // ---- Find in a TEXT preview (#1063) ----------------------------------------------------------------
    // Pages delegate find to preview.js (word overlays); a text preview has no pages, so the matches are
    // computed here, purely, against the decoded text — which is what makes this testable without a browser.

    /// <summary>Match start offsets into <see cref="Text"/>, case-insensitive. Capped: a degenerate query
    /// (one letter against a huge document) must not mint tens of thousands of DOM marks.</summary>
    public IReadOnlyList<int> TextMatches { get; private set; } = [];

    public const int MaxTextMatches = 500;

    /// <summary>Recomputes the text matches for <see cref="FindQuery"/> and resets Count/Index. No-op
    /// (clears) when the kind is not text, the query is empty, or there is no text.</summary>
    public void ApplyTextFind()
    {
        if (Kind != "text" || string.IsNullOrEmpty(FindQuery) || string.IsNullOrEmpty(Text))
        {
            TextMatches = [];
            Count = 0;
            Index = 0;
            return;
        }

        var matches = new List<int>();
        var at = 0;
        while (matches.Count < MaxTextMatches
               && (at = Text.IndexOf(FindQuery, at, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            matches.Add(at);
            at += FindQuery.Length;
        }

        TextMatches = matches;
        Count = matches.Count;
        Index = matches.Count > 0 ? 1 : 0;
    }

    /// <summary>Advances the active text match by <paramref name="delta"/> (±1), wrapping.</summary>
    public void CycleTextFind(int delta)
    {
        if (Count > 0)
        {
            Index = ((Index - 1 + delta) % Count + Count) % Count + 1;
        }
    }

    /// <summary>The text split into render segments: plain runs and matches (with the active one marked) —
    /// what the pane's text branch renders, kept pure so the split logic is unit-tested.</summary>
    public IEnumerable<(string Segment, bool IsHit, bool IsActive)> TextSegments()
    {
        if (Text is null)
        {
            yield break;
        }

        if (TextMatches.Count == 0)
        {
            yield return (Text, false, false);
            yield break;
        }

        var pos = 0;
        for (var i = 0; i < TextMatches.Count; i++)
        {
            var start = TextMatches[i];
            if (start > pos)
            {
                yield return (Text[pos..start], false, false);
            }

            yield return (Text.Substring(start, FindQuery.Length), true, i + 1 == Index);
            pos = start + FindQuery.Length;
        }

        if (pos < Text.Length)
        {
            yield return (Text[pos..], false, false);
        }
    }

    // True when the preview is a server-generated rendition (drives the "Converted preview" badge).
    public bool Converted { get; set; }

    public string? Url { get; set; }

    // Resets the content-bearing state so a stale preview can't render after a tab switch. Fullscreen is left
    // to the caller — exiting it is an async JS-interop side effect, not pure state.
    public void Clear()
    {
        Kind = string.Empty;
        Text = null;
        Count = 0;
        Index = 0;
        FindQuery = string.Empty;
        TextMatches = [];
        Converted = false;
    }
}
