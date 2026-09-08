namespace SimplArchive.Client.Pages;

using SimplArchive.Presentation;

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
    // Pages delegate find to preview.js (word overlays); a text preview has no pages, so the matches come
    // from the shared arithmetic (SimplArchive.Presentation.TextFind) — the desktop preview answers the
    // same questions from the same code, which is what keeps the two finds from disagreeing.

    /// <summary>Match start offsets into <see cref="Text"/>, case-insensitive, capped
    /// (<see cref="TextFind.MaxMatches"/>).</summary>
    public IReadOnlyList<int> TextMatches { get; private set; } = [];

    /// <summary>Recomputes the text matches for <see cref="FindQuery"/> and resets Count/Index. No-op
    /// (clears) when the kind is not text, the query is empty, or there is no text.</summary>
    public void ApplyTextFind()
    {
        TextMatches = Kind == "text" ? TextFind.Matches(Text, FindQuery) : [];
        Count = TextMatches.Count;
        Index = TextMatches.Count > 0 ? 1 : 0;
    }

    /// <summary>Advances the active text match by <paramref name="delta"/> (±1), wrapping.</summary>
    public void CycleTextFind(int delta)
    {
        if (Count > 0)
        {
            Index = TextFind.Cycle(Index, Count, delta);
        }
    }

    /// <summary>The text split into render segments: plain runs and matches (with the active one marked) —
    /// what the pane's text branch renders.</summary>
    public IEnumerable<(string Segment, bool IsHit, bool IsActive)> TextSegments() =>
        TextFind.Segments(Text, TextMatches, FindQuery.Length, Index);

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
