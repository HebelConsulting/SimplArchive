using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// Draws the search-hit boxes over a preview page (ADR "Search hit overlay") — Boxes, the find-query matches —
// and makes *every* word on the page clickable to copy it (ADR "Copy a preview word to the clipboard"): Words is
// the full set, hit-tested on click regardless of highlighting. Independent of any search, the word directly
// under the cursor gets a light-grey hover box, so the clickable words are discoverable. A left click replaces
// the clipboard with the word, a shift+left click appends " word" to the current clipboard content. Placed in
// the same grid cell as the page Image so its bounds match the rendered page; every box is normalized 0..1.
public sealed class HighlightOverlay : Control
{



    private HighlightBox? _hovered;

    // The highlighted hit boxes that get drawn.
    public static readonly StyledProperty<IEnumerable<HighlightBox>?> BoxesProperty =
        AvaloniaProperty.Register<HighlightOverlay, IEnumerable<HighlightBox>?>(nameof(Boxes));

    // Every word on the page — clickable to copy, whether or not it's highlighted. Not drawn.
    public static readonly StyledProperty<IEnumerable<HighlightBox>?> WordsProperty =
        AvaloniaProperty.Register<HighlightOverlay, IEnumerable<HighlightBox>?>(nameof(Words));

    // The current find match on this page (find prev/next) — drawn distinctly and scrolled into view. Null when
    // the active match is on another page.
    public static readonly StyledProperty<HighlightBox?> ActiveBoxProperty =
        AvaloniaProperty.Register<HighlightOverlay, HighlightBox?>(nameof(ActiveBox));

