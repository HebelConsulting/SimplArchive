using System.Collections.ObjectModel;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SimplArchive.DesktopClient.Services;

namespace SimplArchive.DesktopClient.ViewModels;

// The preview surface (pages/text + toolbar with find-in-document, hit-overlay and full-screen toggle),
// extracted from MainWindowViewModel so each workbench surface can own an INDEPENDENT preview — the
// Repositories/Intray tabs share one instance, and the Recycle bin tab has its own, so their previews are never
// entangled (an explicit requirement of ADR "Desktop recycle bin parity"; mirrors the web's separate _rbPv,
// ADR 0329). Reused by the PreviewPane UserControl (its DataContext) both docked and full-screen.
public sealed partial class PreviewViewModel : ObservableObject
{
    // The authenticated api client — set by the owner after login (and in test hooks). Rendering needs it for
    // the multi-page/preview-pages, download, and text-layout calls.
    public SimplArchiveApiClient? Api { get; set; }

    // Optional status sink (the owner's status bar) — used for the "copied to clipboard" confirmation.
    private readonly IStatusReporter _status;

    public PreviewViewModel(IStatusReporter status) => _status = status;

    // Preview: exactly one of pages/text is shown; otherwise the placeholder. PreviewPages holds one page per
    // image, or one per PDF page (all pages, stacked) — each carrying its bitmap plus the search hit-overlay.
    public ObservableCollection<PreviewPageViewModel> PreviewPages { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasPreview))][NotifyPropertyChangedFor(nameof(HasWatermark))] private bool _hasPreviewPages;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasPreview))][NotifyPropertyChangedFor(nameof(HasWatermark))] private string? _previewText;
    [ObservableProperty] private string? _previewPlaceholder = "Select a document.";
    [ObservableProperty] private bool _previewConverted;

    /// <summary>
    /// True when the bytes on display arrived as a CMS envelope and were opened by the reader's key.
    /// </summary>
    /// <remarks>
    /// Two things read it. The card-removal path discards what the card decrypted and must leave everything else
    /// alone (#1450) — it used to discard every preview, so pulling a card blanked a plaintext document on a
    /// tenant with no encryption at all. And the preview draws a lock over it (#1452), which is why this is
    /// OBSERVABLE rather than a plain property: a badge bound to a non-notifying property shows whatever was true
    /// when the pane was first built and never changes again, which for a security marker is the worst of the
    /// three possible behaviours.
    /// </remarks>
    [ObservableProperty] private bool _contentWasEnveloped;

    // Text-path line wrapping (#1317). ON by default — that is what fixes the silent clipping this pane had
    // (no wrap, no horizontal scrollbar: a long line was simply unreachable) — and EPHEMERAL per document,
    // the fullscreen toggle's lifetime (ADRs 0295/0297), reset by OnPreviewTextChanged below. The horizontal
    // scrollbar FOLLOWS from it in the view rather than being a second control.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(PreviewWrapIcon))] private bool _previewWrap = true;

    public string PreviewWrapIcon => PreviewWrap ? "mdi-wrap" : "mdi-wrap-disabled";

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void TogglePreviewWrap() => PreviewWrap = !PreviewWrap;

    partial void OnPreviewTextChanged(string? value)
    {
        PreviewWrap = true;

        // The matches belong to the PREVIOUS text; runs before the PropertyChanged the view renders on, so
        // a stale offset can never be cut out of the new (possibly shorter) text — which is a Substring
        // crash, found live by the wrap hook (#1317): set a text, search it, set a shorter text.
        _textMatches = Presentation.TextFind.Matches(value, FindQuery);
        FindCount = _textMatches.Count;
        FindIndex = FindCount > 0 ? 0 : -1;
    }

    // Sensitivity watermark (ADR "Document watermarking") — the "<LABEL> · <viewer>" text, empty when the document
    // isn't Confidential/Restricted. The overlay tiles it diagonally over the preview (client-side only).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasWatermark))][NotifyPropertyChangedFor(nameof(WatermarkTiles))] private string _watermarkText = string.Empty;
    public bool HasWatermark => !string.IsNullOrEmpty(WatermarkText) && HasPreview;
    public System.Collections.Generic.IReadOnlyList<string> WatermarkTiles =>
        string.IsNullOrEmpty(WatermarkText) ? [] : System.Linq.Enumerable.Repeat(WatermarkText, 48).ToArray();

    // There is something to preview (pages or text) — gates the preview toolbar (find + full-screen toggle).
    public bool HasPreview => HasPreviewPages || PreviewText is not null;

    // In-app full-screen of the preview pane (ADR "Desktop preview full-screen toggle"): the preview covers the
    // ribbon + panes (the bottom tab strip stays reachable). Esc / the toggle / a tab switch exit.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewFullscreenIcon))]
    [NotifyPropertyChangedFor(nameof(PreviewFullscreenTip))]
    private bool _previewFullscreen;

    public string PreviewFullscreenIcon => PreviewFullscreen ? "mdi-fullscreen-exit" : "mdi-fullscreen";
    public string PreviewFullscreenTip => PreviewFullscreen ? "Exit full screen (Esc)" : "Full screen";

    [RelayCommand]
    private void TogglePreviewFullscreen() => PreviewFullscreen = !PreviewFullscreen;

    public void ExitFullscreen() => PreviewFullscreen = false;

    // --- Zoom (#480, ADR "Fit the whole page") -------------------------------------------------------------
    // The page is drawn at an explicit width — the pane's width times the zoom — rather than left to stretch, so
    // the zoom has something to act on. The hit and annotation overlays sit in the same grid cell and are
    // normalized 0..1 over their own bounds, so they follow the page for free and no overlay math changes.
    //
    // The scale that fits a whole page cannot be assumed: the panes are user-resizable and the page's own aspect
    // comes from the document, so both come from measurement — the pane from the ScrollViewer's viewport (pushed
    // in by the view), the aspect from the rendered first page.
    private Size _viewport;
    private double _zoomFloor = 1;

    [ObservableProperty][NotifyPropertyChangedFor(nameof(PageWidth))] private double _zoom = 1;

    // The width every page is drawn at. NaN — Avalonia's "Auto" — until the pane has been measured, so an
    // unmeasured preview lays out exactly as it did before zoom existed instead of collapsing to nothing.
    public double PageWidth => PageBaseWidth > 0 ? PageBaseWidth * Zoom : double.NaN;

    // The width fit-width means: the viewport less the page item's own margin.
    private double PageBaseWidth => _viewport.Width - PreviewZoom.PageMargin;

    // Pushed in by the view whenever the pages ScrollViewer is measured.
    //
    // A degenerate size is ignored rather than stored: the docked pane and the full-screen overlay are two
    // PreviewPanes bound to this ONE view model, and the one being hidden reports an empty viewport on its way
    // out — which would otherwise take the page width back to Auto just as the other one appears.
    public void SetViewport(Size viewport)
    {
        if (viewport == _viewport || viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        _viewport = viewport;
        OnPropertyChanged(nameof(PageWidth));
    }

    [RelayCommand] private void ZoomIn() => ZoomBy(PreviewZoom.Step);

    [RelayCommand] private void ZoomOut() => ZoomBy(1 / PreviewZoom.Step);

    // Back to fit-width. The floor is deliberately left where it is: having seen the whole page once, the user
    // can still zoom back down to it — mirrors the web's zoomReset.
    [RelayCommand] private void ZoomReset() => Zoom = PreviewZoom.Clamp(1, _zoomFloor);

    // Ctrl/⌘ + wheel, from the view. (Pinch is a touch gesture the desktop shell does not deliver to a mouse-and-
    // keyboard window; the web has it because a tablet browser does.)
    public void ZoomBy(double multiplier) => Zoom = PreviewZoom.Clamp(Zoom * multiplier, _zoomFloor);

    // Fit the whole page in view. Also lowers the floor to that scale, so zooming out now walks down to
    // whole-page and stops there instead of stopping at fit-width and doing nothing.
    //
    // "Fit entire document" deliberately means fit the CURRENT page, not all of them: a PDF renders as N stacked
    // pages, so fitting the lot would zoom a 40-page document to nothing. The first page stands for the page
    // shape, which is uniform in every format that reaches here.
    [RelayCommand]
    private void FitPage()
    {
        if (PreviewPages.FirstOrDefault()?.Image.Size is not { Width: > 0, Height: > 0 } size
            || PreviewZoom.FitPageScale(PageBaseWidth, _viewport.Height, size.Height / size.Width) is not { } scale)
        {
            return;
        }

        _zoomFloor = scale;
        Zoom = scale;
    }

    // Find-in-document (ADR "Search hit overlay"): the query whose matching words are highlighted on the
    // preview. Seeded from the search when a document is opened from a result; also editable in the preview's
    // own find box. Reapplied to the pages whenever it changes or a new document loads.
    [ObservableProperty] private string _findQuery = string.Empty;
    [ObservableProperty] private bool _canFindInDocument;

    // Occurrence count + current position for the find box (ADR "Find occurrence count + prev/next"). The flat
    // match list is in reading order across all pages; FindIndex is the 0-based current match.
    private readonly List<(PreviewPageViewModel Page, HighlightBox Box)> _findMatches = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindPosition))]
    [NotifyPropertyChangedFor(nameof(CanFindNavigate))]
    private int _findCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FindPosition))]
    private int _findIndex = -1;

    public string FindPosition => FindCount == 0 ? "0 / 0" : $"{FindIndex + 1} / {FindCount}";

    public bool CanFindNavigate => FindCount > 0;

    partial void OnFindQueryChanged(string value)
    {
        if (PreviewText is not null)
        {
            ApplyFindToText();
        }
        else
        {
            ApplyFindToPages();
        }
    }

    // ---- Find in a TEXT preview (#1063, the web pattern promoted per ADR 0511) -------------------------
    // Pages find via the word-box overlay; a text preview has no boxes, so the matches come from the shared
    // arithmetic (SimplArchive.Presentation.TextFind) — the same code the web pane renders from, which is
    // what keeps the two finds from disagreeing.

    private IReadOnlyList<int> _textMatches = [];

    /// <summary>Bumped whenever the text-find result or the active match changes — the view listens and
    /// rebuilds the highlighted runs (a render concern the view model cannot draw).</summary>
    [ObservableProperty] private int _textFindStamp;

    /// <summary>Character offset of the active match, -1 for none — what the view scrolls to.</summary>
    public int ActiveTextMatchOffset =>
        FindIndex >= 0 && FindIndex < _textMatches.Count ? _textMatches[FindIndex] : -1;

    /// <summary>The text split into render segments (plain / hit / active hit), from the shared arithmetic.</summary>
    public IEnumerable<(string Segment, bool IsHit, bool IsActive)> TextFindSegments() =>
        Presentation.TextFind.Segments(PreviewText, _textMatches, FindQuery.Length, FindIndex + 1);

    private void ApplyFindToText()
    {
        _textMatches = Presentation.TextFind.Matches(PreviewText, FindQuery);
        FindCount = _textMatches.Count;
        FindIndex = FindCount > 0 ? 0 : -1;
        TextFindStamp++;
    }

    [RelayCommand]
    private void FindNext()
    {
        if (FindCount == 0)
        {
            return;
        }

        FindIndex = (FindIndex + 1) % FindCount;
        ActivateCurrentFind();
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (FindCount == 0)
        {
            return;
        }

        FindIndex = (FindIndex - 1 + FindCount) % FindCount;
        ActivateCurrentFind();
    }

    private void ActivateCurrentFind()
    {
        if (PreviewText is not null)
        {
            TextFindStamp++;
        }
        else
        {
            ActivateCurrentMatch();
        }
    }

    // Marks the current match active on its page (and clears it on every other page), which the overlay draws
    // in orange and scrolls into view.
    private void ActivateCurrentMatch()
    {
        foreach (var page in PreviewPages)
        {
            page.ActiveHighlight = null;
        }

        if (FindIndex >= 0 && FindIndex < _findMatches.Count)
        {
            var (page, box) = _findMatches[FindIndex];
            page.ActiveHighlight = box;
        }
    }

    // Called by the overlay after a hit word is copied to the clipboard (ADR "Copy a preview word to the clipboard").
    [RelayCommand]
    private void HitWordCopied(HitCopyResult? result)
    {
        if (result is { } r)
        {
            _status.Report(r.Appended ? $"Appended '{r.Word}' to the clipboard." : $"Copied '{r.Word}' to the clipboard.");
        }
    }

    private void ApplyFindToPages()
    {
        var terms = FindQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _findMatches.Clear();
        foreach (var page in PreviewPages)
        {
            page.ApplyQuery(terms);
            page.ActiveHighlight = null;
            foreach (var box in page.Highlights)
            {
                _findMatches.Add((page, box));
            }
        }

        FindCount = _findMatches.Count;
        FindIndex = FindCount > 0 ? 0 : -1;
        ActivateCurrentMatch(); // jump to the first match (like a browser find)
    }

    // Renders any preview (a document version's or an intray item's — the Preview shape is shared) into the
    // preview pane, then attaches the hit-overlay.
    /// <summary>
    /// What this surface last rendered, so a returning card can bring it back (#1453).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT cleared by <see cref="Reset"/>: the discard on card removal resets the surface, and the
    /// whole point is to know afterwards what was on it. It is also the guard against restoring the wrong thing
    /// — if the reader selected another document in the meantime, this no longer refers to what was discarded
    /// and the restore is skipped rather than replacing their current document with the previous one.
    /// </remarks>
    internal Preview? LastRendered { get; private set; }

    public async Task RenderAsync(Preview preview)
    {
        LastRendered = preview;

        if (Api is null)
        {
            return;
        }

        PreviewConverted = preview.PreviewConverted;

        // Sticky notes are available only when the version resource offered an annotations link AND the view
        // supplied a dialog provider (so the Intray/Recycle-bin previews don't show note controls).
        _annotationsUrl = AnnotationDialog is not null ? preview.AnnotationsUrl : null;
        AddNoteMode = false;

        if (preview.PreviewUrl is null)
        {
            Reset("No preview available.");
            return;
        }

        // Multi-page TIFF: each page is its own image rendition (ADR "Multi-page TIFF preview pages") — load
        // them as separate pages. Null (204) for every other format, which falls through to the single flow.
        if (preview.PreviewPagesUrl is { } pagesUrl && await Api.Versions.GetPreviewPagesAsync(pagesUrl) is { Count: > 0 } pageUrls)
        {
            Reset(null);
            foreach (var url in pageUrls)
            {
                var (pageBytes, _, pageEnveloped) = await SimplArchiveApiClient.DownloadDetailedAsync(url);
                ContentWasEnveloped |= pageEnveloped;
                var pageImage = await Task.Run(() => PreviewRenderer.DecodeImage(pageBytes));
                PreviewPages.Add(new PreviewPageViewModel(pageImage));
            }

            HasPreviewPages = PreviewPages.Count > 0;
            await AttachOverlaysAsync(preview.TextLayoutUrl);
            return;
        }

        var (bytes, contentType, enveloped) = await SimplArchiveApiClient.DownloadDetailedAsync(preview.PreviewUrl);

        switch (SniffPreviewKind(bytes, contentType))
        {
            case PreviewMediaKind.Image:
                var image = await Task.Run(() => PreviewRenderer.DecodeImage(bytes));
                Reset(null);
                PreviewPages.Add(new PreviewPageViewModel(image));
                HasPreviewPages = true;
                await AttachOverlaysAsync(preview.TextLayoutUrl);
                break;

            case PreviewMediaKind.Pdf:
                var pages = await Task.Run(() => PreviewRenderer.RenderPdfPages(bytes));
                Reset(pages.Count == 0 ? "No preview available." : null);
                foreach (var page in pages)
                {
                    PreviewPages.Add(new PreviewPageViewModel(page));
                }

                HasPreviewPages = PreviewPages.Count > 0;
                // The bytes are handed on, so find-in-document can be computed HERE when the server will not
                // answer — on a strict tenant it refuses (409, ADR 0829), because word boxes reconstruct the
                // document through a route that presigns nothing. These are the same bytes the card just
                // decrypted, and the same extraction the server would have run (#1402).
                await AttachOverlaysAsync(preview.TextLayoutUrl, pdfBytes: bytes);
                break;

            case PreviewMediaKind.Text:
                Reset(null);
                PreviewText = Encoding.UTF8.GetString(bytes);
                CanFindInDocument = true; // the find bar works on text too (#1063)
                ApplyFindToText();        // a search-seeded or persisted query applies immediately
                break;

            default:
                Reset("Preview not supported — use Open to view in the native application.");
                break;
        }

        // AFTER the switch, because every branch calls Reset() — which clears this, correctly: a reset means
        // nothing is on display, so nothing enveloped is on display either.
        ContentWasEnveloped = enveloped;
    }

    private enum PreviewMediaKind { Image, Pdf, Text, Unsupported }

    // Determines how to render preview bytes, preferring magic bytes over the Content-Type header — stored
    // objects are frequently served as application/octet-stream (e.g. a PDF uploaded via a presigned PUT with
    // no content type), which the header check misclassifies as unsupported. Same reason the web preview
    // sniffs; see ADR "Web preview pdf.js hit-overlay".
    private static PreviewMediaKind SniffPreviewKind(byte[] b, string contentType)
    {
        if (b.Length >= 4 && b[0] == 0x25 && b[1] == 0x50 && b[2] == 0x44 && b[3] == 0x46) // "%PDF"
        {
            return PreviewMediaKind.Pdf;
        }

        if (b.Length >= 4 && ((b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) // PNG
                              || (b[0] == 0xFF && b[1] == 0xD8)                               // JPEG
                              || (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)))             // GIF
        {
            return PreviewMediaKind.Image;
        }

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewMediaKind.Image;
        }

        if (contentType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewMediaKind.Pdf;
        }

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
        {
            return PreviewMediaKind.Text;
        }

        return PreviewMediaKind.Unsupported;
    }

    // Attaches both overlays after the pages are built: assigns each page its index, loads the search hit-overlay,
    // then loads the sticky notes (ADR "Document annotations").
    private async Task AttachOverlaysAsync(string? textLayoutUrl, byte[]? pdfBytes = null)
    {
        for (var i = 0; i < PreviewPages.Count; i++)
        {
            PreviewPages[i].PageIndex = i;
        }

        await LoadHitOverlayAsync(textLayoutUrl, pdfBytes);

        AnnotationsAvailable = _annotationsUrl is not null && PreviewPages.Count > 0;
        if (AnnotationsAvailable)
        {
            await LoadAnnotationsAsync();
        }
    }

    // Fetches the per-page word boxes for the just-loaded preview and attaches them to the pages, then applies
    // the current find query (search-seeded or typed). No overlay if the format is unsupported / nothing was
    // recognized. See ADR "Search hit overlay".
    // Where the words come from is a POLICY with two sources and a precedence, so it lives in its own class
    // (PreviewWordSource) — which is also what keeps this view-model under the 1000-line limit, and what makes
    // that precedence testable without standing up a preview.
    private async Task LoadHitOverlayAsync(string? textLayoutUrl, byte[]? pdfBytes = null)
    {
        CanFindInDocument = false;
        if (PreviewPages.Count == 0)
        {
            return;
        }

        var pages = await PreviewWordSource.LoadAsync(Api, textLayoutUrl, pdfBytes);
        if (pages.Count == 0 || pages.All(p => p.Count == 0))
        {
            return; // nothing to find — leave the affordance off rather than offering an empty search
        }

        for (var i = 0; i < PreviewPages.Count && i < pages.Count; i++)
        {
            PreviewPages[i].SetWords(pages[i]);
        }

        CanFindInDocument = true;
        ApplyFindToPages();
    }

    /// <summary>
    /// True when the bytes on display arrived as a CMS envelope and were decrypted by the card (#1450).
    /// </summary>
    /// <remarks>
    /// Read by the card-removal path, which discards what the card decrypted and must leave everything else
    /// alone. It used to discard EVERY preview: pulling a card on a tenant with no encryption at all blanked a
    /// plaintext document, which is the opposite of the reassurance the feature is meant to give.
    /// </remarks>

    public void Reset(string? placeholder)
    {
        // Every document opens at fit-width, and the floor goes back to 1 with it: the floor belongs to the PAGE,
        // so a landscape page following a portrait one would otherwise be pinned above its own fit-page scale.
        Zoom = 1;
        _zoomFloor = 1;
        PreviewPages.Clear();
        HasPreviewPages = false;
        PreviewText = null;
        CanFindInDocument = false;
        _findMatches.Clear();
        _textMatches = [];
        FindCount = 0;
        FindIndex = -1;
        PreviewPlaceholder = placeholder;
        ContentWasEnveloped = false;
        AnnotationsAvailable = false;
        CanAddNote = false;
        AddNoteMode = false;
        AnnotationTool = 0;
        _annotations = [];
        _selectedAnnotationIds.Clear();
        _annotationClipboard.Clear();
        HasSelectedAnnotations = false;
        HasClipboardAnnotations = false;
    }

    // ---- Headless screenshot seams (used by Program's --screenshot modes) ------------------------------

    public void SetPreviewPagesForScreenshot(IEnumerable<Bitmap> pages)
    {
        Reset(null);
        foreach (var page in pages)
        {
            PreviewPages.Add(new PreviewPageViewModel(page));
        }

        HasPreviewPages = PreviewPages.Count > 0;
    }

    public void SetHitOverlayPageForScreenshot(PreviewPageViewModel page, string query)
    {
        Reset(null);
        PreviewPages.Add(page);
        HasPreviewPages = true;
        CanFindInDocument = true;
        FindQuery = query;
    }

    // Places annotation boxes on the first rendered page (ADR 0502) so the workbench screenshot's desktop preview
    // shows the seeded highlight + sticky note, matching the web capture.
    public void SetScreenshotNotesOnFirstPage(IReadOnlyList<NoteBox> notes)
    {
        if (PreviewPages.Count > 0)
        {
            PreviewPages[0].Notes = notes;
        }
    }
}
