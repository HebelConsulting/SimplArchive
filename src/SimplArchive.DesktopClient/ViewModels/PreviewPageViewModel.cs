using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// A colour swatch for the annotation toolbar palette (ADR "Highlighting redesign") — the hex (the command
// parameter) plus a precomputed brush for the swatch fill.
public sealed record AnnotationSwatch(string Hex)
{
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(Hex));
}

// A highlight rectangle in normalized 0..1 page coordinates (top-left origin), carrying the word it covers so
// a click can copy it. The overlay control scales the box to the rendered page size. See ADR "Search hit
// overlay" and "Copy a preview word to the clipboard".
public sealed record HighlightBox(string Text, double X, double Y, double Width, double Height);

// A sticky-note box OR a markup shape in normalized 0..1 page coordinates (ADR "Document annotations" +
// "Annotation markup" + "Post-it note boxes" + 0525). Kind 0 = a note — an always-visible coloured box at (X,Y)
// sized Width×Height showing its Text (draggable + resizable if CanEdit); Kind 1/2/3 = highlight box / rectangle /
// arrow, whose Width/Height give the extent (box size, or signed arrow end-offset); Kind 4/5/6 = stamp /
// strikethrough / text-box, also box-extent shapes (stamp + text-box render their Text); Kind 7 = a freehand
// poly-line built from Points ("x,y x,y …", each coord 0..1), which has null Width/Height (not a box).
// Selected carries the multi-select state (ADR "Annotation multi-select") so the overlay draws a selection
// outline; the selected set is owned by the PreviewViewModel and re-flowed onto the notes on every change.
public sealed record NoteBox(Guid Id, int Kind, double X, double Y, double Width, double Height, string Color, bool CanEdit, string Text = "", bool Selected = false, string? Points = null);

// Carried by the overlay's note-placement command when the user clicks a spot in add-note mode.
public sealed record NotePlacement(int PageIndex, double X, double Y);

// Multi-select gestures (ADR "Annotation multi-select"). A click/Ctrl-click on an annotation (Toggle = the
// modifier was held); a marquee drag over empty page area (the ids it enclosed, Additive if Ctrl was held); a
// group drag of the whole selection (a normalized delta applied to every selected annotation on the page).
public sealed record AnnotationSelect(Guid Id, bool Toggle);
public sealed record MarqueeSelect(int PageIndex, IReadOnlyList<Guid> Ids, bool Additive);
public sealed record AnnotationGroupMove(int PageIndex, double Dx, double Dy);

// Carried by the overlay's note-resize command when the user drags a note box's corner grip (ADR "Post-it note
// boxes") — the new normalized size.
public sealed record NoteResize(Guid Id, int PageIndex, double Width, double Height);

// Carried by the overlay's note-moved command when the author drags an existing note to a new spot.
public sealed record NoteMove(Guid Id, int PageIndex, double X, double Y);

// Carried by the overlay's shape-drawn command when a markup shape is drawn by dragging (ADR "Annotation
// markup"). Kind 1/2/3 = highlight/rectangle/arrow; X,Y = start/top-left; W,H = signed extent.
public sealed record ShapeDraw(int PageIndex, int Kind, double X, double Y, double W, double H);

// The outcome of clicking a hit word (ADR "Copy a preview word to the clipboard") — the word copied and whether it was
// appended (shift-click) rather than replacing the clipboard. Drives the status message.
public sealed record HitCopyResult(string Word, bool Appended);

// One rendered preview page (an image, or a rasterized PDF page) plus its hit-overlay: AllWords is every
// OCR/text-layer word (each clickable to copy — ADR "Copy a preview word to the clipboard"), and Highlights is the
// subset drawn for the current find query. Both are reassigned wholesale so the bound overlay control updates.
public sealed partial class PreviewPageViewModel : ObservableObject
{
    private IReadOnlyList<SimplArchiveApiClient.TextLayoutBox> _words = [];

    public PreviewPageViewModel(Bitmap image)
    {
        Image = image;
    }

    public Bitmap Image { get; }

    // This page's 0-based index (set by the owner while building the pages) — carried on a note-placement click.
    public int PageIndex { get; set; }

    // Sticky-note markers on this page (ADR "Document annotations"); drawn + hit-tested by the overlay.
    [ObservableProperty] private IReadOnlyList<NoteBox> _notes = [];

    // Drawn hit boxes (the find-query matches).
    [ObservableProperty] private IReadOnlyList<HighlightBox> _highlights = [];

    // Every word on the page, as clickable boxes — hit-tested for the copy click regardless of highlighting.
    [ObservableProperty] private IReadOnlyList<HighlightBox> _allWords = [];

    // The single "current match" (find prev/next) on this page, drawn distinctly and scrolled into view by the
    // overlay; null when the active match is on another page (ADR "Find occurrence count + prev/next").
    [ObservableProperty] private HighlightBox? _activeHighlight;

    public void SetWords(IReadOnlyList<SimplArchiveApiClient.TextLayoutBox> words)
    {
        _words = words;
        AllWords = words.Select(w => new HighlightBox(w.Text, w.X, w.Y, w.Width, w.Height)).ToList();
    }

    // Highlights every word whose text contains any of the query terms (case-insensitive), so e.g. "invoice"
    // also lights up "Invoices". Empty/blank query clears the overlay.
    public void ApplyQuery(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0 || _words.Count == 0)
        {
            Highlights = [];
            return;
        }

        Highlights = _words
            .Where(w => terms.Any(t => w.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .Select(w => new HighlightBox(w.Text, w.X, w.Y, w.Width, w.Height))
            .ToList();
    }
}
