namespace SimplArchive.Client.Pages;

// The preview pane's display state (ADR "Preview pdf.js hit-overlay", 0294). The Repositories and Inbox tabs
// reuse one JS-owned preview host + this shared state, so switching tabs must reset it or one tab's preview
// would leak into the other. Extracted from Home.razor so that clear-on-switch reset is unit-testable.
public sealed class PreviewPaneState
{
    // image / pdf / text / unsupported / error.
    public string Kind { get; set; } = "";

    // Page-rendered formats (image/pdf) use the JS host; other kinds render as text or a placeholder.
    public bool HasPages => Kind is "image" or "pdf";

    public string? Text { get; set; }

    public string FindQuery { get; set; } = "";

    public int Count { get; set; }

    public int Index { get; set; }

    // True when the preview is a server-generated rendition (drives the "Converted preview" badge).
    public bool Converted { get; set; }

    public string? Url { get; set; }

    // Resets the content-bearing state so a stale preview can't render after a tab switch. Fullscreen is left
    // to the caller — exiting it is an async JS-interop side effect, not pure state.
    public void Clear()
    {
        Kind = "";
        Text = null;
        Count = 0;
        Index = 0;
        FindQuery = "";
        Converted = false;
    }
}
