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
    public Action<string>? StatusReporter { get; set; }

    // Preview: exactly one of pages/text is shown; otherwise the placeholder. PreviewPages holds one page per
    // image, or one per PDF page (all pages, stacked) — each carrying its bitmap plus the search hit-overlay.
    public ObservableCollection<PreviewPageViewModel> PreviewPages { get; } = [];
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasPreview))][NotifyPropertyChangedFor(nameof(HasWatermark))] private bool _hasPreviewPages;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasPreview))][NotifyPropertyChangedFor(nameof(HasWatermark))] private string? _previewText;
    [ObservableProperty] private string? _previewPlaceholder = "Select a document.";
    [ObservableProperty] private bool _previewConverted;

    // Sensitivity watermark (ADR "Document watermarking") — the "<LABEL> · <viewer>" text, empty when the document
    // isn't Confidential/Restricted. The overlay tiles it diagonally over the preview (client-side only).
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasWatermark))][NotifyPropertyChangedFor(nameof(WatermarkTiles))] private string _watermarkText = "";
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
    [ObservableProperty] private string _findQuery = "";
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

    partial void OnFindQueryChanged(string value) => ApplyFindToPages();

    [RelayCommand]
    private void FindNext()
    {
        if (FindCount == 0)
        {
            return;
        }

        FindIndex = (FindIndex + 1) % FindCount;
        ActivateCurrentMatch();
    }

    [RelayCommand]
    private void FindPrevious()
    {
        if (FindCount == 0)
        {
            return;
        }

        FindIndex = (FindIndex - 1 + FindCount) % FindCount;
        ActivateCurrentMatch();
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
            StatusReporter?.Invoke(r.Appended ? $"Appended '{r.Word}' to the clipboard." : $"Copied '{r.Word}' to the clipboard.");
        }
    }

    // --- Sticky notes / positional annotations (ADR "Document annotations") --------------------------------
    // Only a real document-version preview carries notes (the annotations link + a dialog provider must both be
    // present); the Intray and Recycle-bin previews don't. The dialog itself is supplied by the view (code-behind)
    // so the VM stays view-agnostic, mirroring StatusReporter.
    public Func<AnnotationDialogRequest, Task<AnnotationDialogResult?>>? AnnotationDialog { get; set; }

    public sealed record AnnotationDialogRequest(string Text, string Color, string? AuthorName, bool CanEdit, bool CanDelete, bool IsShape = false);
    public sealed record AnnotationDialogResult(string Action, string Text, string Color);

    private string? _annotationsUrl;
    private IReadOnlyList<AnnotationsClient.AnnotationInfo> _annotations = [];

    [ObservableProperty] private bool _annotationsAvailable;
    // Whether the caller has CanAnnotate (ADR "CanAnnotate right") — gates the Add-note button; viewing needs only read.
    [ObservableProperty] private bool _canAddNote;
    [ObservableProperty] private bool _notesVisible = true;
    [ObservableProperty] private bool _addNoteMode;

    [RelayCommand]
    private void AddNote()
    {
        if (!NotesVisible)
        {
            NotesVisible = true;
            PushNotesToPages();
        }

        AnnotationTool = 0; // notes and shape-drawing are mutually exclusive
        AddNoteMode = true;
        StatusReporter?.Invoke("Click a spot on the page to place a note.");
    }

    [RelayCommand]
    private void ToggleNotes()
    {
        NotesVisible = !NotesVisible;
        if (!NotesVisible)
        {
            AddNoteMode = false;
            AnnotationTool = 0;
        }

        PushNotesToPages();
    }

    [RelayCommand]
    private async Task NotePlaced(NotePlacement? placement)
    {
        AddNoteMode = false;
        if (placement is null || _annotationsUrl is null || Api is null || AnnotationDialog is null)
        {
            return;
        }

        var result = await AnnotationDialog(new AnnotationDialogRequest("", "#FFEB3B", null, CanEdit: true, CanDelete: false));
        if (result is null || result.Action != "save")
        {
            return;
        }

        try
        {
            // Create the note with a default size (kind 0 + width/height) so it renders as an always-visible box
            // (ADR "Post-it note boxes"); the overlay grows the height to fit the text and the user can resize it.
            await Api.Annotations.CreateAnnotationAsync(_annotationsUrl, placement.PageIndex, 0, placement.X, placement.Y, 0.22, 0.06, result.Text, result.Color);
            await LoadAnnotationsAsync();
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not add the note.");
        }
    }

    [RelayCommand]
    private async Task NoteClicked(Guid id)
    {
        var note = _annotations.FirstOrDefault(a => a.Id == id);
        if (note is null || _annotationsUrl is null || Api is null || AnnotationDialog is null)
        {
            return;
        }

        var result = await AnnotationDialog(new AnnotationDialogRequest(note.Text, note.Color, note.AuthorName, note.CanEdit, note.CanDelete, IsShape: note.Kind > 0));
        if (result is null)
        {
            return;
        }

        try
        {
            if (result.Action == "delete")
            {
                await Api.Annotations.DeleteAnnotationAsync(_annotationsUrl, note.Id, note.Etag);
            }
            else if (result.Action == "save")
            {
                await Api.Annotations.UpdateAnnotationAsync(_annotationsUrl, note.Id, note.PageIndex, note.PositionX, note.PositionY, note.Width, note.Height, result.Text, result.Color, note.Etag);
            }
            else
            {
                return;
            }

            await LoadAnnotationsAsync();
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not update the note.");
        }
    }

    // Drag-to-reposition (ADR "Document annotations"): the author dropped a note at a new spot; persist the new
    // position via the same update (keeping text/colour + the current etag), then reload — which snaps it back if
    // it failed (e.g. a 412 etag mismatch) and refreshes the etag.
    [RelayCommand]
    private async Task NoteMoved(NoteMove? move)
    {
        if (move is null || _annotationsUrl is null || Api is null)
        {
            return;
        }

        var note = _annotations.FirstOrDefault(a => a.Id == move.Id);
        if (note is null || !note.CanEdit)
        {
            return;
        }

        try
        {
            await Api.Annotations.UpdateAnnotationAsync(_annotationsUrl, note.Id, note.PageIndex, move.X, move.Y, note.Width, note.Height, note.Text, note.Color, note.Etag);
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not move the note.");
        }

        await LoadAnnotationsAsync();
    }

    // Resize (ADR "Post-it note boxes"): the author dragged the note box's corner grip; persist the new size
    // (keeping position/text/colour + the current etag), then reload — snapping back on a failed update.
    [RelayCommand]
    private async Task NoteResized(NoteResize? resize)
    {
        if (resize is null || _annotationsUrl is null || Api is null)
        {
            return;
        }

        var note = _annotations.FirstOrDefault(a => a.Id == resize.Id);
        if (note is null || !note.CanEdit)
        {
            return;
        }

        try
        {
            await Api.Annotations.UpdateAnnotationAsync(_annotationsUrl, note.Id, note.PageIndex, note.PositionX, note.PositionY, resize.Width, resize.Height, note.Text, note.Color, note.Etag);
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not resize the note.");
        }

        await LoadAnnotationsAsync();
    }

    // --- Markup shapes: highlight / rectangle / arrow (ADR "Annotation markup") ----------------------------
    // 0 none, 1 highlight, 2 rectangle, 3 arrow. Bound to the overlay's DrawKind; drag on the page to draw.
    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowAnnotationColorPalette))] private int _annotationTool;

    // The active colour for newly-drawn shapes + the toolbar palette (ADR "Highlighting redesign"). Picking a
    // swatch sets this AND recolours the current selection. Shown while a tool is active or something is selected.
    [ObservableProperty] private string _annotationColor = "#FFEB3B";
    public IReadOnlyList<AnnotationSwatch> AnnotationPalette { get; } =
        new[] { "#FFEB3B", "#8BC34A", "#4FC3F7", "#FF8A80", "#FFB74D", "#CE93D8" }.Select(h => new AnnotationSwatch(h)).ToList();
    public bool ShowAnnotationColorPalette => AnnotationTool > 0 || HasSelectedAnnotations;

    // Picks a colour: sets the draw colour + recolours every selected annotation the caller may edit, then reload.
    [RelayCommand]
    private async Task SetAnnotationColor(string? color)
    {
        if (string.IsNullOrEmpty(color))
        {
            return;
        }

        AnnotationColor = color;
        if (_annotationsUrl is null || Api is null || _selectedAnnotationIds.Count == 0)
        {
            return;
        }

        var targets = _annotations.Where(a => a.CanEdit && _selectedAnnotationIds.Contains(a.Id)).ToList();
        try
        {
            foreach (var a in targets)
            {
                await Api.Annotations.UpdateAnnotationAsync(_annotationsUrl, a.Id, a.PageIndex, a.PositionX, a.PositionY, a.Width, a.Height, a.Text, color, a.Etag);
            }
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not recolour the selection.");
        }

        await LoadAnnotationsAsync();
    }

    [RelayCommand]
    private void SelectShapeTool(object? kindParam)
    {
        // The XAML CommandParameter is a string ("1"/"2"/"3"); accept an int too.
        var kind = kindParam is int i ? i : int.TryParse(kindParam?.ToString(), out var p) ? p : 0;
        if (kind is < 1 or > 3)
        {
            return;
        }

        if (!NotesVisible)
        {
            NotesVisible = true;
            PushNotesToPages();
        }

        AddNoteMode = false;
        AnnotationTool = AnnotationTool == kind ? 0 : kind; // clicking the active tool turns it off
        if (AnnotationTool > 0)
        {
            StatusReporter?.Invoke("Drag on the page to draw.");
        }
    }

    // A shape was drawn by dragging — create it immediately with the tool's default colour (editable by clicking).
    [RelayCommand]
    private async Task ShapeDrawn(ShapeDraw? draw)
    {
        if (draw is null || draw.Kind <= 0 || _annotationsUrl is null || Api is null)
        {
            return;
        }

        AnnotationTool = 0; // one shape per tool activation (ADR "Draw-tool behaviour")

        try
        {
            await Api.Annotations.CreateAnnotationAsync(_annotationsUrl, draw.PageIndex, draw.Kind, draw.X, draw.Y, draw.W, draw.H, "", AnnotationColor);
            await LoadAnnotationsAsync();
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not add the markup.");
        }
    }

    private async Task LoadAnnotationsAsync()
    {
        _annotations = [];
        CanAddNote = false;
        if (Api is not null && _annotationsUrl is not null)
        {
            try
            {
                var list = await Api.Annotations.GetAnnotationsAsync(_annotationsUrl);
                _annotations = list.Items;
                CanAddNote = list.CanCreate; // CanAnnotate (ADR "CanAnnotate right")
            }
            catch (Exception) { /* no notes / not readable */ }
        }

        UpdateSelectionState();
    }

    private void PushNotesToPages()
    {
        foreach (var page in PreviewPages)
        {
            page.Notes = NotesVisible
                ? _annotations.Where(a => a.PageIndex == page.PageIndex).Select(a => new NoteBox(a.Id, a.Kind, a.PositionX, a.PositionY, a.Width ?? 0, a.Height ?? 0, a.Color, a.CanEdit, a.Text, _selectedAnnotationIds.Contains(a.Id), a.Points)).ToList()
                : [];
        }
    }

    // --- Multi-select: delete / group-move / copy-paste (ADR "Annotation multi-select") --------------------
    // The selected annotation ids (notes + shapes) on the current version, and an in-app clipboard of copied
    // annotations (pasted as offset duplicates on the same page). Selection is client-only; a selected id that
    // no longer exists after a reload is dropped by PushNotesToPages/UpdateSelectionState.
    private readonly HashSet<Guid> _selectedAnnotationIds = [];
    private readonly List<AnnotationsClient.AnnotationInfo> _annotationClipboard = [];

    [ObservableProperty][NotifyPropertyChangedFor(nameof(ShowAnnotationColorPalette))] private bool _hasSelectedAnnotations;
    [ObservableProperty] private bool _hasClipboardAnnotations;

    internal IReadOnlyCollection<Guid> SelectedAnnotationIdsForTest => _selectedAnnotationIds;

    private void UpdateSelectionState()
    {
        // Drop ids that no longer resolve to a loaded annotation (deleted elsewhere).
        _selectedAnnotationIds.RemoveWhere(id => _annotations.All(a => a.Id != id));
        HasSelectedAnnotations = _selectedAnnotationIds.Count > 0;
        PushNotesToPages();
    }

    // A click / Ctrl-click on an annotation: a plain click selects just it; a Ctrl-click toggles it in the set.
    [RelayCommand]
    private void SelectAnnotation(AnnotationSelect? select)
    {
        if (select is null)
        {
            return;
        }

        if (select.Toggle)
        {
            if (!_selectedAnnotationIds.Remove(select.Id))
            {
                _selectedAnnotationIds.Add(select.Id);
            }
        }
        else
        {
            _selectedAnnotationIds.Clear();
            _selectedAnnotationIds.Add(select.Id);
        }

        UpdateSelectionState();
    }

    // A marquee drag over empty page area selected the enclosed annotations (Ctrl adds to the current selection).
    [RelayCommand]
    private void MarqueeSelectAnnotations(MarqueeSelect? marquee)
    {
        if (marquee is null)
        {
            return;
        }

        if (!marquee.Additive)
        {
            _selectedAnnotationIds.Clear();
        }

        foreach (var id in marquee.Ids)
        {
            _selectedAnnotationIds.Add(id);
        }

        UpdateSelectionState();
    }

    [RelayCommand]
    private void ClearAnnotationSelection()
    {
        if (_selectedAnnotationIds.Count == 0)
        {
            return;
        }

        _selectedAnnotationIds.Clear();
        UpdateSelectionState();
    }

    // A group drag moved the whole selection by a normalized delta — shift every selected annotation on the page
    // (only the ones the caller may edit), persist each, then reload.
    [RelayCommand]
    private async Task GroupMoveAnnotations(AnnotationGroupMove? move)
    {
        if (move is null || _annotationsUrl is null || Api is null)
        {
            return;
        }

        var targets = _annotations
            .Where(a => a.PageIndex == move.PageIndex && a.CanEdit && _selectedAnnotationIds.Contains(a.Id))
            .ToList();
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var a in targets)
            {
                var nx = Math.Clamp(a.PositionX + move.Dx, 0, 1);
                var ny = Math.Clamp(a.PositionY + move.Dy, 0, 1);
                await Api.Annotations.UpdateAnnotationAsync(_annotationsUrl, a.Id, a.PageIndex, nx, ny, a.Width, a.Height, a.Text, a.Color, a.Etag);
            }
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not move the selection.");
        }

        await LoadAnnotationsAsync();
    }

    // Delete every selected annotation the caller may delete; reload + clear the selection.
    [RelayCommand]
    private async Task DeleteSelectedAnnotations()
    {
        if (_annotationsUrl is null || Api is null || _selectedAnnotationIds.Count == 0)
        {
            return;
        }

        var targets = _annotations.Where(a => a.CanDelete && _selectedAnnotationIds.Contains(a.Id)).ToList();
        try
        {
            foreach (var a in targets)
            {
                await Api.Annotations.DeleteAnnotationAsync(_annotationsUrl, a.Id, a.Etag);
            }
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not delete the selection.");
        }

        _selectedAnnotationIds.Clear();
        await LoadAnnotationsAsync();
    }

    // Copy the selected annotations into the in-app clipboard (a snapshot; paste re-creates them offset).
    [RelayCommand]
    private void CopySelectedAnnotations()
    {
        _annotationClipboard.Clear();
        _annotationClipboard.AddRange(_annotations.Where(a => _selectedAnnotationIds.Contains(a.Id)));
        HasClipboardAnnotations = _annotationClipboard.Count > 0;
        if (HasClipboardAnnotations)
        {
            StatusReporter?.Invoke($"Copied {_annotationClipboard.Count} annotation(s).");
        }
    }

    // Paste the clipboard as offset duplicates on their original page/version; reload + select the new copies is
    // not attempted (fresh ids are server-assigned) — the pastes just appear, nudged by a small offset.
    [RelayCommand]
    private async Task PasteAnnotations()
    {
        if (_annotationsUrl is null || Api is null || _annotationClipboard.Count == 0 || !CanAddNote)
        {
            return;
        }

        const double offset = 0.03;
        try
        {
            foreach (var a in _annotationClipboard)
            {
                var nx = Math.Clamp(a.PositionX + offset, 0, 1);
                var ny = Math.Clamp(a.PositionY + offset, 0, 1);
                // Freehand (kind 7) has no box extent — carry its poly-line points instead of a width/height.
                var w = a.Kind == 7 ? (double?)null : a.Width ?? (a.Kind == 0 ? 0.22 : 0.1);
                var h = a.Kind == 7 ? (double?)null : a.Height ?? (a.Kind == 0 ? 0.06 : 0.05);
                await Api.Annotations.CreateAnnotationAsync(_annotationsUrl, a.PageIndex, a.Kind, nx, ny, w, h, a.Text, a.Color, a.Points);
            }
        }
        catch (Exception e)
        {
            StatusReporter?.Invoke(e is ApiActionException ae ? ae.Message : "Could not paste the annotations.");
        }

        await LoadAnnotationsAsync();
    }

    // Test seam: point the view model at a real annotations URL and load, so a VM-level test can drive the
    // multi-select commands without the full detail-load path.
    internal async Task LoadAnnotationsForTestAsync(string annotationsUrl)
    {
        _annotationsUrl = annotationsUrl;
        await LoadAnnotationsAsync();
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
    public async Task RenderAsync(Preview preview)
    {
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
                var (pageBytes, _) = await SimplArchiveApiClient.DownloadAsync(url);
                var pageImage = await Task.Run(() => PreviewRenderer.DecodeImage(pageBytes));
                PreviewPages.Add(new PreviewPageViewModel(pageImage));
            }

            HasPreviewPages = PreviewPages.Count > 0;
            await AttachOverlaysAsync(preview.TextLayoutUrl);
            return;
        }

        var (bytes, contentType) = await SimplArchiveApiClient.DownloadAsync(preview.PreviewUrl);

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
                await AttachOverlaysAsync(preview.TextLayoutUrl);
                break;

            case PreviewMediaKind.Text:
                Reset(null);
                PreviewText = Encoding.UTF8.GetString(bytes);
                break;

            default:
                Reset("Preview not supported — use Open to view in the native application.");
                break;
        }
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
    private async Task AttachOverlaysAsync(string? textLayoutUrl)
    {
        for (var i = 0; i < PreviewPages.Count; i++)
        {
            PreviewPages[i].PageIndex = i;
        }

        await LoadHitOverlayAsync(textLayoutUrl);

        AnnotationsAvailable = _annotationsUrl is not null && PreviewPages.Count > 0;
        if (AnnotationsAvailable)
        {
            await LoadAnnotationsAsync();
        }
    }

    // Fetches the per-page word boxes for the just-loaded preview and attaches them to the pages, then applies
    // the current find query (search-seeded or typed). No overlay if the format is unsupported / nothing was
    // recognized. See ADR "Search hit overlay".
    private async Task LoadHitOverlayAsync(string? textLayoutUrl)
    {
        CanFindInDocument = false;
        if (Api is null || textLayoutUrl is null || PreviewPages.Count == 0)
        {
            return;
        }

        try
        {
            var layout = await Api.Versions.GetTextLayoutAsync(textLayoutUrl);
            if (layout is null || layout.Pages.Count == 0)
            {
                return;
            }

            for (var i = 0; i < PreviewPages.Count && i < layout.Pages.Count; i++)
            {
                PreviewPages[i].SetWords(layout.Pages[i].Words);
            }

            CanFindInDocument = true;
            ApplyFindToPages();
        }
        catch (Exception)
        {
            // Best-effort — no overlay on failure.
        }
    }

    public void Reset(string? placeholder)
    {
        // Every document opens at fit-width, and the floor goes back to 1 with it: the floor belongs to the PAGE,
        // so a landscape page following a portrait one would otherwise be pinned above its own fit-page scale.
        Zoom = 1;
        _zoomFloor = 1;
        PreviewPages.Clear();
        HasPreviewPages = false;
        PreviewText = null;
        PreviewPlaceholder = placeholder;
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
