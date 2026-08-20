namespace SimplArchive.Application.Abstractions;

/// <summary>
/// Turns a stored <c>.vcf</c>/<c>.ics</c> into an HTML card the preview pipeline can convert to a PDF.
/// </summary>
/// <remarks>
/// <para>
/// It exists as an ABSTRACTION rather than as a method on the rendition service because the parsing belongs to
/// the Api layer, where the vCard/iCalendar composers already live, and Infrastructure may not depend on Api
/// (the layering is asserted by ArchitectureTests). So Infrastructure asks the question and Api answers it.
/// </para>
/// <para>
/// <b>Why a rendered card rather than the file's own text.</b> A vCard IS text, so serving it as
/// <c>text/plain</c> would be the cheap answer — and it is the wrong one: a card carrying a picture is mostly a
/// base64 blob, so the "preview" would be several screens of encoded bytes with the name somewhere above them.
/// An iCalendar has the same shape of problem in a smaller way (<c>DTSTART;TZID=…</c> is not what anyone is
/// asking). The reader wants the contact, not the encoding.
/// </para>
/// </remarks>
public interface IStructuredItemRenderer
{
    /// <summary>Whether this renderer handles <paramref name="extension"/> (a lower-cased <c>.vcf</c>/<c>.ics</c>).</summary>
    bool Handles(string extension);

    /// <summary>
    /// An HTML document for the stored item, or null when it cannot be parsed.
    /// </summary>
    /// <remarks>
    /// Null rather than a throw for an unparseable file: the preview pipeline treats "no rendition" as "no
    /// preview available", which is exactly the right answer for a malformed card and is what every other
    /// converter's failure already produces.
    /// </remarks>
    string? ToHtml(string content, string extension);
}
