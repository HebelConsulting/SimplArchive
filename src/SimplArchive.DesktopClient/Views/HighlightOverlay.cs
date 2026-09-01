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
public sealed partial class HighlightOverlay : Control
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
