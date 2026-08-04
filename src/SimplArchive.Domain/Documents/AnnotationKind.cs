namespace SimplArchive.Domain.Documents;

// The kind of a DocumentAnnotation (ADR "Annotation markup: highlight + shapes"; extended to cover the full set of
// imported annotation types, ADR 0525). A Note is a sticky note (a point + text); the box kinds carry a normalized
// extent (Width/Height); Freehand carries a stroke path in Points instead. SimplArchive's own English naming — the
// values are append-only so stored data + the demo seed keep their meaning.
//   • Highlight / Rectangle / Strikethrough — a box with top-left (PositionX,PositionY) + size (Width,Height) ≥ 0.
//   • Arrow — a line from (PositionX,PositionY) to (PositionX+Width, PositionY+Height); Width/Height signed.
//   • Stamp / TextBox — a box (like a Rectangle) carrying Text (the stamp caption / the text-box content).
//   • Freehand — a pen stroke: a polyline of normalized points in Points ("x,y x,y …"); no extent, no text.
public enum AnnotationKind
{
    Note = 0,
    Highlight = 1,
    Rectangle = 2,
    Arrow = 3,
    Stamp = 4,
    Strikethrough = 5,
    TextBox = 6,
    Freehand = 7,
}
