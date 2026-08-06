namespace SimplArchive.Domain.Documents;

// How a text-bearing annotation's text is rendered (ADR 0542) — font, size and the four styles. Owned by
// DocumentAnnotation, and entirely optional: an annotation with no style is exactly what existed before this,
// rendered with the client's defaults.
//
// Applies to the text-bearing kinds (Note, TextBox, Stamp). A shape leaves it unset.
//
// This models the MEANING of the styling in SimplArchive's own idiom — close to CSS, and to what both clients
// already understand — rather than transcribing any external system's encoding. It exists so annotation styling
// can survive a round trip through external-system interop, but nothing of any foreign wire format leaks in
// here: that conversion lives entirely in the interop layer's mapper.
public sealed class AnnotationTextStyle
{
    // The font family name, as CSS would express it (e.g. "Arial", "Comic Sans MS"). Free text rather than a fixed
    // list: SimplArchive is not bound to any one viewer's font catalogue, so a consumer that supports only a
    // subset validates and falls back at ITS boundary rather than constraining what can be stored here.
    public string? FontFamily { get; set; }

    // Font size in pixels. ALWAYS POSITIVE — how the size is measured is SizeBasis's job, not a sign convention.
    public int? FontSizePx { get; set; }

    // What FontSizePx measures. Separated from the number because the two readings differ visibly at the same
    // value, so collapsing them silently changes how text renders (ADR 0542).
    public FontSizeBasis? SizeBasis { get; set; }

    public bool Bold { get; set; }

    public bool Italic { get; set; }

    public bool Underline { get; set; }

    public bool Strikethrough { get; set; }

    // True when nothing is set — used to avoid persisting an all-default style, so "unstyled" stays genuinely null
    // rather than becoming a row of falses that reads as a deliberate choice.
    public bool IsEmpty =>
        FontFamily is null && FontSizePx is null && SizeBasis is null
        && !Bold && !Italic && !Underline && !Strikethrough;
}

// What a font size measures. Windows distinguishes the two via LOGFONT.lfHeight — and so do the external systems
// SimplArchive interoperates with — and the same numeric size renders visibly differently under each:
// CellHeight includes the font's internal leading, CharacterHeight does not.
public enum FontSizeBasis
{
    // The full character cell, including internal leading. LOGFONT expresses this as a POSITIVE lfHeight.
    CellHeight = 0,

    // The character height alone, excluding internal leading. LOGFONT expresses this as a NEGATIVE lfHeight.
    CharacterHeight = 1,
}
