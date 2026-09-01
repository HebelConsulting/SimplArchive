using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// What the POINTER does over the preview overlay: hover tracking, click-to-copy a word, placing and selecting
// a note, dragging and resizing one, and the marquee that selects a group.
//
// Split out because this control was 995 lines — five from the limit — and CLAUDE.md's rule is to split when a
// class APPROACHES it rather than to wait for the build to fail. Nearly half the file was these four handlers,
// and they are one subject: every one of them turns a screen point into an annotation decision.
//
// A partial rather than a helper type, and the reason is specific rather than habitual: these are `protected
// override` members of an Avalonia Control. An override cannot live anywhere but the class, so a helper would
// have to keep four stubs here that forward — which is the same file plus indirection. HighlightOverlayDrawing
// is the counter-example beside it: pure geometry, no overrides, and it became a real type.
public sealed partial class HighlightOverlay
{
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
}
