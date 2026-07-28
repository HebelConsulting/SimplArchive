namespace SimplArchive.Domain.Documents;

// The kind of a DocumentAnnotation (ADR "Annotation markup: highlight + shapes"). A Note is the original
// sticky note (a point + text); the markup kinds carry a normalized extent (Width/Height) instead:
//   • Highlight / Rectangle — a box with top-left (PositionX,PositionY) + size (Width,Height) ≥ 0.
//   • Arrow — a line from (PositionX,PositionY) to (PositionX+Width, PositionY+Height); Width/Height signed.
public enum AnnotationKind
{
    Note = 0,
    Highlight = 1,
    Rectangle = 2,
    Arrow = 3,
}
