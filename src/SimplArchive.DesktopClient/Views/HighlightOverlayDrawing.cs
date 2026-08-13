using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using SimplArchive.DesktopClient.ViewModels;

namespace SimplArchive.DesktopClient.Views;

// The stateless drawing layer of the preview overlay (issue #466 moved it out of HighlightOverlay): its style constants, normalized-box
// measurement, note/shape hit-testing, the word-insertion text edit, and the shape/text/colour drawing primitives. Pure functions over the
// page size — no control state, which is what made them movable; the pointer state machine and rendering pass
// that USE them stay in the control, whose fields they share.
internal static class HighlightOverlayDrawing
{
    internal static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xD5, 0x00));

    internal static readonly IPen Stroke = new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xE0, 0xA4, 0x00)), 1);

    // Light grey for the word under the cursor (drawn under the yellow hits, so a hit stays yellow on hover).
    internal static readonly IBrush HoverFill = new SolidColorBrush(Color.FromArgb(0x3A, 0x90, 0x90, 0x90));

    internal static readonly IPen HoverStroke = new Pen(new SolidColorBrush(Color.FromArgb(0x99, 0x60, 0x60, 0x60)), 1);

    // Orange for the "current match" (find prev/next) — drawn on top so it stands out among the yellow hits.
    internal static readonly IBrush ActiveFill = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0x8C, 0x00));

    internal static readonly IPen ActiveStroke = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0xD2, 0x6E, 0x00)), 2);

    // Multi-select (ADR "Annotation multi-select"): a click/Ctrl-click on an annotation, a marquee over empty
    // page area, clearing the selection (a plain click on empty), and a group drag of the whole selection.
    internal static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.FromArgb(0xFF, 0x2f, 0x6f, 0xed)), 2) { DashStyle = new DashStyle([3, 2], 0) };

    internal static readonly IBrush MarqueeFill = new SolidColorBrush(Color.FromArgb(0x22, 0x2f, 0x6f, 0xed));

    internal static readonly IPen MarqueePen = new Pen(new SolidColorBrush(Color.FromArgb(0xAA, 0x2f, 0x6f, 0xed)), 1) { DashStyle = new DashStyle([2, 2], 0) };

    internal static readonly IPen NoteStroke = new Pen(new SolidColorBrush(Color.FromArgb(0xB0, 0x00, 0x00, 0x00)), 1);

    // Inserts a clicked word into a text field at the caret (replacing any selection). Shift prepends a space
    // (unless at the start or already after whitespace). Pure string logic, so it's testable without a display.
    public static (string Text, int Caret) InsertWordInto(string text, int selectionStart, int selectionEnd, string word, bool append)
    {
        var from = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        var to = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);
        var prefix = append && from > 0 && !char.IsWhiteSpace(text[from - 1]) ? " " : "";
        var insert = prefix + word;
        return (text[..from] + insert + text[to..], from + insert.Length);
    }

    // Sticky-note boxes on this page (ADR "Document annotations" / "Post-it note boxes") — an always-visible
    // coloured box showing the note text, drawn + hit-tested + resizable.
    internal const double NotePad = 6;          // px inner padding

    internal const double NoteFontSize = 12;    // px

    internal const double NoteMinWidthPx = 90;  // px minimum box width

    internal const double NoteGripPx = 14;      // px bottom-right resize-grip zone

    internal static readonly IBrush NoteTextBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0x22, 0x22));

    internal static readonly IPen NoteBorder = new Pen(new SolidColorBrush(Color.FromArgb(0x88, 0x00, 0x00, 0x00)), 1);

    internal static readonly IBrush NoteGripBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x00, 0x00, 0x00));

    // The pixel bounding box of a markup shape (box shapes: highlight/rectangle; also used for the arrow bbox).
    internal static Rect ShapeBounds(NoteBox s, double w, double h) =>
        new(Math.Min(s.X, s.X + s.Width) * w, Math.Min(s.Y, s.Y + s.Height) * h, Math.Abs(s.Width) * w, Math.Abs(s.Height) * h);

    // Whether a point is on an editable box shape's bottom-right resize grip (arrows have no grip — move-only).
    internal static bool InShapeGrip(NoteBox shape, Point point, double w, double h)
    {
        if (shape.Kind is not (1 or 2) || !shape.CanEdit)
        {
            return false;
        }

        var r = ShapeBounds(shape, w, h);
        return new Rect(r.Right - NoteGripPx, r.Bottom - NoteGripPx, NoteGripPx, NoteGripPx).Contains(point);
    }

    // The pixel rect of a note box (ADR "Post-it note boxes"): top-left at (X,Y); width = persisted (min
    // NoteMinWidthPx); height auto-grown to fit the wrapped text (min), so the full text always shows. Also
    // returns the FormattedText so Render can draw it without re-measuring.
    internal static (Rect Rect, FormattedText Text) MeasureNote(NoteBox note, double pw, double ph)
    {
        var w = Math.Max(NoteMinWidthPx, note.Width * pw);
        var ft = new FormattedText(
            string.IsNullOrEmpty(note.Text) ? " " : note.Text,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, NoteFontSize, NoteTextBrush)
        {
            MaxTextWidth = Math.Max(10, w - 2 * NotePad),
        };
        var fitHeight = ft.Height + 2 * NotePad;
        var h = Math.Max(fitHeight, note.Height * ph);
        return (new Rect(note.X * pw, note.Y * ph, w, h), ft);
    }

    // Which note box (if any) a point falls in — the box rect (ADR "Post-it note boxes").
    internal static NoteBox? HitTestNote(IEnumerable<NoteBox>? notes, Point point, double width, double height)
    {
        if (notes is null || width <= 0 || height <= 0)
        {
            return null;
        }

        return notes.FirstOrDefault(n => n.Kind == 0 && MeasureNote(n, width, height).Rect.Contains(point));
    }

    // Whether a point is on an editable note box's bottom-right resize grip.
    internal static bool InNoteGrip(NoteBox note, Point point, double width, double height)
    {
        var r = MeasureNote(note, width, height).Rect;
        return note.CanEdit && new Rect(r.Right - NoteGripPx, r.Bottom - NoteGripPx, NoteGripPx, NoteGripPx).Contains(point);
    }

    // Which markup shape (if any) a point falls in — the shape's padded bounding box (ADR "Annotation markup").
    internal static NoteBox? HitTestShape(IEnumerable<NoteBox>? notes, Point point, double width, double height)
    {
        if (notes is null || width <= 0 || height <= 0)
        {
            return null;
        }

        return notes.FirstOrDefault(n =>
            n.Kind > 0 &&
            new Rect(Math.Min(n.X, n.X + n.Width) * width - 4, Math.Min(n.Y, n.Y + n.Height) * height - 4,
                Math.Abs(n.Width) * width + 8, Math.Abs(n.Height) * height + 8).Contains(point));
    }

    // Which box (if any) a point in this control's coordinate space falls in. Pure geometry, so it's directly
    // testable without a display.
    public static HighlightBox? HitTest(IEnumerable<HighlightBox>? boxes, Point point, double width, double height)
    {
        if (boxes is null || width <= 0 || height <= 0)
        {
            return null;
        }

        return boxes.FirstOrDefault(b =>
            new Rect(b.X * width, b.Y * height, b.Width * width, b.Height * height).Contains(point));
    }

    // Draws a markup shape from normalized geometry (x,y start / top-left; w,h signed extent) scaled to the page
    // pixels. Kinds: 1 highlight fill, 2 rectangle outline, 3 arrow, 4 stamp (bordered box + centred uppercase
    // bold caption), 5 strikethrough (a mid-height line across the box), 6 text-box (bordered box + its text),
    // 7 freehand (a poly-line from Points) — ADR 0525. Text is used by 4/6; points by 7.
    internal static void DrawShape(DrawingContext ctx, int kind, double x, double y, double w, double h, double pw, double ph, Color color, bool preview, string text = "", string? points = null)
    {
        if (kind == 3)
        {
            var p1 = new Point(x * pw, y * ph);
            var p2 = new Point((x + w) * pw, (y + h) * ph);
            var pen = new Pen(new SolidColorBrush(color), preview ? 1.5 : 2);
            ctx.DrawLine(pen, p1, p2);
            var ang = Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
            const double hd = 11, sp = 0.5;
            var a = new Point(p2.X - hd * Math.Cos(ang - sp), p2.Y - hd * Math.Sin(ang - sp));
            var b = new Point(p2.X - hd * Math.Cos(ang + sp), p2.Y - hd * Math.Sin(ang + sp));
            ctx.DrawGeometry(new SolidColorBrush(color), null, new PolylineGeometry([p2, a, b], true));
            return;
        }

        // Freehand (ADR 0525): a poly-line built from the normalized "x,y x,y …" points, scaled to page pixels.
        // It has no box extent, so the x/y/w/h are ignored here.
        if (kind == 7)
        {
            var pts = ParsePoints(points, pw, ph);
            if (pts.Count >= 2)
            {
                var pen = new Pen(new SolidColorBrush(color), 1.5) { LineJoin = PenLineJoin.Round, LineCap = PenLineCap.Round };
                ctx.DrawGeometry(null, pen, new PolylineGeometry(pts, false));
            }

            return;
        }

        var rect = new Rect(Math.Min(x, x + w) * pw, Math.Min(y, y + h) * ph, Math.Abs(w) * pw, Math.Abs(h) * ph);
        switch (kind)
        {
            case 1: // highlight — a translucent fill in the annotation colour
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(preview ? (byte)0x40 : (byte)0x60, color.R, color.G, color.B)), null, rect, 2, 2);
                break;
            case 5: // strikethrough — a horizontal line through the box's vertical middle, in the annotation colour
                var midY = rect.Y + rect.Height / 2;
                ctx.DrawLine(new Pen(new SolidColorBrush(color), 2), new Point(rect.X, midY), new Point(rect.Right, midY));
                break;
            case 4: // stamp — a bordered box with a centred, uppercase, bold caption in the annotation colour
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(color), 2), rect, 3, 3);
                DrawBoxText(ctx, rect, text, color, bold: true, centre: true, upper: true);
                break;
            case 6: // text-box — a bordered box on a translucent-white ground showing its text
                ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), new Pen(new SolidColorBrush(color), 1), rect);
                DrawBoxText(ctx, rect, text, Color.FromArgb(0xFF, 0x22, 0x22, 0x22), bold: false, centre: false, upper: false);
                break;
            default: // 2 — rectangle outline
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(color), 2), rect);
                break;
        }
    }

    // Parses a normalized "x,y x,y …" poly-line (each coord 0..1, invariant-culture) into page-pixel points.
    internal static List<Point> ParsePoints(string? points, double pw, double ph)
    {
        var result = new List<Point>();
        if (string.IsNullOrWhiteSpace(points))
        {
            return result;
        }

        foreach (var pair in points.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var xy = pair.Split(',');
            if (xy.Length == 2 &&
                double.TryParse(xy[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px) &&
                double.TryParse(xy[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var py))
            {
                result.Add(new Point(px * pw, py * ph));
            }
        }

        return result;
    }

    // The pixel bounding box of a freehand poly-line (ADR 0525) — used to outline it when selected.
    internal static Rect FreehandBounds(string? points, double pw, double ph)
    {
        var pts = ParsePoints(points, pw, ph);
        if (pts.Count == 0)
        {
            return default;
        }

        var bb = new Rect(pts[0], pts[0]);
        foreach (var p in pts)
        {
            bb = bb.Union(new Rect(p, p));
        }

        return bb;
    }

    // Draws a caption inside a shape's box, clipped to it (stamp / text-box, ADR 0525): optionally uppercased,
    // bold, and horizontally centred + vertically centred (stamp) or top-left (text-box).
    internal static void DrawBoxText(DrawingContext ctx, Rect rect, string text, Color color, bool bold, bool centre, bool upper)
    {
        if (string.IsNullOrEmpty(text) || rect.Width < 6 || rect.Height < 6)
        {
            return;
        }

        var content = upper ? text.ToUpperInvariant() : text;
        var typeface = bold ? new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold) : Typeface.Default;
        var ft = new FormattedText(content, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 11, new SolidColorBrush(color))
        {
            MaxTextWidth = Math.Max(4, rect.Width - 6),
            MaxTextHeight = Math.Max(4, rect.Height - 4),
            TextAlignment = centre ? TextAlignment.Center : TextAlignment.Left,
        };
        using (ctx.PushClip(rect))
        {
            var ty = centre ? rect.Y + Math.Max(0, (rect.Height - ft.Height) / 2) : rect.Y + 2;
            ctx.DrawText(ft, new Point(rect.X + 3, ty));
        }
    }

    internal static Color ParseColor(string hex) => TryParseColor(hex, out var c) ? c : Colors.Yellow;

    internal static bool TryParseColor(string hex, out Color color)
    {
        color = Colors.Yellow;
        try { color = Color.Parse(hex); return true; }
        catch { return false; }
    }
}