    // Invoked with a HitCopyResult after a word is copied, so the view model can show a status message.
    public static readonly StyledProperty<ICommand?> WordCopiedCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(WordCopiedCommand));


    public static readonly StyledProperty<IEnumerable<NoteBox>?> NotesProperty =
        AvaloniaProperty.Register<HighlightOverlay, IEnumerable<NoteBox>?>(nameof(Notes));

    public static readonly StyledProperty<int> PageIndexProperty =
        AvaloniaProperty.Register<HighlightOverlay, int>(nameof(PageIndex));

    // When true, a click on empty space places a note (fires NotePlacedCommand) instead of copying a word.
    public static readonly StyledProperty<bool> AddModeProperty =
        AvaloniaProperty.Register<HighlightOverlay, bool>(nameof(AddMode));

    // Fired with a NotePlacement when a note is dropped in add mode.
    public static readonly StyledProperty<ICommand?> NotePlacedCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(NotePlacedCommand));

    // Fired with the clicked note's Guid.
    public static readonly StyledProperty<ICommand?> NoteClickedCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(NoteClickedCommand));

    public IEnumerable<NoteBox>? Notes
    {
        get => GetValue(NotesProperty);
        set => SetValue(NotesProperty, value);
    }

    public int PageIndex
    {
        get => GetValue(PageIndexProperty);
        set => SetValue(PageIndexProperty, value);
    }

    public bool AddMode
    {
        get => GetValue(AddModeProperty);
        set => SetValue(AddModeProperty, value);
    }

    public ICommand? NotePlacedCommand
    {
        get => GetValue(NotePlacedCommandProperty);
        set => SetValue(NotePlacedCommandProperty, value);
    }

    public ICommand? NoteClickedCommand
    {
        get => GetValue(NoteClickedCommandProperty);
        set => SetValue(NoteClickedCommandProperty, value);
    }

    // Fired with a NoteMove when the author drags an existing marker to a new spot.
    public static readonly StyledProperty<ICommand?> NoteMovedCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(NoteMovedCommand));

    public ICommand? NoteMovedCommand
    {
        get => GetValue(NoteMovedCommandProperty);
        set => SetValue(NoteMovedCommandProperty, value);
    }

    // Fired with a NoteResize when the author drags a note box's corner grip (ADR "Post-it note boxes").
    public static readonly StyledProperty<ICommand?> NoteResizedCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(NoteResizedCommand));

    public ICommand? NoteResizedCommand
    {
        get => GetValue(NoteResizedCommandProperty);
        set => SetValue(NoteResizedCommandProperty, value);
    }


    public static readonly StyledProperty<ICommand?> SelectAnnotationCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(SelectAnnotationCommand));

    public ICommand? SelectAnnotationCommand
    {
        get => GetValue(SelectAnnotationCommandProperty);
        set => SetValue(SelectAnnotationCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> MarqueeSelectCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(MarqueeSelectCommand));

    public ICommand? MarqueeSelectCommand
    {
        get => GetValue(MarqueeSelectCommandProperty);
        set => SetValue(MarqueeSelectCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> ClearSelectionCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(ClearSelectionCommand));

    public ICommand? ClearSelectionCommand
    {
        get => GetValue(ClearSelectionCommandProperty);
        set => SetValue(ClearSelectionCommandProperty, value);
    }

    public static readonly StyledProperty<ICommand?> GroupMovedCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(GroupMovedCommand));

    public ICommand? GroupMovedCommand
    {
        get => GetValue(GroupMovedCommandProperty);
        set => SetValue(GroupMovedCommandProperty, value);
    }

    // Marquee-select state (rubber-band over empty page area) + group-drag state (moving the whole selection).
    private bool _marqueeing;
    private bool _marqueePending;
    private Point _marqueeStart;
    private Point _marqueeCurrent;
    private bool _groupDragging;
    private NoteBox? _groupPending;
    private Point _groupStart;
    private Point _groupCurrent;

    private static bool CtrlOrCmd(KeyModifiers m) => m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta);
    private int SelectedCount() => Notes?.Count(n => n.Selected) ?? 0;

    private void ToggleSelect(Guid id)
    {
        var sel = new AnnotationSelect(id, Toggle: true);
        if (SelectAnnotationCommand?.CanExecute(sel) == true)
        {
            SelectAnnotationCommand.Execute(sel);
        }
    }

    private void SelectOne(Guid id)
    {
        var sel = new AnnotationSelect(id, Toggle: false);
        if (SelectAnnotationCommand?.CanExecute(sel) == true)
        {
            SelectAnnotationCommand.Execute(sel);
        }
    }

    // Markup drawing (ADR "Annotation markup"): the active tool (0 none, 1 highlight, 2 rectangle, 3 arrow). When
    // > 0, a press-drag on the page draws a shape and fires ShapeDrawnCommand on release.
    public static readonly StyledProperty<int> DrawKindProperty =
        AvaloniaProperty.Register<HighlightOverlay, int>(nameof(DrawKind));

    public int DrawKind
    {
        get => GetValue(DrawKindProperty);
        set => SetValue(DrawKindProperty, value);
    }

    // The active draw colour (ADR "Draw-tool behaviour" / "Highlighting redesign") — the in-progress shape
    // preview uses this instead of a hardcoded per-kind colour, so the drag matches the picked palette colour.
    public static readonly StyledProperty<string?> DrawColorProperty =
        AvaloniaProperty.Register<HighlightOverlay, string?>(nameof(DrawColor));

    public string? DrawColor
    {
        get => GetValue(DrawColorProperty);
        set => SetValue(DrawColorProperty, value);
    }

    public static readonly StyledProperty<ICommand?> ShapeDrawnCommandProperty =
        AvaloniaProperty.Register<HighlightOverlay, ICommand?>(nameof(ShapeDrawnCommand));

    public ICommand? ShapeDrawnCommand
    {
        get => GetValue(ShapeDrawnCommandProperty);
        set => SetValue(ShapeDrawnCommandProperty, value);
    }

    // In-progress shape drawing (normalized start/current), while DrawKind > 0.
    private bool _drawing;
    private Point _drawStart;
    private Point _drawCurrent;

    // Drag-to-reposition state (ADR "Document annotations"). A press on an editable marker starts a potential
    // drag; moving past the threshold makes it a drag (persisted on release), otherwise the release is a click.
    private const double DragThreshold = 4; // px
    private NoteBox? _pressedNote;
    private Point _pressPoint;
    private Point _dragPoint;
    private bool _noteDragging;
    private Point _grabOffset; // press point minus the note box's top-left, so the grabbed spot stays under the cursor

    // Resize state: a press on a note box's corner grip drags its size (ADR "Post-it note boxes").
    private NoteBox? _resizingNote;
    private Point _resizePoint;

    // Shape move/resize state (ADR "Highlighting redesign"): a shape is movable (drag its body) + a box shape is
    // resizable (corner grip). Mirrors the note drag/resize, but a shape moves its start point + keeps its extent.
    private NoteBox? _pressedShape;
    private Point _shapePressPoint;
    private Point _shapeDragPoint;
    private bool _shapeDragging;
    private NoteBox? _resizingShape;
    private Point _shapeResizePoint;



    static HighlightOverlay()
    {
        AffectsRender<HighlightOverlay>(BoxesProperty, ActiveBoxProperty, BoundsProperty, NotesProperty);
    }





    // The annotation ids whose box (note) or bounding box (shape) intersect a marquee rect (ADR "Annotation
    // multi-select"). Pixel-space geometry over the current Notes.
    private IReadOnlyList<Guid> AnnotationsInRect(Rect marquee)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (Notes is not { } notes || width <= 0 || height <= 0)
        {
            return [];
        }

        var ids = new List<Guid>();
        foreach (var n in notes)
        {
            var r = n.Kind == 0
                ? HighlightOverlayDrawing.MeasureNote(n, width, height).Rect
                : new Rect(Math.Min(n.X, n.X + n.Width) * width, Math.Min(n.Y, n.Y + n.Height) * height,
                    Math.Abs(n.Width) * width, Math.Abs(n.Height) * height);
            if (r.Intersects(marquee))
            {
                ids.Add(n.Id);
            }
        }

        return ids;
    }

    public IEnumerable<HighlightBox>? Boxes
    {
        get => GetValue(BoxesProperty);
        set => SetValue(BoxesProperty, value);
    }

    public HighlightBox? ActiveBox
    {
        get => GetValue(ActiveBoxProperty);
        set => SetValue(ActiveBoxProperty, value);
    }

    public IEnumerable<HighlightBox>? Words
    {
        get => GetValue(WordsProperty);
        set => SetValue(WordsProperty, value);
    }

    public ICommand? WordCopiedCommand
    {
        get => GetValue(WordCopiedCommandProperty);
        set => SetValue(WordCopiedCommandProperty, value);
    }

    // Forces the hover box for the headless screenshot (hovering is otherwise interactive).
    internal void SetHoveredForScreenshot(HighlightBox box)
    {
        _hovered = box;
        InvalidateVisual();
    }


    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // A transparent fill over the whole page makes the overlay hit-testable everywhere (so pointer-move
        // and click fire over any word), without painting anything visible.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        Rect ToRect(HighlightBox b) => new(b.X * width, b.Y * height, b.Width * width, b.Height * height);

        // The word under the cursor (light grey), drawn first so a yellow search hit stays on top when hovered.
        if (_hovered is { } hovered)
        {
            context.DrawRectangle(HighlightOverlayDrawing.HoverFill, HighlightOverlayDrawing.HoverStroke, ToRect(hovered));
        }

        if (Boxes is { } boxes)
        {
            foreach (var box in boxes)
            {
                context.DrawRectangle(HighlightOverlayDrawing.Fill, HighlightOverlayDrawing.Stroke, ToRect(box));
            }
        }

        // The current match (find prev/next) on top, in orange.
        if (ActiveBox is { } active)
        {
            context.DrawRectangle(HighlightOverlayDrawing.ActiveFill, HighlightOverlayDrawing.ActiveStroke, ToRect(active));
        }

        // While group-dragging the selection, offset every selected annotation by the drag delta (ADR
        // "Annotation multi-select").
        var gdx = _groupDragging ? (_groupCurrent.X - _groupStart.X) / width : 0;
        var gdy = _groupDragging ? (_groupCurrent.Y - _groupStart.Y) / height : 0;

        // Markup shapes + sticky-note markers (ADRs "Annotation markup" / "Document annotations"), on top of all.
        if (Notes is { } notes)
        {
            foreach (var note in notes)
            {
                var groupOffset = _groupDragging && note.Selected;
                if (note.Kind > 0)
                {
                    // Live-preview a single-shape drag / box-shape resize (ADR "Highlighting redesign").
                    var sx = groupOffset ? note.X + gdx : note.X;
                    var sy = groupOffset ? note.Y + gdy : note.Y;
                    var sw = note.Width;
                    var sh = note.Height;
                    if (_shapeDragging && _pressedShape?.Id == note.Id)
                    {
                        sx = note.X + (_shapeDragPoint.X - _shapePressPoint.X) / width;
                        sy = note.Y + (_shapeDragPoint.Y - _shapePressPoint.Y) / height;
                    }
                    else if (_resizingShape?.Id == note.Id)
                    {
                        sw = Math.Max(0.01, _shapeResizePoint.X / width - note.X);
                        sh = Math.Max(0.01, _shapeResizePoint.Y / height - note.Y);
                    }

                    HighlightOverlayDrawing.DrawShape(context, note.Kind, sx, sy, sw, sh, width, height, HighlightOverlayDrawing.ParseColor(note.Color), preview: false, note.Text, note.Points);
                    if (note.Selected)
                    {
                        // Freehand (kind 7) has no box extent — outline the poly-line's own bounds instead.
                        var bb = note.Kind == 7
                            ? HighlightOverlayDrawing.FreehandBounds(note.Points, width, height).Inflate(3)
                            : new Rect(Math.Min(sx, sx + sw) * width - 3, Math.Min(sy, sy + sh) * height - 3,
                                Math.Abs(sw) * width + 6, Math.Abs(sh) * height + 6);
                        context.DrawRectangle(null, HighlightOverlayDrawing.SelectionPen, bb);
                    }

                    continue;
                }

                // A sticky-note box showing its text, always visible (ADR "Post-it note boxes"). While moving,
                // the grabbed spot follows the cursor; while resizing, the corner follows the cursor.
                var eff = note;
                if (_noteDragging && _pressedNote?.Id == note.Id)
                {
                    eff = note with { X = (_dragPoint.X - _grabOffset.X) / width, Y = (_dragPoint.Y - _grabOffset.Y) / height };
                }
                else if (_resizingNote?.Id == note.Id)
                {
                    eff = note with
                    {
                        Width = Math.Max(HighlightOverlayDrawing.NoteMinWidthPx, _resizePoint.X - note.X * width) / width,
                        Height = Math.Max(0, _resizePoint.Y - note.Y * height) / height,
                    };
                }
                else if (groupOffset)
                {
                    eff = note with { X = note.X + gdx, Y = note.Y + gdy };
                }

                var (nrect, nft) = HighlightOverlayDrawing.MeasureNote(eff, width, height);
                context.DrawRectangle(new SolidColorBrush(HighlightOverlayDrawing.ParseColor(eff.Color)), HighlightOverlayDrawing.NoteBorder, nrect, 4, 4);
                context.DrawText(nft, new Point(nrect.X + HighlightOverlayDrawing.NotePad, nrect.Y + HighlightOverlayDrawing.NotePad));
                if (note.Selected)
                {
                    context.DrawRectangle(null, HighlightOverlayDrawing.SelectionPen, nrect.Inflate(2), 4, 4);
                }

                if (eff.CanEdit)
                {
                    var gx = nrect.Right - 3;
                    var gy = nrect.Bottom - 3;
                    context.DrawLine(new Pen(HighlightOverlayDrawing.NoteGripBrush, 1.5), new Point(gx - 8, gy), new Point(gx, gy - 8));
                    context.DrawLine(new Pen(HighlightOverlayDrawing.NoteGripBrush, 1.5), new Point(gx - 4, gy), new Point(gx, gy - 4));
                }
            }
        }

        // The marquee rubber-band (ADR "Annotation multi-select").
        if (_marqueeing)
        {
            context.DrawRectangle(HighlightOverlayDrawing.MarqueeFill, HighlightOverlayDrawing.MarqueePen, new Rect(_marqueeStart, _marqueeCurrent));
        }

        // The shape being drawn (a live preview from the drag start to the current point) — in the active draw
        // colour (ADR "Draw-tool behaviour"), so the drag matches the picked palette colour, not a hardcoded one.
        if (_drawing && DrawKind > 0)
        {
            var color = string.IsNullOrEmpty(DrawColor) ? Colors.Yellow : HighlightOverlayDrawing.ParseColor(DrawColor);
            HighlightOverlayDrawing.DrawShape(context, DrawKind, _drawStart.X, _drawStart.Y, _drawCurrent.X - _drawStart.X, _drawCurrent.Y - _drawStart.Y, width, height, color, preview: true);
        }
    }








    // Scroll the current match into view when it lands on this page, by nudging the enclosing ScrollViewer so
    // the box sits about a third down the viewport.
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ActiveBoxProperty || ActiveBox is not { } b || Bounds is not { Width: > 0, Height: > 0 })
        {
            return;
        }

        var scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (scrollViewer is null)
        {
            return;
        }

        var boxInViewport = this.TranslatePoint(new Point(b.X * Bounds.Width, b.Y * Bounds.Height), scrollViewer);
        if (boxInViewport is not { } point)
        {
            return;
        }

        var target = scrollViewer.Offset.Y + point.Y - scrollViewer.Viewport.Height / 3;
        var max = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Clamp(target, 0, max));
    }

    // Track the word under the cursor for the light-grey hover box + a hand cursor.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // Drawing a markup shape — extend the rubber-band to the current point.
        if (_drawing && Bounds is { Width: > 0, Height: > 0 })
        {
            var pt = e.GetPosition(this);
            _drawCurrent = new Point(Math.Clamp(pt.X / Bounds.Width, 0, 1), Math.Clamp(pt.Y / Bounds.Height, 0, 1));
            Cursor = new Cursor(StandardCursorType.Cross);
            InvalidateVisual();
            return;
        }

        // Resizing a note box via its corner grip (ADR "Post-it note boxes").
        if (_resizingNote is not null)
        {
            _resizePoint = e.GetPosition(this);
            Cursor = new Cursor(StandardCursorType.BottomRightCorner);
            InvalidateVisual();
            return;
        }

        // Resizing a box shape via its corner grip (ADR "Highlighting redesign").
        if (_resizingShape is not null)
        {
            _shapeResizePoint = e.GetPosition(this);
            Cursor = new Cursor(StandardCursorType.BottomRightCorner);
            InvalidateVisual();
            return;
        }

        // A press is in progress on a shape — track it into a drag once past the threshold (ADR "Highlighting redesign").
        if (_pressedShape is { } pressedShape)
        {
            var spt = e.GetPosition(this);
            if (!_shapeDragging && pressedShape.CanEdit &&
                (Math.Abs(spt.X - _shapePressPoint.X) > DragThreshold || Math.Abs(spt.Y - _shapePressPoint.Y) > DragThreshold))
            {
                _shapeDragging = true;
            }

            if (_shapeDragging)
            {
                _shapeDragPoint = spt;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                InvalidateVisual();
            }

            return;
        }

        // A group drag of the selection is in progress (ADR "Annotation multi-select").
        if (_groupPending is not null && Bounds is { Width: > 0, Height: > 0 })
        {
            var pt = e.GetPosition(this);
            if (!_groupDragging && (Math.Abs(pt.X - _groupStart.X) > DragThreshold || Math.Abs(pt.Y - _groupStart.Y) > DragThreshold))
            {
                _groupDragging = true;
            }

            if (_groupDragging)
            {
                _groupCurrent = pt;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                InvalidateVisual();
            }

            return;
        }

        // A marquee rubber-band over empty page area (ADR "Annotation multi-select").
        if (_marqueePending)
        {
            var pt = e.GetPosition(this);
            if (!_marqueeing && (Math.Abs(pt.X - _marqueeStart.X) > DragThreshold || Math.Abs(pt.Y - _marqueeStart.Y) > DragThreshold))
            {
                _marqueeing = true;
            }

            if (_marqueeing)
            {
                _marqueeCurrent = pt;
                InvalidateVisual();
            }

            return;
        }

        // A press is in progress on an editable note box — track it into a drag once past the threshold.
        if (_pressedNote is { } pressed)
        {
            var pt = e.GetPosition(this);
            if (!_noteDragging && pressed.CanEdit &&
                (Math.Abs(pt.X - _pressPoint.X) > DragThreshold || Math.Abs(pt.Y - _pressPoint.Y) > DragThreshold))
            {
                _noteDragging = true;
            }

            if (_noteDragging)
            {
                _dragPoint = pt;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                InvalidateVisual();
            }

            return;
        }

        if (AddMode)
        {
            Cursor = new Cursor(StandardCursorType.Cross);
            return;
        }

        var point = e.GetPosition(this);
        var box = HighlightOverlayDrawing.HitTest(Words, point, Bounds.Width, Bounds.Height);
        var overNote = HighlightOverlayDrawing.HitTestNote(Notes, point, Bounds.Width, Bounds.Height);
        var overShape = overNote is null ? HighlightOverlayDrawing.HitTestShape(Notes, point, Bounds.Width, Bounds.Height) : null;
        Cursor =
            overNote is not null && HighlightOverlayDrawing.InNoteGrip(overNote, point, Bounds.Width, Bounds.Height) ? new Cursor(StandardCursorType.BottomRightCorner)
            : overShape is not null && HighlightOverlayDrawing.InShapeGrip(overShape, point, Bounds.Width, Bounds.Height) ? new Cursor(StandardCursorType.BottomRightCorner)
            : overShape is not null ? new Cursor(StandardCursorType.SizeAll)         // a shape is movable (ADR "Highlighting redesign")
            : (box is not null || overNote is not null) ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
        if (!Equals(box, _hovered))
        {
            _hovered = box;
            InvalidateVisual();
        }
    }

    // Clear the hover box when the pointer leaves the page.
    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hovered is not null)
        {
            _hovered = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // Capture the focused text field BEFORE anything (incl. base) can move focus — the overlay is
        // non-focusable, so a hit word can be typed into whatever field the user had focused (ADR "Intray
        // refinements"). The find box opts out via Tag="find".
        var focusedTextBox = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as TextBox;

        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);

        // Markup drawing (ADR "Annotation markup") takes priority when a tool is active: press-drag draws a shape.
        if (DrawKind > 0 && Bounds.Width > 0 && Bounds.Height > 0)
        {
            e.Handled = true;
            _drawing = true;
            _drawStart = _drawCurrent = new Point(point.X / Bounds.Width, point.Y / Bounds.Height);
            e.Pointer.Capture(this);
            return;
        }

        // Sticky notes (ADR "Document annotations") take priority over word-copy. In add mode, a click drops a
        // note here; otherwise a click on an existing marker opens it.
        if (AddMode)
        {
            e.Handled = true;
            var nx = point.X / Bounds.Width;
            var ny = point.Y / Bounds.Height;
            var placement = new NotePlacement(PageIndex, nx, ny);
            if (NotePlacedCommand?.CanExecute(placement) == true)
            {
                NotePlacedCommand.Execute(placement);
            }

            return;
        }

        var ctrl = CtrlOrCmd(e.KeyModifiers);

        if (HighlightOverlayDrawing.HitTestNote(Notes, point, Bounds.Width, Bounds.Height) is { } note)
        {
            e.Handled = true;

            // Ctrl/Cmd-click toggles the note in the multi-selection (ADR "Annotation multi-select").
            if (ctrl)
            {
                ToggleSelect(note.Id);
                return;
            }

            // A press on the bottom-right grip resizes the box (ADR "Post-it note boxes").
            if (note.CanEdit && HighlightOverlayDrawing.InNoteGrip(note, point, Bounds.Width, Bounds.Height))
            {
                _resizingNote = note;
                _resizePoint = point;
                e.Pointer.Capture(this);
                return;
            }

            // A double-click opens the edit dialog (text / colour / delete).
            if (e.ClickCount >= 2)
            {
                if (NoteClickedCommand?.CanExecute(note.Id) == true)
                {
                    NoteClickedCommand.Execute(note.Id);
                }

                return;
            }

            // A plain press on a note that's part of a multi-selection begins a group drag of the whole
            // selection (ADR "Annotation multi-select").
            if (note.Selected && SelectedCount() > 1)
            {
                _groupPending = note;
                _groupStart = _groupCurrent = point;
                _groupDragging = false;
                e.Pointer.Capture(this);
                return;
            }

            // Otherwise select just this note and defer to release so we can tell a plain press from a drag
            // (reposition). Capture the pointer so the drag survives leaving the box; record the grab offset.
            SelectOne(note.Id);
            var topLeft = HighlightOverlayDrawing.MeasureNote(note, Bounds.Width, Bounds.Height).Rect.TopLeft;
            _pressedNote = note;
            _pressPoint = point;
            _dragPoint = point;
            _grabOffset = point - topLeft;
            _noteDragging = false;
            e.Pointer.Capture(this);
            return;
        }

        if (HighlightOverlayDrawing.HitTestShape(Notes, point, Bounds.Width, Bounds.Height) is { } shape)
        {
            e.Handled = true;

            // Ctrl/Cmd-click toggles the shape in the multi-selection.
            if (ctrl)
            {
                ToggleSelect(shape.Id);
                return;
            }

            // A press on a box shape's corner grip resizes it (ADR "Highlighting redesign").
            if (shape.CanEdit && HighlightOverlayDrawing.InShapeGrip(shape, point, Bounds.Width, Bounds.Height))
            {
                _resizingShape = shape;
                _shapeResizePoint = point;
                e.Pointer.Capture(this);
                return;
            }

            // A plain press on a selected shape in a multi-selection begins a group drag.
            if (shape.Selected && SelectedCount() > 1)
            {
                _groupPending = shape;
                _groupStart = _groupCurrent = point;
                _groupDragging = false;
                e.Pointer.Capture(this);
                return;
            }

            // Otherwise select just this shape and defer to release: a drag moves it, a plain click just selects
            // (a shape has no edit dialog — recolour via the toolbar palette, delete via select + delete).
            SelectOne(shape.Id);
            _pressedShape = shape;
            _shapePressPoint = point;
            _shapeDragPoint = point;
            _shapeDragging = false;
            e.Pointer.Capture(this);
            return;
        }

        var box = HighlightOverlayDrawing.HitTest(Words, point, Bounds.Width, Bounds.Height);
        if (box is null)
        {
            // A press over empty page area starts a potential marquee (ADR "Annotation multi-select"); a drag
            // rubber-band-selects, a plain click clears the selection. Decided on release.
            e.Handled = true;
            _marqueePending = true;
            _marqueeing = false;
            _marqueeStart = _marqueeCurrent = point;
            e.Pointer.Capture(this);
            return;
        }

        e.Handled = true;
        var append = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _ = CopyAsync(box.Text, append);

        if (focusedTextBox is not null && focusedTextBox.Tag as string != "find")
        {
            var (text, caret) = HighlightOverlayDrawing.InsertWordInto(focusedTextBox.Text ?? "", focusedTextBox.SelectionStart, focusedTextBox.SelectionEnd, box.Text, append);
            focusedTextBox.Text = text;
            focusedTextBox.CaretIndex = caret;
        }
    }

    // Finish a note press: a drag persists the new position (NoteMovedCommand), a plain press opens the note
    // (NoteClickedCommand). See OnPointerPressed's note branch.
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        // Finish drawing a markup shape (ADR "Annotation markup") — fire ShapeDrawnCommand unless it was too small.
        if (_drawing)
        {
            _drawing = false;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            var w = _drawCurrent.X - _drawStart.X;
            var h = _drawCurrent.Y - _drawStart.Y;
            InvalidateVisual();
            if ((Math.Abs(w) > 0.01 || Math.Abs(h) > 0.01) && DrawKind > 0)
            {
                var draw = new ShapeDraw(PageIndex, DrawKind, _drawStart.X, _drawStart.Y, w, h);
                if (ShapeDrawnCommand?.CanExecute(draw) == true)
                {
                    ShapeDrawnCommand.Execute(draw);
                }
            }

            return;
        }

        // Finish a note-box resize — persist the new size, clamping the height to what the text needs at the new
        // width so the full text always fits (ADR "Post-it note boxes").
        if (_resizingNote is { } rn && Bounds is { Width: > 0, Height: > 0 })
        {
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            _resizingNote = null;
            InvalidateVisual();

            var newW = Math.Max(HighlightOverlayDrawing.NoteMinWidthPx, _resizePoint.X - rn.X * Bounds.Width) / Bounds.Width;
            var fitHeightNorm = HighlightOverlayDrawing.MeasureNote(rn with { Width = newW, Height = 0 }, Bounds.Width, Bounds.Height).Rect.Height / Bounds.Height;
            var newH = Math.Max(fitHeightNorm, Math.Max(0, _resizePoint.Y - rn.Y * Bounds.Height) / Bounds.Height);
            var resize = new NoteResize(rn.Id, PageIndex, newW, newH);
            if (NoteResizedCommand?.CanExecute(resize) == true)
            {
                NoteResizedCommand.Execute(resize);
            }

            return;
        }

        // Finish a box-shape resize (ADR "Highlighting redesign") — the start point is kept, the extent follows
        // the cursor. Reuses NoteResizedCommand (which sets Width/Height, keeping the position).
        if (_resizingShape is { } rs && Bounds is { Width: > 0, Height: > 0 })
        {
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            _resizingShape = null;
            InvalidateVisual();

            var newW = Math.Max(0.01, _shapeResizePoint.X / Bounds.Width - rs.X);
            var newH = Math.Max(0.01, _shapeResizePoint.Y / Bounds.Height - rs.Y);
            var resize = new NoteResize(rs.Id, PageIndex, newW, newH);
            if (NoteResizedCommand?.CanExecute(resize) == true)
            {
                NoteResizedCommand.Execute(resize);
            }

            return;
        }

        // Finish a single-shape drag (ADR "Highlighting redesign") — the start point moves by the drag delta,
        // extent preserved. Reuses NoteMovedCommand (which sets the position, keeping Width/Height). A plain
        // click just leaves it selected (no dialog).
        if (_pressedShape is { } ps && Bounds is { Width: > 0, Height: > 0 })
        {
            e.Pointer.Capture(null);
            var shapeWasDragged = _shapeDragging;
            _pressedShape = null;
            _shapeDragging = false;
            Cursor = Cursor.Default;
            InvalidateVisual();

            if (shapeWasDragged)
            {
                var nx = Math.Clamp(ps.X + (_shapeDragPoint.X - _shapePressPoint.X) / Bounds.Width, 0, 1);
                var ny = Math.Clamp(ps.Y + (_shapeDragPoint.Y - _shapePressPoint.Y) / Bounds.Height, 0, 1);
                var move = new NoteMove(ps.Id, PageIndex, nx, ny);
                if (NoteMovedCommand?.CanExecute(move) == true)
                {
                    NoteMovedCommand.Execute(move);
                }
            }

            return;
        }

        // Finish a group drag (ADR "Annotation multi-select"): a drag moves the whole selection by the delta; a
        // plain press collapses the selection to the pressed item.
        if (_groupPending is { } gp)
        {
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            var wasDragging = _groupDragging;
            _groupPending = null;
            _groupDragging = false;
            InvalidateVisual();

            if (wasDragging && Bounds is { Width: > 0, Height: > 0 })
            {
                var dx = (_groupCurrent.X - _groupStart.X) / Bounds.Width;
                var dy = (_groupCurrent.Y - _groupStart.Y) / Bounds.Height;
                var groupMove = new AnnotationGroupMove(PageIndex, dx, dy);
                if (GroupMovedCommand?.CanExecute(groupMove) == true)
                {
                    GroupMovedCommand.Execute(groupMove);
                }
            }
            else
            {
                SelectOne(gp.Id);
            }

            return;
        }

        // Finish a marquee (ADR "Annotation multi-select"): a rubber-band selects the enclosed annotations; a
        // plain click on empty space clears the selection.
        if (_marqueePending)
        {
            e.Pointer.Capture(null);
            var wasMarquee = _marqueeing;
            _marqueePending = false;
            _marqueeing = false;
            InvalidateVisual();

            if (wasMarquee)
            {
                var rect = new Rect(_marqueeStart, _marqueeCurrent);
                var ids = AnnotationsInRect(rect);
                var marquee = new MarqueeSelect(PageIndex, ids, Additive: CtrlOrCmd(e.KeyModifiers));
                if (MarqueeSelectCommand?.CanExecute(marquee) == true)
                {
                    MarqueeSelectCommand.Execute(marquee);
                }
            }
            else if (ClearSelectionCommand?.CanExecute(null) == true)
            {
                ClearSelectionCommand.Execute(null);
            }

            return;
        }

        if (_pressedNote is not { } note)
        {
            return;
        }

        e.Pointer.Capture(null);
        var dragging = _noteDragging;
        _pressedNote = null;
        _noteDragging = false;
        Cursor = Cursor.Default;
        InvalidateVisual();

        // A drag repositions the box (the grabbed spot follows the cursor). A plain single click does nothing —
        // the text is always visible; editing is a double-click (handled in OnPointerPressed).
        if (dragging)
        {
            var topLeft = _dragPoint - _grabOffset;
            var nx = Bounds.Width > 0 ? Math.Clamp(topLeft.X / Bounds.Width, 0, 1) : note.X;
            var ny = Bounds.Height > 0 ? Math.Clamp(topLeft.Y / Bounds.Height, 0, 1) : note.Y;
            var move = new NoteMove(note.Id, PageIndex, nx, ny);
            if (NoteMovedCommand?.CanExecute(move) == true)
            {
                NoteMovedCommand.Execute(move);
            }
        }
    }


    private async Task CopyAsync(string word, bool append)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            var text = word;
            if (append)
            {
                var current = await clipboard.TryGetTextAsync();
                text = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
            }

            await clipboard.SetTextAsync(text);

            var command = WordCopiedCommand;
            var result = new HitCopyResult(word, append);
            if (command?.CanExecute(result) == true)
            {
                command.Execute(result);
            }
        }
        catch
        {
            // Clipboard access can fail on some platforms — copying is best-effort.
        }
    }
}
